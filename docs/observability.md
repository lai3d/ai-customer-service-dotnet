# Observability


Metrics on `/metrics` through prometheus-net, traces over OTLP through the OpenTelemetry
SDK. `docker compose up` starts Jaeger alongside the app and points the exporter at it; the
UI is at **http://localhost:16688**. Export is off by default, so `make run` on its own does
not fill the log with failed exports.

### Why traces matter more here than in an ordinary service

A single turn is retrieval, then a model call, then possibly a tool call and a second model
call. Metrics can tell you a turn took five seconds; only a trace tells you which of those it
was. A real turn, read out of Jaeger:

```
POST /api/v1/chat                        5320 ms
└─ chat turn                             5308 ms
   ├─ retrieve                             11 ms
   │  ├─ embed query                        4 ms
   │  └─ pgvector similarity search         5 ms
   ├─ chat claude-opus-5                 2165 ms   in=1733 out=70   tool_use
   ├─ tool lookup_order_status              4 ms   found
   └─ chat claude-opus-5                 3116 ms   in=1923 out=191  end_turn
```

Retrieval is 11 ms of a 5.3-second turn. Everything else is the model, and it is *two*
model calls, because the first one asked for a tool — visible here as two spans, each
carrying its own `gen_ai.usage.*`, and the same shape the token accounting in
[Cost and failure](reliability.md#a-turn-is-not-a-model-call) rests on.

Attribute names follow OpenTelemetry's GenAI semantic conventions — `gen_ai.system`,
`gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.usage.input_tokens`,
`gen_ai.usage.output_tokens`, `gen_ai.response.finish_reasons`. Spans are
`System.Diagnostics.Activity` from one `ActivitySource`; when nothing is listening,
`StartActivity` returns null and the turn carries on with no trace id in its `usage` event.

### The tool span was in the wrong place, and only the trace said so

The first trace read back out of Jaeger had `tool lookup_order_status` nested *under* the
first model-call span rather than beside it. The reason was a `using var` on the call span
declared in the loop body, which kept the span current while the tools ran. Nothing was
wrong in any test: the spans all existed, the attributes were all correct, the durations
added up. Only the tree was wrong, and only a backend shows the tree. The call span now ends
before the tools run.

### The customer's words are not in the trace, and that was checked rather than assumed

Nothing in this codebase puts customer text on a span: not the question, not the reply, not
the tool arguments the model wrote from what the customer said. The conversation id is there;
the content is not. The check is the Go implementation's: send turns containing text that
must not leak, then search what actually arrived at the backend.

```
POST /api/v1/chat         {"message":"Where is my order ORD-10042?"}
POST /api/v1/chat/stream  {"message":"我的订单 ORD-10042 什么时候到？退货有时间限制吗"}

$ curl -s http://localhost:16688/api/traces/$TRACE_ID > trace.json
ORD-10042 … False    降噪 … False    退货 … False    headphones … False    什么时候到 … False
```

Zero, including the order number that was in the tool's arguments and the fragments of the
question that reached the model. What is kept is everything that makes a span useful: top-k,
how many passages came back, the threshold, the dimensions, the model, the token counts, the
finish reason, the tool's outcome, and the timing.

### Attributes are not the only way into a backend

A span name is an aggregated dimension, and so is a metric label. The tool name is written
by the model, and a retrieved passage can carry an instruction to call a tool that does not
exist. The name is validated against the tool table before it can become either `tool <name>`
or `{tool=<name>}`; the literal `unknown` is emitted when it does not match, and the model is
still told the name it asked for, because it needs that to recover.
`AnInventedToolNameNeverBecomesAMetricLabel` sends a 200-character invented name and asserts
exactly one bounded label value.

### Metrics

```
chat_tokens_total{model="claude-opus-5",type="input"}
chat_cost_usd_total{model="claude-opus-5"}
chat_unpriced_model_calls_total{model="gpt-5-2025-08-07"}
chat_model_calls_total{model="claude-opus-5",outcome="success"}
chat_turns_total{outcome="completed"}
chat_turn_duration_seconds{model="claude-opus-5"}
chat_tool_invocations_total{tool="lookup_order_status",outcome="found"}
chat_retrieval_duration_seconds
```

The same names as the Java and Go implementations', so one Grafana dashboard reads all
three. Everything is tagged by model and **never** by conversation id. The Go implementation
also registers `chat_embedding_duration_seconds`; it is absent here on purpose, because
nothing observed it and a metric that is always zero is a claim rather than a measurement.

---

[← Back to the README](../README.md)
