# Retrieval


The FAQ corpus lives in [`corpus/faq.json`](../corpus/faq.json) — 18 entries across
returns, shipping, payment, account and support, each written in English and Chinese. Every
language becomes its own document, so 36 in total. **It is sample data.** Replace it before
this answers anything real.

It is copied byte for byte from the Java and Go implementations of this system, and
`TheCorpusIsByteIdenticalToTheSiblingImplementations` pins its hash: the three repositories
exist to be compared, and a reworded corpus would make every retrieval number below
incomparable.

Ingestion runs at startup and *replaces* what it wrote last time rather than appending.
Duplicates do not merely waste space: they crowd out distinct passages in the top-k window,
so the model sees one answer four times instead of four different ones.

No text splitter, deliberately. An FAQ entry is already the unit a customer's question
should match, and splitting one would separate a question from its answer.

### In-process embedding in .NET: no native build, and a tokenizer to write

Anthropic has no embedding API, so a RAG path either runs a model locally or takes a
dependency on a second vendor. The Go implementation found the local route viable and
paid for it in cgo. Here the bill is shaped differently.

ONNX Runtime arrives through NuGet with native libraries for linux, macOS and Windows on
x64 and arm64, so there is no linker flag, no static library to fetch, and the Dockerfile
has one fewer stage than the Go one. What NuGet does not supply is the tokenizer.
`multilingual-e5-small` is XLM-RoBERTa, whose tokenizer is a SentencePiece Unigram model
behind a precompiled normalisation table; the .NET tokenizer packages load SentencePiece
protobufs rather than HuggingFace's `tokenizer.json`, and this model's ids are offset from
its protobuf's. So [`E5Tokenizer`](../src/CustomerService/Rag/Tokenizer/E5Tokenizer.cs) and
[`PrecompiledCharsMap`](../src/CustomerService/Rag/Tokenizer/PrecompiledCharsMap.cs) are a
port: the Darts double-array trie the normaliser is stored in, the Metaspace pre-tokeniser,
Viterbi over the vocabulary, fused unknowns, and `<s> … </s>`. About 250 lines.

Getting a tokenizer subtly wrong produces plausible vectors and bad rankings rather than an
error, so the check is not a unit test written from the same understanding as the code.
[`tests/CustomerService.Tests/tokenizer-fixture.json`](../tests/CustomerService.Tests/tokenizer-fixture.json)
holds token ids produced by the Rust `tokenizers` library — the one the Go implementation
links against — for the whole corpus with its passage prefix, every measured query with its
query prefix, and inputs chosen to exercise the normaliser: full-width letters, an
ideographic space, enclosed digits, a ligature, emoji, tabs, runs of spaces.
`TokenIdsAreIdenticalToTheRustTokenizersForEveryFixtureCase` asserts identity on all 74.

The first version of the trie walk checked for a leaf on the wrong unit — after the offset
had been applied rather than before — and every case failed with an index out of range
before a single vector was produced. A fixture from a *different* implementation is what
made that a five-minute fix rather than a week of vaguely wrong rankings. 74 of 74 on the
second attempt, and the scores below say the rest.

| | |
| --- | --- |
| Session start (470 MB fp32 model) | 748 ms, once |
| The whole 36-document corpus embedded and written | 560 ms |
| One query embedded | **13.7 ms** median (6.9 min, 33.9 max, n=20) |
| Embed + pgvector search | 22 ms average |

Measured by `RetrievalMeasurements` inside the .NET SDK container on an arm64 laptop. The Go
implementation reports 2 ms per query on the same machine's host, and the two numbers are
**not yet comparable**: one is inside a Linux container and one is not, and neither has been
run beside the other on the same runtime binary. The live trace in
[Observability](observability.md) shows the whole of retrieval at 11 ms of a 5.3-second
turn, which is the number that matters for a customer.

e5 requires asymmetric input markers — `query: ` before a search query, `passage: ` before
an indexed document. They are part of the model contract, and applying them to one side only
is worse than applying neither. Here they are enforced by the type:
[`IEmbedder`](../src/CustomerService/Rag/IEmbedder.cs) has `EmbedQueryAsync` and
`EmbedPassagesAsync` and no `Embed`, so there is no way to embed a query as a passage.

### Retrieval quality

Measured against a real pgvector and the real model on every build, with no API key
([`RetrievalTests`](../tests/CustomerService.Tests/RetrievalTests.cs)). The queries are the
Java and Go implementations', verbatim, so the numbers can be put side by side. They avoid
the corpus wording in both languages: matching a question to its own text proves nothing
about a customer describing a problem in their own words.

- **20 of 20** paraphrased questions, ten English and ten Chinese, retrieve the correct
  entry first.
- A Chinese question matches a Chinese passage, every time.
- **4 of 4** cross-lingual: a Chinese question finds the right English passage when only
  English exists.

**The scores are identical to the Go implementation's to four decimal places**, and that is
the strongest test in this repository:

| query | Go | .NET |
| --- | --- | --- |
| *my parcel showed up broken* → `returns-damaged` | 0.8378 | 0.8378 |
| *你们招聘工程师吗* (off-topic) | 0.8490 | 0.8490 |
| *。。。* (degenerate) | 0.8417 | 0.8417 |

Three independent tokenizer implementations — Java's DJL, Rust's `tokenizers`, and the C#
port here — and one shared ONNX graph landing on the same cosine similarity for the same
query against the same passage is a check none of them could run alone.
`ScoresAgreeWithTheSiblingImplementations` holds it at ±0.0015.

### No similarity threshold is worth setting with this model

The Go implementation re-measured the Java implementation's threshold and found the three
score populations overlapping. Same model, same corpus, same queries, and the same numbers
here:

| | boundary | worst case |
| --- | --- | --- |
| Relevant questions (en + zh) | weakest **0.8378** | *"my parcel showed up broken"* |
| Off-topic questions (en + zh) | strongest **0.8490** | *"你们招聘工程师吗"* |
| Degenerate input | strongest **0.8417** | *"。。。"* |

So the default is `0`, and relevance judgement lives in the system prompt, which tells the
model that reference material is selected by similarity, that some of it will be unrelated,
and to say so rather than stretch an unrelated passage to fit. `NoSimilarityThresholdIsUseful`
asserts the *overlap*, so if a future embedding model separates the populations the test
fails and says to re-measure rather than quietly passing on a claim that has stopped being
true. **Re-measure before setting it. Do not copy this 0 either.**

### What is not measured here

There is no evaluation harness scoring answer quality against a golden set. Everything above
says which passage was found, not whether the answer built from it was good. The `top-k` of 8
is inherited from the Java implementation's recall-against-tokens measurement and is
**labelled here as unverified on this side**. And the embedding latency has not been measured
under load, where the interesting .NET question lives: a thread blocked inside ONNX Runtime
is a thread-pool thread, and the pool's hill-climbing injects replacements slowly.
[`BoundedEmbedder`](../src/CustomerService/Rag/BoundedEmbedder.cs) bounds concurrency at the
processor count on that reasoning, not yet on a measurement.

---

[← Back to the README](../README.md)
