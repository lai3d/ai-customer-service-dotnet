# Every target works on a machine with Docker and no .NET SDK: scripts/dotnet.sh runs the
# SDK in its container when `dotnet` is not on the PATH. The test suite starts a real
# pgvector through Testcontainers either way, so Docker must be running.

DOTNET := ./scripts/dotnet.sh

.PHONY: deps build run test publish lint fmt clean

deps:
	./scripts/fetch-deps.sh

build:
	$(DOTNET) build -c Release

run: deps
	set -a && [ -f .env ] && . ./.env; set +a; $(DOTNET) run --project src/CustomerService -c Release

# The full suite. No API key: everything up to the model call is testable, and
# Testcontainers supplies a real pgvector. Tests that need the embedding model skip when
# `make deps` has not run; CI asserts the model is present so a skip cannot pass there.
test:
	$(DOTNET) test

publish:
	$(DOTNET) publish src/CustomerService -c Release -o publish

lint:
	$(DOTNET) format --verify-no-changes
	$(DOTNET) build -c Release -warnaserror

fmt:
	$(DOTNET) format

clean:
	rm -rf src/CustomerService/bin src/CustomerService/obj tests/CustomerService.Tests/bin tests/CustomerService.Tests/obj publish
