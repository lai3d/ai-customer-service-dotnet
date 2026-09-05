# Repository Guidelines

## Project Structure & Module Organization

`src/CustomerService/Program.cs` wires the application. Folders under `src/CustomerService/`
hold chat orchestration (`Chat`), retrieval and the in-process embedder (`Rag`), provider
clients (`Llm`), the HTTP and SSE edge (`HttpApi`), tools, configuration, storage, cost
accounting and observability. Tests live in `tests/CustomerService.Tests`, one file per
concern; `Support/` holds the Postgres fixture, the scripted model and the fake provider. The
embedded demo UI is `src/CustomerService/web/index.html`. Decision records and measurements
live in `docs/`; bilingual FAQ data lives in `corpus/faq.json`.

## Build, Test, and Development Commands

Use .NET 10 or `scripts/dotnet.sh`, which runs the SDK in its container, and a running Docker
daemon.

- `make deps`: fetch the embedding model.
- `make build`: compile in Release.
- `docker compose up -d postgres jaeger`: start local dependencies.
- `make run`: run the server on port 8082, loading `.env`.
- `make test`: run all tests without a chat API key.
- `make lint`: `dotnet format --verify-no-changes` and a build with warnings as errors.

## Coding Style & Naming Conventions

Standard .NET conventions: PascalCase for public members, camelCase for locals and fields,
records for data, `sealed` by default. Prefer plain classes and explicit interfaces over
framework indirection. Preserve the turn ordering in `ChatService.TurnAsync`: retrieved
passages must never enter conversation memory. Each `IChatModel.StreamAsync` invocation
represents one model call and returns its usage, including on errors.

## Testing Guidelines

xUnit v3 on Microsoft.Testing.Platform. A fake `HttpMessageHandler` for provider protocols,
Testcontainers with real pgvector for database integration, the real ONNX model for
retrieval measurements. Name tests as sentences: `ATurnStoppedByTheToolCapIsNotRecordedAsCompleted`.
Assert observable behaviour at the relevant boundary. CI asserts the model is present so a
skipped measurement cannot pass there.

## Commit & Pull Request Guidelines

History uses descriptive imperative subjects. PRs should explain the problem, the resulting
behaviour and the validation performed. Keep both READMEs' section structures aligned.
Re-run measurements before updating reported numbers.

## Security & Configuration

Copy `.env.example` to `.env`; never commit credentials or print interpolated Compose
configuration. Declare new Compose settings explicitly. Never edit `corpus/faq.json`.
