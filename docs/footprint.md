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

### Memory, measured on kind

From each container's own cgroup on a kind cluster, the way the Go and Java numbers were
taken, by `k8s/kind/sweep.sh` and `k8s/kind/verify.sh`:

| | .NET | Go | Java |
| --- | --- | --- | --- |
| `anon` at rest — the real requirement | **637 MiB** | 951 MiB | 1409–1527 MiB |
| `file` at rest — page cache, reclaimable | 17–394 MiB | 124–379 MiB | 10–18 MiB |
| `memory.current` at rest | **660–1046 MiB** | 1082–1337 MiB | 1437–1547 MiB |
| `memory.peak` — what a limit must accommodate | **924–1292 MiB** | 1394–1655 MiB | 2874–2889 MiB |
| OOMKilled at | 896Mi and below | 1152Mi and below | 2560Mi and below |
| Deployed `requests` / `limits` | 1152Mi / 1536Mi | 1536Mi / 2Gi | 3Gi / 4Gi |
| Image | 1.22 GB | 1.1 GB | 1.92 GB |

Three things are worth reading off that table rather than the ratios.

**The process memory is the smallest of the three, and the reason is not known.** All three
hold the same 470 MB fp32 model in the same ONNX Runtime library; the .NET process does it in
637 MiB of anonymous memory, the Go one in 951, the Java one in over 1400. The Java gap has an
explanation (DJL's buffers and the JVM's own heap). The Go–.NET gap of about 310 MiB does not,
yet: both load the same file through the same C library, and where the difference lives —
the binding's copy of the model bytes, arena settings, the runtime's own allocator — has not
been measured. It is reported as a number, not as a story.

**Page cache moves between replicas.** `file` was 394 MiB in one .NET replica and 123 MiB in
the other, then 17–26 MiB in every single-replica sweep row, with `anon` identical
throughout. The kernel charges the model file's pages to whichever cgroup faults them in
first. A comparison that quoted `memory.current` for one replica would be off by a factor
that has nothing to do with the runtime.

**The peak is at boot, and it is 1.4× the resting `anon`.** The Go ratio is 1.24×, the Java
one ~1.9×. Sizing against `anon` alone OOMKills at 896Mi — 260 MiB above `anon` — because
the page cache churn of reading a 470 MB file cannot all be reclaimed in time during
startup. The request has to cover the peak.

### Startup

| | |
| --- | --- |
| Time to `/readyz` returning 200, in Compose | **~4 s** after the container starts |
| Container start to `Ready`, on kind under a 2-CPU limit | **4 s**, quantised to the 2 s probe period; 6.9–7.5 s of CPU consumed to get there. Go: 4.4 s and 8.0 s of CPU |
| Model session created | 748 ms |
| Corpus embedded and written (36 documents) | 560 ms |

Measured by polling every two seconds, so "~4 s" is an upper bound with a two-second grain.
Inside the kind pod `Environment.ProcessorCount` reads 2 on an 18-CPU node, derived from the
CPU limit, and the embedding bound is the ConfigMap's explicit 4 rather than that 2. Startup is dominated by
ONNX Runtime opening the model, then by the JIT: a Release build with tiered compilation
warms up over the first requests, and the first live turn's retrieval took 28 ms where the
third's took 11.

### What is not measured

The load run is in [the benchmark](benchmark.md), in the SDK container without a CPU limit.
Nothing here says what that burst does under the 2-CPU limit the manifest sets; the
`ProcessorCount` the pod reports (2) is also the pool's minimum thread count there, so the
starvation shape the benchmark describes would be sharper, not gentler.

---

[← Back to the README](../README.md)
