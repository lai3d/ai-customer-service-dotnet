# The thread pool, measured — and what a native call does to it


The Java implementation published a benchmark to justify virtual threads; the Go one ran
the same benchmark to measure goroutines against them. This is the same benchmark again:
**1000 concurrent requests, a stubbed model with a fixed 1000 ms delay, the full production
request path** — validation, conversation memory in Postgres, the turn record, query
embedding, a pgvector search, tool definitions and metrics, over a real Kestrel socket —
and one fresh conversation per request.

```
make bench
```

One process per variant, so a variant's thread-pool growth is not inherited by the next.

**Where it ran, and why that matters before any number does.** The Java and Go rows were
measured natively on the laptop. There is no .NET SDK on this machine by design, so these
rows ran inside the .NET SDK container in Docker Desktop's Linux VM — `.NET 10.0.11 on
Ubuntu 24.04 Arm64`, 18 CPUs — with Postgres in a sibling container. Same silicon, a
different operating system, a hypervisor in between, and an ONNX Runtime build for
`linux-arm64` rather than `osx-arm64`. One query embeds in **5.8 ms** here where the Go
implementation measured **2 ms** on the host. Read the .NET rows against each other; read
them against the Go and Java rows with that in mind.

| runtime | wall | req/s | p50 | p95 | p99 | OS threads |
| --- | --- | --- | --- | --- | --- | --- |
| Java, platform threads | 6254 ms | 160 | 4037 ms | 6118 ms | 6174 ms | 246 |
| Java, virtual threads | 2000 ms | 500 | 1616 ms | 1955 ms | 1986 ms | 52 |
| Go, goroutines | 1667 ms | 600 | 1648 ms | 1663 ms | 1665 ms | 13 → 135 |
| Go, embedding bounded to 18 | 1876 ms | 533 | 1448 ms | 1845 ms | 1871 ms | 13 → 40 |
| Go, embedding stubbed | 1156 ms | 865 | 1128 ms | 1152 ms | 1154 ms | 12 → 27 |
| **.NET, ONNX as shipped: bounded to 18, one intra-op thread** | **2227 ms** | **449** | **1843 ms** | **2212 ms** | **2219 ms** | **22 → 40** |
| .NET, ONNX unbounded, one intra-op thread | 3040 ms | 329 | 2900 ms | 3028 ms | 3036 ms | 21 → 49 |
| .NET, ONNX bounded to 18, runtime-default intra-op threads | 3571 ms | 280 | 3451 ms | 3554 ms | 3566 ms | 38 → 73 |
| .NET, ONNX unbounded, runtime-default intra-op threads | 4078 ms | 245 | 3865 ms | 4032 ms | 4055 ms | 39 → 73 |
| .NET, embedding stubbed | 1644 ms | 608 | 1531 ms | 1628 ms | 1638 ms | 20 → 39 |

Thread-pool threads, sampled every 5 ms from a dedicated thread: 4–5 at rest before every
run, **22 at peak** for the shipped configuration and the stub, 31 for unbounded with one
intra-op thread, **38** for the runtime-default rows. The pool's work queue peaked at 983–1007
items in every row — a thousand requests arrive at once and the pool has five threads.

Run-to-run variance: the unbounded runtime-default row was run four times and landed between
3910 and 4583 ms wall (p50 3758–4401 ms); the stub row three times, 1630–1832 ms. The table
shows the run from the final, complete matrix. Read the ratios.

### The headline is the third .NET row

Two knobs, each measured with the other held fixed:

| | unbounded | bounded to 18 |
| --- | --- | --- |
| runtime-default intra-op threads | p50 3865 ms · 73 threads | p50 3451 ms · 73 threads |
| one intra-op thread | p50 2900 ms · 49 threads | **p50 1843 ms · 40 threads** |

Bounding the embedding concurrency is the Go implementation's finding and it transfers: an
async `SemaphoreSlim` in front of the native call keeps at most eighteen pool threads
blocked inside ONNX Runtime and lets the other 982 requests wait without a thread. On its
own it was worth 12%.

The other knob is .NET-specific in where it lives, and it was worth 47%. ONNX Runtime's
default is to run each forward pass across an intra-op thread pool sized to the core
count. Under one query at a time that is right. Under eighteen concurrent queries it is
eighteen passes each bringing eighteen threads to eighteen cores — the OS thread count in
the default rows says exactly that, 73 against 40 — and the contention costs more than the
parallelism buys on a 384-dimension model whose input is one short sentence.
`EMBEDDING_INTRA_OP_THREADS` is **1** by default now, and that is a measurement. A single
query embeds in 5.8 ms with it and 7.0 ms without; the only thing it slows is the startup
batch, 1.5 s for 36 documents against 0.55 s, paid once.

### Where the threads come from

A request waiting on the stubbed model's `Task.Delay` costs no thread. A request waiting on
Postgres costs no thread. A request waiting for an embedding slot costs no thread. The stub
row holds a thousand in-flight requests on 22 pool threads and 39 OS threads, which is the
Go implementation's 27 plus what the .NET runtime and Kestrel bring.

What costs a thread is the native call itself, and the thread it costs is a **thread-pool
thread**. That is the difference from Go worth stating plainly. A goroutine in a cgo call
blocks an OS thread and the Go scheduler makes another, up to ten thousand; the count runs
away and the work continues. A .NET request in a native call blocks one of the pool's
threads, and the pool injects replacements slowly on purpose — hill climbing, roughly one or
two a second once past the minimum — so the count does not run away and *everything else on
the pool waits*: the continuations of the 982 requests that are not embedding, Kestrel's
own dispatch, the timers. The queue-depth column is that wait. Go's failure mode is threads;
.NET's is latency for bystanders. Bounding fixes the Go shape by capping the count; it fixes
the .NET shape by making the waiting asynchronous, so the bystanders keep their threads.

### A constant delay flatters everything

The same run with the delay drawn from `300 ms + Exp(mean 700 ms)`, capped at 8 s — the
same 1000 ms mean, a median near 785 ms, a tail of several seconds:

| | p50 | p95 | p99 | OS threads |
| --- | --- | --- | --- | --- |
| runtime-default intra-op threads | 2530 ms | 4161 ms | 5294 ms | 38 → 73 |
| one intra-op thread | 1617 ms | 3252 ms | 4417 ms | 22 → 59 |

Wall and requests per second are not reported for these rows because they are not
throughput when the delay is heavy-tailed: a thousand requests finish when the slowest one
does. p50 improves against the constant-delay rows for the same reason it did in Go — most
requests draw a shorter delay than the constant — and the intra-op finding holds under a
realistic spread.

### What this is not

The model is stubbed, so this measures scheduling rather than an assistant. It ran in a
virtual machine, so the absolute numbers are not the host's. And the embedding work is
real work: the best .NET row still takes 2.2 s for an operation that sleeps for 1.0 s,
because a thousand queries at 5.8 ms each on eighteen cores is 320 ms of CPU even with
perfect packing, and the rest is the packing.

Two measurement mistakes are recorded because they changed the numbers:

**The first runs failed two to six requests with a 500, and the table would have been of a
service that was not working.** The cause was `53300: sorry, too many clients already` —
Npgsql's default pool of 100 connections per data source meeting Postgres's default
`max_connections` of 100 in the test container. The production connection string bounds the
pool at 20; the test fixture's did not. It does now, so a test that opens a thousand turns
meets the same limit the service does. The harness also captures what the server logged
next to each failed request, because a `500` without its cause is a number without a
measurement.

**The sampler is a dedicated thread.** A sampler that was itself a pool work item would be
starved by the starvation it was sampling, and would report a calm pool.

---

[← Back to the README](../README.md)
