# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this
repository.

## What this repository is

The .NET third of a trio. The Java implementation is `../ai-customer-service-java` on this
machine and `github.com/lai3d/ai-customer-service-java` publicly; the Go one is
`../ai-customer-service-go` and `github.com/lai3d/ai-customer-service-go`. **It is not a
port of either.** The three share a corpus, a system prompt, a set of measurements and a
method, and the comparison is the point: same system, three runtimes, honest numbers where
they differ.

Rules that follow from that:

- **`corpus/faq.json` is byte-identical to the other two repositories'. Never edit it.** A
  reworded corpus makes every retrieval number incomparable. `TheCorpusIsByteIdenticalToTheSiblingImplementations`
  pins the hash.
- **The system prompt in `ChatService.SystemPrompt` is byte-identical too.** Prompt parity is
  part of what makes a live comparison mean anything.
- **Do not translate Java or Go code.** Where Spring AI needs an advisor and Go needs a
  channel, the .NET answer is whatever is idiomatic here -- an `IAsyncEnumerable`, a
  `Channel<T>`, a `SemaphoreSlim`, a record. Where a hazard the other two have to test for is
  a compile error or unrepresentable here, that asymmetry is a finding; rebuilding their
  shape would erase it.
- **No Microsoft.Extensions.AI in the turn.** It was measured before this repository existed
  (`../dotnet-probe/FINDINGS.md`): it keeps the call boundary, which is to its credit, and it
  is still a layer between the loop and the wire. The turn owns its own loop so that one
  `StreamAsync` is one model call is one bill, and so the tool loop can validate a tool name
  before it becomes a metric label.

## Toolchain

There is no `dotnet` on this machine by design. `scripts/dotnet.sh` runs the .NET 10 SDK in
its container when `dotnet` is not on the PATH, with the Docker socket mounted so the test
suite can start a real pgvector through Testcontainers. Every `make` target goes through it.

```bash
make deps      # the 470 MB embedding model into model-cache/, once
make build
make test      # full suite, no API key; Docker must be running
make run       # from source on :8082, loading .env
docker compose up -d postgres jaeger   # dependencies only
docker compose up -d                   # the whole stack, app included
```

To run one test class through the Microsoft.Testing.Platform runner:

```bash
./scripts/dotnet.sh run --project tests/CustomerService.Tests -- --filter-class CustomerService.Tests.RetrievalTests
./scripts/dotnet.sh run --project tests/CustomerService.Tests -- --filter-class CustomerService.Tests.RetrievalMeasurements --output Detailed
```

Ports avoid the other stacks' on purpose: Postgres 5434, app 8082, Jaeger 16688/4320, and
the Compose project is `ai-customer-service-dotnet`. Container names are global -- two
projects cannot both claim `csagent-postgres`.

## Architecture

One turn is `ChatService.TurnAsync`, and it does five things in an order that is the design:

```
1. memory.Append(user)      the customer's own words, before anything rewrites them
2. retriever.Retrieve       passages returned, not spliced into the message
3. memory.History           windowed at 40
4. the tool loop            one StreamAsync == one model call == one bill
5. memory.Append(assistant) whatever was said, however the turn ended
```

**Never put retrieved passages into memory.** They belong to one request. In the Java
implementation retrieval rewrote the user message and memory stored whatever it was handed,
so the wrong composition wrote every passage into the customer's history and re-sent it
forever -- silently. `RetrievedPassagesNeverEnterMemory` and
`ASecondTurnDoesNotResendTheFirstTurnsPassages` hold the line.

### A turn's events reach the client through three files

`ChatService.TurnAsync` takes a `Func<TurnEvent, ValueTask>` and pushes typed events --
`retrieval`, `tool`, `message`, `usage`, `error` -- rather than returning a string. Adding
or changing one means touching all three of:

```
src/CustomerService/Chat/TurnEvent.cs        the event records and what each carries
src/CustomerService/HttpApi/ChatEndpoints.cs the turn runs as a task, events arrive on a
                                             Channel, and the heartbeat interleaves with them
src/CustomerService/web/index.html           the only consumer that exercises the whole contract
```

The channel is not incidental. It is what makes the turn consumed exactly once while a
heartbeat interleaves, and what gives the response exactly one writer.

`ChatEndpoints.MapChatEndpoints` takes an `ITurner` rather than `ChatService`, so the edge --
validation, status codes, SSE framing -- is tested with no database, no model and no
embedding model. `ChatServiceTests` is where the turn itself is tested, against a real
Postgres.

**`IChatModel.StreamAsync` makes exactly one model call and returns exactly one call's
usage.** The caller sums. On failure it throws `ModelCallException`, whose `Partial` carries
whatever the provider had already reported. Do not add a heuristic that reconstructs call
boundaries from usage frames; the sibling repositories have the frame counts that show why
none is needed.

## Constraints that fail silently

- **Never set `temperature`, `top_p` or `top_k`.** Claude Opus 5 returns HTTP 400 for any of
  them; GPT-5 accepts only its own default. There is no property for one in `ModelRequest`,
  `ModelOptions` or `ChatConfig`, and `NoSamplingParameterIsConfigurable` asserts that stays
  true; `NeitherClientSendsASamplingParameter` reads the wire.
- **A tool result is prompt.** `System.Text.Json` writes enums as integers unless told
  otherwise, and the first live turn sent Claude `{"status":1}`. The model, correctly, said
  it could not translate a coded status and declined to guess. No test noticed, because every
  test read the JSON back through the same serializer. `ToolJson.Options` carries the enum
  converter; `OrderLookupToleratesCaseAndWhitespace` asserts on the string.
- **`ApiEnum.ToString()` returns the JSON encoding, quotes included.** `stopReason !=
  "tool_use"` was always true in the probe that preceded this repository and a loop stopped
  after one call. `AnthropicChatModel.EnumText` strips them; use it for every enum the
  Anthropic SDK hands back.
- **The OpenAI protocol reports no usage in a streamed response** unless the request sets
  `stream_options.include_usage`. The OpenAI SDK sets it itself, and
  `TheOpenAIProtocolAsksForUsageInTheStream` reads the request body to make sure it keeps
  doing so.
- **Prices key on the model the provider reports.** `gpt-5` comes back as
  `gpt-5-2025-08-07`. `chat_unpriced_model_calls_total` exists so a permanently-zero cost
  meter is visible rather than plausible.
- **A zero vector has NaN cosine distance**, and `1 - NaN >= threshold` is false, so a search
  silently returns nothing. Test doubles must return a non-zero vector.
- **Both ASP.NET and ONNX Runtime define `SessionOptions`**, and the Web SDK's implicit
  usings pull the first one in. Qualify the ONNX one.
- **The aspnet base image already has an `app` user.** `useradd` fails with exit code 9;
  `USER app` is what the Dockerfile does.
- **Compose does not inject an undeclared variable.** Anything in `.env.example` must be
  listed in the app service's `environment:`; `DeploymentTests` asserts it.
- **Metrics are tagged by model, never by conversation id.** Per-conversation tags are
  unbounded cardinality -- and so is anything the *model* writes. A tool name is validated
  against the tool table before it can become a metric label or a span name.
- **Never return early from a client `StreamAsync` on error.** Anthropic reports the input
  count at `message_start`, so an abandoned stream has already been billed. Build the result
  from whatever accumulated and throw it inside `ModelCallException`. `LlmClientTests` assert
  this against the real clients over a fake HTTP handler, not in a stub.
- **The model-call span ends before the tools run.** Otherwise tool spans nest under the call
  that asked for them instead of sitting beside it under the turn -- found by reading a trace
  back out of Jaeger.
- **Schema creation takes a Postgres advisory lock.** `CREATE EXTENSION IF NOT EXISTS` is not
  concurrency-safe, and two replicas starting against a cold database crash one of them.
- **A turn holds a per-conversation lock for its whole length.** History read, model call,
  budget record and reply persistence are only coherent together.
- **The demo page dispatches on the SSE `event:` name, never on a payload field.** Chat
  events carry a `type`; a post-commit failure carries problem+json whose `type` is a URI.
- **`README.md` and `README.zh.md` are a pair.** `BothReadmesHaveTheSameSectionStructure`
  compares heading-level sequences, which is the drift that actually happens.

## The operations surface

`/api/admin/v1/**` and `admin-ui/` are the operations admin, built 2026-09-06 with the
frontend deployed separately at the owner's request; `docs/operations-admin.md` records the
decisions and where they differ from the siblings'. Rules:

- **Admin routes exist only when `ADMIN_ENABLED=true`.** Off, they are 404s. Do not add a guard
  that turns them into 401s when disabled.
- **Every mutation takes `expectedVersion`, required.** A stale one is a 409 and writes
  nothing; a broken rule is a 422 and is audited; a 403 is audited with the method and path.
- **The conclusion lives on the resolving `ticket_event`, never on the ticket row.** Reopen
  clears the owner and requires a reason.
- **Reading a conversation writes an audit row.** Nothing edits or deletes `admin_audit`.
- **The turn record opens before the model call and closes in the `finally`.** Opening failure
  fails the turn; closing failure is logged. Cancellation is classified before any step that
  noticed it, or a customer closing the tab is recorded as the database breaking.
- **Enums by name everywhere JSON leaves the process.** `ToolJson.Options` and
  `ChatEndpoints.Json` both carry the converter; the bug has now arrived through tool results,
  the siblings' dates and the admin API. Any new serializer options must too.
- **The UI reads only what the API sends.** No `dangerouslySetInnerHTML`; the markdown subset
  builds elements; the session token is a bearer in `sessionStorage`. `admin-ui` has its own
  `npm test` and typecheck, run by the `admin-ui` CI job and `make ui-test`.

## Kubernetes

`k8s/` is applied *unmodified* by `k8s/kind/verify.sh`; that guarantee is what makes the
manifests the ones that were verified. The Secret template lives in `k8s/examples/` so the
directory apply cannot sweep it up. Rules:

- **Every number in `deployment.yaml`'s `resources` came from `k8s/kind/sweep.sh`.** If you
  change the image or the model, re-run the sweep and paste the table; do not edit a number.
- **The API pod has no volumes and a read-only root.** `DOTNET_EnableDiagnostics: "0"` in the
  ConfigMap is what makes that possible; the runtime otherwise wants a socket under `/tmp`.
- **`EMBEDDING_MAX_CONCURRENCY` is explicit in the ConfigMap** because `ProcessorCount`
  follows the cgroup CPU limit and the bound would otherwise move with `resources`.
- **The harness never opens the user's kubeconfig**; it exports its own under `k8s/kind/`.
- **Every ConfigMap key is a variable `.env.example` documents** (plus `DOTNET_*` runtime
  knobs); `DeploymentTests` asserts it, so the two places a tunable is described cannot drift.

## Measurements, and how to change one

`docs/` holds one document per decision and every number in it was produced by a test or a
live call. **When a measured value changes, re-run the measurement and update it -- do not
edit the number.**

| Measurement | Where |
| --- | --- |
| 74/74 token-id cases identical to the Rust tokenizer | `TokenizerTests` |
| 20/20 paraphrases, 4/4 cross-lingual, scores equal to the siblings' | `RetrievalTests` |
| No threshold separates the three score populations | `NoSimilarityThresholdIsUseful` |
| Session start, embed and retrieve timings | `RetrievalMeasurements` (run with `--output Detailed`) |
| The ticket cap holds under concurrency | `TheCapHoldsUnderConcurrentCalls` |
| Throughput, latency, pool and OS threads under 1000 concurrent requests | `make bench` (one process per variant; inside the SDK container) |
| Memory limit sweep and cgroup peaks | `k8s/kind/sweep.sh`, `k8s/kind/verify.sh` footprint block |
| Live turns, usage per call, trace shape | `docs/reliability.md`, `docs/observability.md` |

`RAG_SIMILARITY_THRESHOLD` is **0** and `EMBEDDING_INTRA_OP_THREADS` is **1**; both are
measurements, not omissions -- the second cut the benchmark's p50 by 47% because ONNX
Runtime's per-pass thread pool oversubscribes the cores under concurrent queries. If you
change the embedding model, re-measure the threshold, the dimensions, the corpus embeddings
and the tokenizer fixture together.

## Scope

No authentication, no multi-tenancy, no MCP, no Gemini -- three providers, and
`CHAT_PROVIDER=gemini` fails at startup by name. No admin surface, for the reason the Go
repository gives. No Kubernetes manifests yet and no benchmark yet; both are listed in the
README as not done rather than implied.
