# Every target works on a machine with Docker and no .NET SDK: scripts/dotnet.sh runs the
# SDK in its container when `dotnet` is not on the PATH. The test suite starts a real
# pgvector through Testcontainers either way, so Docker must be running.

DOTNET := ./scripts/dotnet.sh

.PHONY: deps build run test publish lint fmt clean ui-install ui-test ui-build

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

# The operations UI. Node on the host if present; otherwise the node:22-alpine container.
NPM := $(shell command -v npm >/dev/null 2>&1 && echo npm || echo docker run --rm -v "$$PWD/admin-ui":/ui -w /ui node:22-alpine npm)

ui-install:
	cd admin-ui && $(NPM) install --no-audit --no-fund

ui-test: ui-install
	cd admin-ui && $(NPM) run typecheck && $(NPM) test

ui-build: ui-install
	cd admin-ui && $(NPM) run build

clean:
	rm -rf src/CustomerService/bin src/CustomerService/obj tests/CustomerService.Tests/bin tests/CustomerService.Tests/obj publish
