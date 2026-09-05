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

### Not yet verified in a browser

The Go page was driven in a headless Chromium and timed frame by frame; this one has not
been. What has been verified is the wire: the stream from a live turn parsed into
`retrieval`, `tool`, 57 `message` and one `usage` frame, in that order, with the reply in
Chinese for a Chinese question. Font fallback, layout and anything gated on a real display
are unverified here.

---

[← Back to the README](../README.md)
