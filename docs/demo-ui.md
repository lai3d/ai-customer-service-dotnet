# The demo UI


`docker compose up` serves a single page at **http://localhost:8082** — one HTML file,
embedded in the assembly, no build step and no `npm install`.

It is deliberately not a chat widget. A widget's job is to make the model feel seamless and
invisible; this repository's substance *is* the invisible part. So the page is a glass box:
the conversation on the left, and on the right, for every turn, the passages retrieval found
with their scores, the tools the model called and what they decided, how many model calls
the turn billed for, and a link to that turn's trace in Jaeger.

That is only possible because the stream carries typed events rather than bare tokens:

```
event: retrieval    passages, with entry ids, languages and scores
event: tool         name and outcome
event: message      a chunk of the answer
event: usage        model, model calls, tokens, cost, wall time, trace id
event: error        a failure after the response was already committed
```

A production widget would read `message` and `error` and ignore the rest.

### It is the Go implementation's page, and that is deliberate

The file is the Go repository's `index.html` with three edits: the title, the heading and
the Jaeger port. Everything the Go page learned the hard way — rendering the model's
markdown by building DOM nodes rather than through `innerHTML`, never rendering a link the
model wrote, dispatching on the SSE `event:` name rather than on a payload field, relative
score bars, an inline favicon — arrived here already paid for. The four `DemoPageTests`
that pin those properties are the Go ones translated, and they are the reason the page can
be shared: the tests are what say the contract is the same.

Sharing the page is also what makes the SSE contract a measured thing rather than a
described one. The page parses `event:` and `data:` lines and switches on five event names;
`TheStreamCarriesTypedEventsInOrder` asserts the server emits exactly those names, with a
payload `type` that agrees.

### Two model calls are two messages

A tool-calling turn makes two model calls, and if the model says something before asking for
the tool — *"I'll check that for you."* — the second call's text is a new message rather
than a continuation of the first. The stream carries a paragraph break at the boundary, in
the streamed events and in what is persisted, so the next turn does not re-send a
run-together message as history. `TextFromTwoModelCallsIsNotRunTogether` pins it, and the
first live turn against Claude exercised it: the model narrated before calling the tool.

### Verified in a browser, and what it found

Driven in a headless Chrome at 1440×950 by `scripts/drive-demo.mjs` against the Compose
stack, sampling the DOM every 120 ms, on two live turns and one refused request. Two runs
of the order question, because the model chose differently each time:

```
The model narrated first ("I'll look up your order first.")
   161 ms   retrieval card — 8 passages, scores, relative bars
  1146 ms   the first word of the answer — the first model call's sentence
  1888 ms   tool pill — lookup_order_status → found
  5090 ms   usage card — claude-opus-5, model calls 2

The model went straight to the tool
   169 ms   retrieval card
  6533 ms   tool pill
  7512 ms   the first word of the answer
  9600 ms   usage card — model calls 2
```

The second-turn question, in Chinese with nothing for a tool to do: retrieval at 152 ms,
the first word at 2.2–3.2 s, one model call, the answer in Chinese with the earlier order
carried as context. Both turns rendered the model's markdown as DOM — 5 paragraphs, 4 list
items and 2 bold spans on the order turn, no asterisk or hyphen in the visible text — and
the seam between the two model calls was a paragraph break: *"I'll look up your order
first."* then a new paragraph, not `first.Your order`. The score bars ran from 100% down to
18%. The Jaeger link on the usage card resolved to a trace with three spans. No console
error, no failed request other than the one the run provokes.

**What it found: a refusal landed below the fold.** The page scrolls the log when a user
message is appended and as answer chunks arrive. It did not scroll when an error was
appended. A message the server refuses before committing — 4,001 characters against a
4,000 limit — fills the log with the customer's own text, and the *Message too long*
problem was rendered correctly, one line below the visible area. The same holds for a
failure after a long partial answer. Every error now goes through one function that
appends and scrolls. The check that the error bubble sits inside the log's box was red on
the old page before the fix and green after; the Go page, which this one is a copy of,
had the same gap, measured there at 38 pixels below the fold and fixed the same way.

What found the defect needs a browser; what keeps it fixed does not. The Go session's
division, adopted here: `EveryErrorBubbleGoesThroughTheHelperThatScrolls` reads the page's
code and asserts that the helper exists and scrolls and that no branch appends an error
bubble outside it. It was made red both ways before it was trusted — one branch appending
directly again, and the helper without its scroll — and it runs in CI, where a browser does
not. What it cannot see is the log becoming a scrolling document rather than a scrolling
element, where `scrollTop = scrollHeight` on the element silently does nothing; only the
browser's rect check would, which is why that one stays.

**A check that was red for the wrong reason.** The first version of the seam check read
`textContent`, which joins block elements with nothing between them, so a correct page —
two `<p>` elements — read as `first.Your order` and failed. It reads `innerText` now,
where a block boundary is a newline. A red check is a claim about the page, and the first
thing to do with it is find out whether it is the page or the check.

**Not covered.** A failure *after* the response is committed — budget exhausted, provider
error mid-answer — is not provoked live; the server's `error` frame is pinned by
`TheStreamCarriesTypedEventsInOrder` and the page's dispatch by
`TheDemoPageDispatchesOnTheEventName`, and the new scroll goes through the same function,
but no browser has watched that path. Headless, with a throwaway profile: font fallback and
anything gated on a real display are not covered. And this is a script run by hand, not a
CI job, because it spends two real model calls per run.

---

[← Back to the README](../README.md)
