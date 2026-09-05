# Kubernetes manifests

Namespace, ConfigMap, Service, Deployment, and the operations UI as a second Deployment. No
Postgres and no Secret — see [Before you apply](#before-you-apply).

These were written against the Go implementation's manifests and harness, and the part
worth copying was copied: every number here was measured on a kind cluster before it was
committed, by a script that applies `k8s/` *unmodified*. The Java implementation's first
manifests were committed without ever being applied and two were wrong; the Go one built
the harness in response; this one inherited it.

```
k8s/
├── namespace.yaml
├── configmap.yaml
├── service.yaml
├── deployment.yaml
├── admin-ui.yaml            the operations UI: its own Deployment, Service and ConfigMap
├── examples/secret.yaml     a template, deliberately not in the directory apply path
└── kind/
    ├── postgres.yaml        a Postgres for the throwaway cluster only
    ├── verify.sh            create a cluster, deploy, assert twenty-five things
    └── sweep.sh             sweep the memory limit and read each pod's own cgroup
```

## Apply

```sh
kubectl apply -f k8s/namespace.yaml

# Create the Secret imperatively so real values never touch a file git can see.
kubectl -n ai-customer-service-dotnet create secret generic ai-customer-service-dotnet-secrets \
  --from-literal=ANTHROPIC_API_KEY="$ANTHROPIC_API_KEY" \
  --from-literal=POSTGRES_USER='csagent' \
  --from-literal=POSTGRES_PASSWORD="$PGPASSWORD"

kubectl apply -f k8s/
kubectl -n ai-customer-service-dotnet rollout status deploy/ai-customer-service-dotnet
```

## The operations surface is off unless you turn it on

The admin API reads customer conversations, so it is opt-in. The switch is in the ConfigMap
because it is a switch; the first admin's credentials are in the Secret because they are a
credential for every conversation in the database:

```sh
kubectl -n ai-customer-service-dotnet patch configmap ai-customer-service-dotnet-config \
  --type=merge -p '{"data":{"ADMIN_ENABLED":"true"}}'
kubectl -n ai-customer-service-dotnet patch secret ai-customer-service-dotnet-secrets --type=merge \
  -p "{\"stringData\":{\"ADMIN_SEED_USERNAME\":\"root\",\"ADMIN_SEED_PASSWORD\":\"$(openssl rand -base64 18)\"}}"
kubectl -n ai-customer-service-dotnet rollout restart deploy/ai-customer-service-dotnet
```

The seed creates the first admin only into an empty `staff_account` table, under a table
lock, so two replicas starting together seed once — `verify.sh` counts the rows. After that
admin signs in, accounts are made on the Staff page and the seed may be removed.

**With `ADMIN_ENABLED` false, `/api/admin/v1/*` is a 404 rather than a guarded 401** — the
routes are never registered. `verify.sh` asserts both halves, because "documented but never
deployed" is exactly how the sibling manifests were wrong.

## The operations UI is a second deployment

`k8s/admin-ui.yaml` is the static bundle on `nginx-unprivileged`: its own Deployment,
Service and ConfigMap, two replicas, `10m`/`24Mi` requested. It holds no secret and reaches
no database. It **proxies `/api` to the API's Service**, so the browser sees one origin and
the API needs no CORS — the ConfigMap's one key, `ADMIN_API_UPSTREAM`, is the Service DNS
name as nginx reaches it from inside the cluster, rendered into the nginx config at start by
the image's envsubst step. That is the shape the Compose stack has too, with `app:8082`.

This is the one place the .NET deployment differs from the Go one on purpose: the Go UI
calls the API on a separate origin and the two are wired by `ADMIN_API_BASE` on one side
and `ADMIN_CORS_ORIGINS` on the other, a pair only the harness checks agree. A proxy has one
value to get right, and `verify.sh`'s "401 through nginx" assertion is the whole check: a
request to `/api/admin/v1/me` on the UI's origin arrives at the API and is refused there.

Unlike the API pod, this one needs writable paths: envsubst writes the rendered config into
`/etc/nginx/conf.d`, and nginx keeps its pid and caches under `/tmp`. Both are `emptyDir`;
the root filesystem stays read-only.

## Before you apply

1. `deployment.yaml` and `admin-ui.yaml` → `image`. Point them at your registry and, in
   anything you care about, an immutable tag or digest.
2. `configmap.yaml` → `POSTGRES_HOST` / `POSTGRES_PORT` / `POSTGRES_DB`. The database
   needs the `vector` extension available.

**Known limitation:** hand-editing tracked files is a drift generator. A Kustomize base
plus an overlay is the fix; this directory is deliberately flat, for the reason the Go
README gives: the harness's guarantee that it applies `k8s/` *unmodified* is what makes
these the manifests that were verified, and an overlay would need the same guarantee.

## Verify on kind, before a real cluster

```sh
k8s/kind/verify.sh          # create a throwaway cluster, deploy, assert
k8s/kind/verify.sh --keep   # skip the image builds if the tags are already present
k8s/kind/verify.sh --down   # delete it
```

Twenty-five assertions: capacity before deploying; both replicas ready; nothing OOMKilled;
the Secret untouched by the directory apply; no replica losing the `CREATE EXTENSION` race on
a database the harness makes cold every run; uid 1654; a read-only root filesystem; no
writable volume at all; health, readiness, .NET process metrics and the demo page through the
Service; the admin API absent (404) with the switch off, then turned on the documented way
and refusing without a session, signing in the seeded admin, accepting the session, and
having seeded exactly one account across two replicas; the UI rolling out, served, with a
Content-Security-Policy on the document, proxying `/api` to the API, as uid 101 on a
read-only root with a writable `/tmp`; and a bad key surfacing as `502` rather than as a
healthy pod returning errors. No API key needed; export `ANTHROPIC_API_KEY` to check the
model call too.

The harness never opens your kubeconfig: it exports one of its own
(`k8s/kind/.kubeconfig`, gitignored). The reasons are the Go README's and are not repeated.

## Which assertions have been seen to fail

An assertion nobody has seen go red is a claim, not a check. This is the honest inventory
after the first day:

| Assertion | Seen red? |
| --- | --- |
| no container was OOMKilled | **the condition, yes** — `sweep.sh` reads the same field, and it reported `OOMKilled` at 640Mi, 768Mi and 896Mi. The assertion itself has not fired in `verify.sh`, because the shipped limit is above the boundary. |
| the node has room for the replicas | no |
| both replicas ready | no |
| the directory apply left the Secret alone | no |
| no replica lost the `CREATE EXTENSION` race | no — the advisory lock was in the schema code before the harness existed, so the race has never been observed here; the Go repository reproduced it without the lock |
| runs as uid 1654 / read-only root / no volume | no |
| health, readiness, metrics, the demo page | no |
| the admin API is a 404 with the switch off | no in the harness; the same property was observed live in Compose, by accident, when an `up` without the variable recreated the service and the UI's sign-in got a 404 |
| 401 without a session, seeded sign-in, 200 with a session, exactly one seed | no |
| the UI rolled out, served, CSP, proxy, uid 101, read-only, writable /tmp | no |
| a bad key surfaces as 502 | the branch was exercised (run without `ANTHROPIC_API_KEY`) and passed; it has never failed |

Twenty-five green on the first run is not evidence that twenty-five things are right. It is
evidence that the manifests and the harness agree with each other, which is what a first
run can show. The rows above should be read as unproven until something has made each one
red.

## Sizing, measured

`k8s/kind/sweep.sh`, one replica, each row reading its own cgroup from inside the pod:

| limit | outcome | cgroup peak | `anon` | process RSS | % of limit |
| --- | --- | --- | --- | --- | --- |
| 640Mi | **OOMKilled** | — | — | — | — |
| 768Mi | **OOMKilled** | — | — | — | — |
| 896Mi | **OOMKilled** | — | — | — | — |
| 1Gi | started | 932 MiB | 637 MiB | 742 MiB | 91% |
| 1152Mi | started | 924 MiB | 637 MiB | 742 MiB | 80% |
| 1280Mi | started | 924 MiB | 635 MiB | 742 MiB | 72% |
| 1536Mi | started | 925 MiB | 637 MiB | 742 MiB | 60% |
| 2Gi | started | 933 MiB | 635 MiB | 741 MiB | 46% |

**The number to size against is `anon`, and it is 637 MiB** — the process itself,
overwhelmingly the 470 MB fp32 model held by ONNX Runtime, identical in every row. The
peak is ~930 MiB from 1Gi upwards; the ~300 MiB between them is page cache from reading the
model file out of the image layer, reclaimable, and charged to whichever replica faults it
in first: the two-replica run showed `file` at 394 MiB in one pod and 123 MiB in the other
with `anon` equal in both.

Sizing against `anon` alone would be wrong in the other direction: 896Mi is well above 637
MiB and still OOMKills, because the page cache churn while reading a 470 MB file cannot all
be reclaimed in time during startup. Hence `requests: 1152Mi` — covering the *startup*
peak, because the peak is at boot and a node packed to requests would crash-loop the pod
rather than degrade it — and `limits: 1536Mi`, which leaves the worst observed peak at 60%.

Against the Go implementation on the same laptop: Go's `anon` is 951 MiB and its peak
~1270 MiB; it OOMKills at 1152Mi and ships `1536Mi` / `2Gi`. The .NET process holds the same
model in about 310 MiB less anonymous memory. That number is reported, not explained — the
two bindings load the same ONNX file into the same runtime library, and where the difference
lives has not been measured.

## Startup

| | |
| --- | --- |
| Container start to `Ready` | **4 s** (the startup probe polls every 2 s, so this is quantised) |
| CPU consumed to reach `Ready` | **6.9–7.5 s** of CPU, under a 2-CPU limit |
| `Environment.ProcessorCount` inside the pod | **2**, on a node with 18 CPUs — derived from `limits.cpu: "2"` |
| `anon` / peak, two replicas at rest | 635–640 MiB / 924–925 MiB |

Nothing is downloaded at startup: the model is baked into the image. That is why the image
is 1.22 GB and why a cold pod needs no egress.

## The .NET-specific part: ProcessorCount is the embedding concurrency

.NET derives `Environment.ProcessorCount` from the cgroup CPU limit, rounding up. It is what
the embedding concurrency bound defaults to, and each caller inside ONNX Runtime is a
thread-pool thread blocked in native code — so the CPU limit is not only a throttle, it
decides how many pool threads the embedding path may hold. Remove the limit and the bound
silently becomes the node's core count. `EMBEDDING_MAX_CONCURRENCY` is therefore set
explicitly in the ConfigMap, so the bound does not move when someone edits `resources`.

The other coupling is the diagnostics socket. The runtime opens an IPC socket under `/tmp`
for `dotnet-counters` and dumps; on a read-only root with no volumes that path does not
exist. `DOTNET_EnableDiagnostics: "0"` in the ConfigMap is what lets the API pod run with no
volume at all — the same property the Go pod has and the Java pod, which unpacks its native
library into `/tmp`, does not. Turn it back on with an `emptyDir` at `/tmp` when you need a
dump.

## What this deployment does not fix

**The per-conversation lock is per process.** Turns are serialised within a replica, so two
replicas can still interleave one conversation. The real fix is Postgres advisory locks on
the conversation id. The ticket cap and deduplication, by contrast, are in Postgres now and
hold across replicas.

## Deliberately not included

- **Ingress / Gateway.** The chat API has no authentication and the operations UI is a
  sign-in page for customer conversations; what sits in front of each is the edge owner's
  decision.
- **HorizontalPodAutoscaler.** The useful signal is in-flight model calls, not CPU.
- **PodDisruptionBudget.** Worth adding (`minAvailable: 1`) on a cluster with real node churn.
- **NetworkPolicy.** Depends entirely on the CNI and the cluster's conventions.
- **A Postgres.** Conversation memory, the vectors, tickets and the operational record share
  one database, so it wants a real managed instance with backups.

---

[← Back to the README](../README.md)
