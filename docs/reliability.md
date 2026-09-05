# Cost and failure


An assistant that answers well and bills unpredictably is not finished.

### A turn is not a model call

A tool-calling turn makes at least two model calls — one where the model asks for the tool,
one where it answers with the result — and each is billed. Live tool-calling turns, per call
and summed, one question in each language:

```
claude-opus-5       1733/70   + 1923/191   = 3656/261      Where is my order ORD-10042?
                    1818/60   + 1998/178   = 3816/238      我的订单 ORD-10042 什么时候到？退货有时间限制吗
grok-4.6            1653/18   + 1749/62    = 3402/80
                    1733/18   + 1829/101   = 3562/119
gpt-5-2025-08-07    1000/92   + 1099/809   = 2099/901
                    1066/348  + 1165/1325  = 2231/1673
```

Each line is two `model call finished` log entries, one per call, and the sum is what the
`usage` event and the budget see. [`IChatModel.StreamAsync`](../src/CustomerService/Llm/ChatModel.cs)
makes exactly one model call and returns exactly one call's usage; the turn adds them up. No
rule reconstructs a boundary from usage frames, because the loop never lost it.

### The abstraction leak was one framework's, and it was measured before this repository existed

The Java implementation had to reconstruct that boundary from the numbers, because Spring AI
handed it a flat sequence of usage frames covering a whole turn — 124 identical frames on
one xAI turn. It concluded the loss was Spring AI's doing rather than the protocols', and the
Go implementation confirmed the wire half: Anthropic carries usage on two frames per model
call, OpenAI and xAI on one.

What neither could say was whether a *unified multi-provider chat abstraction* has to lose
the boundary. This repository's first act was a probe that answered that, with .NET's own
such abstraction: `Microsoft.Extensions.AI`'s `IChatClient` plus `UseFunctionInvocation`,
over the same three providers, on the same turn, with every request body and SSE byte
captured. Measured twice:

| provider | updates for the turn | carrying usage | per call | distinct response ids | naive sum vs wire |
| --- | --- | --- | --- | --- | --- |
| Anthropic | 24 | 2 | 1 | 3 | equal |
| xAI | 113 | 2 | 1 | 3 | equal |
| OpenAI | 82 | 2 | 1 | 3 | equal |

One usage per model call, each stamped with its own response id, a tool-role update between
the calls, the two assistant texts kept as separate messages, and no sampling parameter
seeded. So the Java finding narrows to a Spring AI design choice, and it was written that
way in both sibling repositories on the day.

The probe also found the one place the comparison made a constraint *larger*: in .NET
`ChatMessage` is mutable and the pipeline passes the caller's instances down, so a middleware
that appends retrieved passages to the user message in place leaks them into whatever
persists history in *either* composition order — Spring AI's immutable `Prompt` only permits
the ordering mistake. This repository does not use `Microsoft.Extensions.AI` in the turn,
for the reasons in [CLAUDE.md](../CLAUDE.md); the probe's full write-up lives beside the three
repositories in the workspace.

### An abandoned stream has usually already been billed

Anthropic reports the input count at `message_start`, before a single token of the answer,
so a stream that dies half-way through — most often because the customer closed the tab —
has spent real money. A client that threw a bare exception there would throw the number away
one layer below the comment that promises otherwise.

`StreamAsync` never returns early on failure. It builds a result from whatever accumulated
and throws it inside [`ModelCallException.Partial`](../src/CustomerService/Llm/ChatModel.cs),
and the turn records that usage in the budget, the meters and the span before rethrowing.
The asymmetry between protocols is a property of the wire, not of this code: the OpenAI
protocol sends usage in a single final chunk, so a call cut off mid-stream genuinely has
nothing to report and the client returns a zero honestly rather than inventing a number.

The Go implementation shipped this wrong at first and its service-level stub hid it: a stub
implementing the client interface can return usage alongside an error whether or not any
real client does. So [`LlmClientTests`](../tests/CustomerService.Tests/LlmClientTests.cs)
drive the real clients against a fake HTTP handler that serves `message_start` and one token
and then drops the connection, and assert the input count survives. The test is one layer
below the seam where the claim lives, which is the only place it can be checked.

### A conversation is an open-ended bill

A message window bounds any single request; nothing bounds the number of requests. A
customer who keeps typing, or a script that does, runs indefinitely, and the failure is
undramatic: no error, no alert, a larger invoice. A conversation that reaches its token
budget gets a `429` pointing at a human, which is the right outcome for a conversation that
long anyway.

Spend is held in a **bounded** LRU map, per replica, reset on restart. That is honest about
what it is — blast-radius limiting, not a ledger. The bound matters more than it looks: an
unbounded map keyed by conversation id is a memory leak with a long fuse.

### Interactive retry and timeouts, not batch ones

Both SDKs default to ten-minute request timeouts and two retries with their own backoff.
Here: three attempts and a 120-second read timeout, set explicitly. The read timeout is
generous because a long answer legitimately takes time; it guards against a stall, not
against slowness. Kestrel has no response write timeout for the same reason — an SSE
response is legitimately open for as long as the model keeps talking.

### Bound tool side effects in the tool

The system prompt says that retrieved passages, tool results and customer messages are data
rather than instructions. That is worth saying and it is not a defence: a prompt asks, it
does not enforce. What holds is what the tool is allowed to do. `create_support_ticket`
deduplicates per conversation **and** caps at three, both under one lock — checking the count
and then inserting is not the same as doing both atomically. `TheCapHoldsUnderConcurrentCalls`
fires twenty differently worded requests at once and asserts three tickets.

**What the cap is not.** State lives in memory in one process, so two replicas mean two
dedupe tables and an upper bound of `replicas × 3`. A real implementation would put the
idempotency key in Postgres behind a unique constraint.

### A turn that never answered is not a completed turn

`chat_turns_total` distinguishes `completed`, `cancelled`, `failed`, `tool_limit`,
`budget_exceeded`, `retrieval_failed` and `memory_failed`. The block that records the
outcome and persists whatever the model said is a `finally` installed before anything can
fail, so a budget rejection or a retrieval outage shows up as a spike in the first metric
anyone reaches for rather than as silence. `AFailureBeforeTheModelIsStillCountedAsATurn`
and `ATurnStoppedByTheToolCapIsNotRecordedAsCompleted` pin both.

### One turn at a time, per conversation

A turn writes the customer's message, retrieves, reads history, calls the model and writes a
reply, and those steps are only coherent together. Two overlapping requests on one
conversation — two browser tabs is enough — would interleave: the second one's user message
and reply land between the first one's write and its history read, so the first sends the
model a conversation ending in somebody else's answer, and its retrieved passages are dropped
at the same moment.

Turns are serialised per conversation by [`ConversationLocks`](../src/CustomerService/Chat/ConversationLocks.cs):
a `SemaphoreSlim` per conversation in flight, reference-counted so the table is bounded by
requests in progress rather than conversations ever seen, and waited on with the request's
cancellation token so a caller whose client has gone stops queueing.
`OverlappingTurnsOnOneConversationDoNotInterleave` holds the first model call open, starts a
second turn, and asserts it waits. **Single process only**, as in the Go implementation.

### Failures a client can act on

| Failure | Response |
| --- | --- |
| Blank or oversized message, over-long conversation id, malformed JSON | `400`, before any model call |
| Conversation has spent its token budget | `429` — a human agent should take it |
| Provider rate limiting or overloaded (`429`, `5xx`, `529`) | `503` — retrying shortly is worthwhile |
| Provider rejected the request | `502` — retrying will not help |
| Anything else | `500`, logged with the exception; the customer sees no internal text |

After the first byte of a stream the status code is gone, so the same problem arrives as an
`event: error` frame carrying problem+json. `AFailureAfterTheFirstTokenArrivesAsAnErrorEvent`
asserts the frame is named and that its payload's `type` is a URI, never the string
`"error"` — the distinction the demo page depends on.

---

[← Back to the README](../README.md)
