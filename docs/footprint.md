# Footprint


What one instance of this implementation costs to run, measured on the same laptop the Java
and Go numbers were taken on, and labelled with what was and was not measured the same way.

### The image

**1.22 GB**, and it is worth knowing where it goes:

| | |
| --- | --- |
| 470 MB | `model.onnx`, the fp32 export of multilingual-e5-small |
| 276 MB | `mcr.microsoft.com/dotnet/aspnet:10.0` — Debian, the runtime, ASP.NET Core |
| 57 MB | the published application, ONNX Runtime's native library included |
| 17 MB | `tokenizer.json` — a SentencePiece vocabulary is not small either |

Against the siblings: Go 1.1 GB, Java 1.92 GB. The model is 40% of every one of them. The
difference between Go and .NET is the runtime the application needs to carry; the difference
between .NET and Java is the JVM plus the DJL native stack. Calling an embedding API instead
would cut all three to a fraction and add a vendor, a key, and a network round trip per query
— the trade [Retrieval](retrieval.md) has measured rather than argued.

There is no native build stage: ONNX Runtime arrives through NuGet, so the Dockerfile has
three stages where the Go one has four, and nothing has to be compiled against a C library.

### Memory, and how it was measured

`docker stats` after six live turns across three providers: **680–700 MiB**. That is one
number from one tool, and it is not the number the Go and Java repositories report — theirs
come from the cgroup's `memory.current`, `anon` and `peak` on a kind cluster, with the model
file's page cache separated out. This one has not been through that harness, so the honest
comparison is:

| | .NET | Go | Java |
| --- | --- | --- | --- |
| `docker stats` after a few turns | **680–700 MiB** | not published this way | not published this way |
| `anon` at rest, kind, cgroup v2 | not measured | 951 MiB | 1409–1527 MiB |
| `memory.peak` | not measured | 1394–1655 MiB | 2874–2889 MiB |

A resting figure under 700 MiB with a 470 MB model mapped in is consistent with the model
being read into managed or native memory once and the rest of the process being small, and
that is exactly the kind of sentence a measurement is supposed to replace. When this
implementation gets Kubernetes manifests, the Go harness's `anon`/`file`/`peak` sweep is the
method to copy, and the numbers above are the ones to fill in.

### Startup

| | |
| --- | --- |
| Time to `/readyz` returning 200, in Compose | **~4 s** after the container starts |
| Model session created | 748 ms |
| Corpus embedded and written (36 documents) | 560 ms |

Measured by polling every two seconds, so "~4 s" is an upper bound with a two-second grain;
the Go implementation reports 4.4 s on kind under a CPU limit. Startup is dominated by
ONNX Runtime opening the model, then by the JIT: a Release build with tiered compilation
warms up over the first requests, and the first live turn's retrieval took 28 ms where the
third's took 11.

### What is not measured

No CPU limit has been applied, so nothing here says what happens to the thread pool when
the embedder's native call is throttled. No load has been run, so nothing here says what a
burst of a thousand arrivals does to OS threads — the question the Go benchmark answered for
goroutines and the one that matters most for a runtime whose thread pool grows slowly on
purpose. Both are listed in the README as not done.

---

[← Back to the README](../README.md)
