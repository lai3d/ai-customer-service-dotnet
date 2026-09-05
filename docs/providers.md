# Chat providers


The provider is configuration, not code. Everything around the model — memory, retrieval,
both tools, the tool loop, SSE streaming, metrics and spans — is written against
[`IChatModel`](../src/CustomerService/Llm/ChatModel.cs), a three-member interface.

```bash
CHAT_PROVIDER=anthropic  ANTHROPIC_API_KEY=…   # default, claude-opus-5
CHAT_PROVIDER=openai     OPENAI_API_KEY=…      # gpt-5
CHAT_PROVIDER=xai        XAI_API_KEY=…         # grok-4.6
```

Only the selected provider's key is required, and startup fails immediately if it is missing.
A service that starts without credentials, reports itself healthy, is marked ready and then
401s every customer request is the worse failure.

All three are verified live, in both languages: each answers the order question from the
corpus, calls `lookup_order_status` and uses its result, and reports usage that reaches the
budget, the meters and the spans. The per-call numbers are in
[Cost and failure](reliability.md#a-turn-is-not-a-model-call).

### The official SDKs, and nothing in between

Anthropic through the `Anthropic` package's `Messages.CreateStreaming`; OpenAI and xAI
through the `OpenAI` package's `CompleteChatStreamingAsync`. Each client accumulates the
stream itself — blocks by index, tool-call arguments by fragment, usage from the frames that
carry it — so what is counted is what arrived. `Microsoft.Extensions.AI` was considered and
measured first; it keeps the call boundary, and it is still a layer between the loop and the
wire. [Cost and failure](reliability.md#the-abstraction-leak-was-one-frameworks-and-it-was-measured-before-this-repository-existed)
has the measurement.

### No sampling parameters, for any provider

Claude Opus 5 returns HTTP 400 for `temperature`, `top_p` or `top_k` — any of them. GPT-5
accepts only its own default. There is no property in `ModelRequest`, `ModelOptions` or
`ChatConfig` to set one, `NoSamplingParameterIsConfigurable` asserts that by reflection, and
`NeitherClientSendsASamplingParameter` reads the request bodies both clients put on the
wire. That sounds like testing nothing until you know the shape of the bug it prevents:
Spring AI set a temperature in a field initialiser that configuration could not null out.

### xAI is a provider, not a base-URL trick

xAI speaks OpenAI's wire protocol, so reimplementing streaming, tool calling and retry for
Grok would be pure cost. But selecting `openai`, putting an xAI key in `OPENAI_API_KEY` and
overriding the base URL works and lies: the configuration then says OpenAI everywhere while
talking to xAI, and the two cannot be configured side by side.
[`OpenAIProtocolChatModel`](../src/CustomerService/Llm/OpenAIProtocolChatModel.cs) is one
class with two factories, `OpenAI` and `XAI`, differing in the provider name they report and
the credentials, base URL and model they are given. xAI's compatibility is xAI's to maintain;
if they diverge from the protocol, this breaks, and the file says so.

### What only a live call found

**GPT-5 bills its reasoning as output.** A five-line Chinese answer cost 1,325 output tokens
on the second call of its turn, against 178 for Claude's and 101 for Grok's answers to the
same question. Nothing in the response marks them as reasoning; they are simply the output
count. A budget or a price table that assumes output tokens are visible text will be
surprised by this provider and not by the other two.

**The model id in the response is not the one you asked for.** `gpt-5` comes back as
`gpt-5-2025-08-07`. Metrics and prices key on what the provider reports, and
`chat_unpriced_model_calls_total{model="gpt-5-2025-08-07"}` counted four calls in the live
run because the price table has no entry for it — visible rather than a cost meter that
happens to read zero.

**The OpenAI SDK asks for streamed usage itself.** The protocol reports no usage unless the
request sets `stream_options.include_usage`, and the .NET SDK sets it on every streaming
call. `TheOpenAIProtocolAsksForUsageInTheStream` reads the request body so a future SDK
version that stops doing so is a red test rather than a budget that never fires.

**The Anthropic SDK's enum `ToString()` includes the JSON quotes.** `stop_reason` came back
as `"tool_use"` with the quotes, a comparison against `tool_use` was always false, and the
loop stopped after one call. Caught in the probe that preceded this repository by a table of
what the frame counts should have been; `EnumText` strips them everywhere now.

### What this does not claim

Nothing here calls three APIs and compares their answers for quality. The abstraction covers
the request shape; tool-call reliability and streaming granularity differ in ways only live
traffic reveals. **Gemini is not implemented.** The Java implementation of this system
supports it and records what it took; that finding is theirs and is not re-verified here.

---

[← Back to the README](../README.md)
