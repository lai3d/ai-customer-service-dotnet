#!/usr/bin/env bash
# Verify the Kubernetes manifests on a throwaway kind cluster.
#
#   k8s/kind/verify.sh            create the cluster, deploy, assert, leave it running
#   k8s/kind/verify.sh --down     delete the cluster and exit
#   k8s/kind/verify.sh --keep     skip the image builds if the tags are already present
#
# It applies the manifests in k8s/ *unmodified*. A harness that patches the resources or the
# image before applying verifies the patch, not the file anyone else will use. The only
# things it adds are the two the manifests deliberately do not ship -- a Postgres
# (k8s/kind/postgres.yaml) and a Secret, created imperatively -- and, for the operations
# assertions, the ConfigMap and Secret patches an operator would make to turn the surface on.
#
# The Secret gets a placeholder ANTHROPIC_API_KEY unless one is exported. Nothing in startup
# or in either probe calls the model, so a fake key verifies everything except the model
# call -- and it verifies that a bad key surfaces as 502 rather than as a healthy pod
# serving errors. Export a real key to check the model path too.
#
# The method is the Go implementation's, whose harness this was written against: every
# number in k8s/ was measured here before it was committed.
set -euo pipefail

CLUSTER=${CLUSTER:-ai-cs-dotnet}
NS=ai-customer-service-dotnet
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
IMAGE=$(grep -m1 'image: ghcr.io/lai3d/ai-customer-service-dotnet:' "$ROOT/k8s/deployment.yaml" | awk '{print $2}')
UI_IMAGE=$(grep -m1 'image: ghcr.io/lai3d/ai-customer-service-dotnet-admin-ui' "$ROOT/k8s/admin-ui.yaml" | awk '{print $2}')
PASS=0; FAIL=0

say()  { printf '\n\033[1m== %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; PASS=$((PASS+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; FAIL=$((FAIL+1)); }
note() { printf '  \033[33mNOTE\033[0m %s\n' "$*"; }
# Compare captured output instead of discarding it, so an assertion failure and a transient
# infrastructure failure do not look identical.
contains(){ local d=$1 pattern=$2; shift 2
  local out; out=$("$@" 2>&1) || true
  case "$out" in (*"$pattern"*) ok "$d";; (*) bad "$d -- got: ${out:0:90}";; esac; }

# A kubeconfig of the harness's own, so the user's is never opened for writing. What this
# guards is which cluster somebody's next `kubectl delete` reaches; `kind create cluster`
# writes into $KUBECONFIG, so pinning --context alone is not enough on a fresh run.
export KUBECONFIG="$(dirname "$0")/.kubeconfig"

if [[ ${1:-} == --down ]]; then
  kind delete cluster --name "$CLUSTER"
  rm -f "$KUBECONFIG"
  exit 0
fi

for t in kind kubectl docker curl; do
  command -v "$t" >/dev/null || { echo "missing: $t" >&2; exit 1; }
done

say "cluster"
if kind get clusters 2>/dev/null | grep -qx "$CLUSTER"; then
  kind export kubeconfig --name "$CLUSTER" >/dev/null
else
  kind create cluster --name "$CLUSTER" --wait 120s
fi
KUBECTL=(kubectl --context "kind-$CLUSTER")

say "images  $IMAGE  $UI_IMAGE"
if [[ ${1:-} == --keep ]] && docker image inspect "$IMAGE" >/dev/null 2>&1; then
  echo "  reusing the local API image"
else
  docker build -t "$IMAGE" "$ROOT"
fi
if [[ ${1:-} == --keep ]] && docker image inspect "$UI_IMAGE" >/dev/null 2>&1; then
  echo "  reusing the local UI image"
else
  docker build -q -t "$UI_IMAGE" "$ROOT/admin-ui" >/dev/null
fi
# 1.2 GB, of which 470 MB is the embedding model: the honest cost of baking it in.
kind load docker-image "$IMAGE" --name "$CLUSTER"
kind load docker-image "$UI_IMAGE" --name "$CLUSTER" >/dev/null

say "capacity"
# Check the node can hold what the manifests ask for, before deploying rather than after: a
# too-large request does not fail, the pod sits Pending and the rollout times out. Read the
# rendered spec, not the file, and compare against what is free, not what the node has.
node_mem_ki=$("${KUBECTL[@]}" get nodes -o jsonpath='{.items[0].status.allocatable.memory}' | tr -d 'Ki')
req=$("${KUBECTL[@]}" apply --dry-run=client -o jsonpath='{.spec.template.spec.containers[0].resources.requests.memory}' -f "$ROOT/k8s/deployment.yaml" 2>/dev/null)
reps=$("${KUBECTL[@]}" apply --dry-run=client -o jsonpath='{.spec.replicas}' -f "$ROOT/k8s/deployment.yaml" 2>/dev/null)
case "$req" in (*Gi) req_mi=$(( ${req%Gi} * 1024 ));; (*Mi) req_mi=${req%Mi};; (*) req_mi="";; esac
if [ -z "$req_mi" ] || [ -z "$reps" ]; then
  bad "could not read the memory request or replica count from deployment.yaml (got req='$req' replicas='$reps')"
else
  used_mi=$("${KUBECTL[@]}" get pods --all-namespaces \
    -o jsonpath='{range .items[*]}{.metadata.namespace}{" "}{range .spec.containers[*]}{.resources.requests.memory}{" "}{end}{"\n"}{end}' 2>/dev/null \
    | awk -v skip="$NS" '$1!=skip{for(i=2;i<=NF;i++){v=$i;
        if (v ~ /Gi$/) {sub(/Gi$/,"",v); m+=v*1024}
        else if (v ~ /Mi$/) {sub(/Mi$/,"",v); m+=v}
        else if (v ~ /Ki$/) {sub(/Ki$/,"",v); m+=v/1024}}} END{printf "%d", m}')
  total_mi=$(( node_mem_ki / 1024 )); free_mi=$(( total_mi - used_mi )); want_mi=$(( req_mi * reps ))
  printf '  node %d MiB allocatable, %d MiB reserved by other namespaces, %d MiB free; this deploy wants %s x %s = %d MiB\n' \
    "$total_mi" "$used_mi" "$free_mi" "$reps" "$req" "$want_mi"
  if [ "$want_mi" -gt "$free_mi" ]; then bad "only $free_mi MiB is free -- a replica will sit Pending and the rollout will just time out"
  else ok "the node has room for $reps replicas at $req ($want_mi of $free_mi MiB free)"; fi
fi

say "deploy"
"${KUBECTL[@]}" apply -f "$ROOT/k8s/namespace.yaml"
"${KUBECTL[@]}" apply -f "$ROOT/k8s/kind/postgres.yaml"
"${KUBECTL[@]}" -n "$NS" rollout status deploy/postgres --timeout=180s

"${KUBECTL[@]}" -n "$NS" create secret generic ai-customer-service-dotnet-secrets \
  --from-literal=ANTHROPIC_API_KEY="${ANTHROPIC_API_KEY:-placeholder-no-model-call-is-made-during-startup}" \
  --from-literal=POSTGRES_USER=csagent \
  --from-literal=POSTGRES_PASSWORD=csagent \
  --dry-run=client -o yaml | "${KUBECTL[@]}" apply -f - >/dev/null

# Make the cold-database path real on every run: CREATE EXTENSION IF NOT EXISTS is not
# concurrency-safe, and the check below is only meaningful if both replicas actually start
# against a database without the extension. The whole schema, not just the extension, so
# the harness does not invent a table with no vector column.
"${KUBECTL[@]}" -n "$NS" exec deploy/postgres -- psql -U csagent -d csagent \
  -c 'DROP SCHEMA public CASCADE' -c 'CREATE SCHEMA public' >/dev/null 2>&1 || true

# The operations surface must be *made* off, not assumed off: a --keep run reuses the
# ConfigMap the enabling half of this script patched last time.
"${KUBECTL[@]}" apply -f "$ROOT/k8s/"
"${KUBECTL[@]}" -n "$NS" rollout restart deploy/ai-customer-service-dotnet >/dev/null 2>&1 || true
"${KUBECTL[@]}" -n "$NS" rollout status deploy/ai-customer-service-dotnet --timeout=300s || true

say "assertions"
ready_pod() {
  "${KUBECTL[@]}" -n "$NS" get pods -l "app.kubernetes.io/component=${1:-app}" \
    -o jsonpath='{range .items[*]}{.metadata.name}{" "}{.status.conditions[?(@.type=="Ready")].status}{" "}{.metadata.deletionTimestamp}{"\n"}{end}' \
    | awk '$2=="True" && $3==""{print $1; exit}'
}
# exec_in_pod DESCRIPTION EXPECTED-SUBSTRING -- COMMAND...   (COMPONENT selects the deployment)
exec_in_pod() {
  local d=$1 pattern=$2; shift 2
  local out="" pod=""
  for _ in $(seq 1 10); do
    pod=$(ready_pod "${COMPONENT:-app}")
    if [ -n "$pod" ]; then
      out=$("${KUBECTL[@]}" -n "$NS" exec "$pod" -- "$@" 2>&1) || true
      case "$out" in
        (*"completed pod"*|*"not found"*|*"is terminating"*) ;;
        (*) case "$out" in (*"$pattern"*) ok "$d"; return;; (*) bad "$d -- got: ${out:0:90}"; return;; esac;;
      esac
    fi
    sleep 2
  done
  bad "$d -- no Ready, non-terminating pod after 20s"
}

POD=$(ready_pod)
replicas=$("${KUBECTL[@]}" -n "$NS" get deploy ai-customer-service-dotnet -o jsonpath='{.status.readyReplicas}')
[[ ${replicas:-0} == 2 ]] && ok "both replicas ready" || bad "readyReplicas=${replicas:-0}, want 2"

if "${KUBECTL[@]}" -n "$NS" get pods -l app.kubernetes.io/component=app -o json | grep -q OOMKilled; then
  bad "a container was OOMKilled -- the memory limit is too low"
else ok "no container was OOMKilled"; fi

if "${KUBECTL[@]}" -n "$NS" get secret ai-customer-service-dotnet-secrets -o jsonpath='{.data.ANTHROPIC_API_KEY}' | base64 -d | grep -q REPLACE_ME; then
  bad "the directory apply overwrote the Secret with placeholders"
else ok "the directory apply left the Secret alone"; fi

raced=0
for p in $("${KUBECTL[@]}" -n "$NS" get pods -l app.kubernetes.io/component=app -o name); do
  hits=$("${KUBECTL[@]}" -n "$NS" logs "$p" --previous 2>/dev/null | grep -c pg_extension_name_index || true)
  [[ ${hits:-0} -gt 0 ]] && raced=$((raced + 1))
done
[[ $raced -gt 0 ]] && bad "$raced replica(s) lost the CREATE EXTENSION race on a cold database and restarted" || ok "no replica lost the CREATE EXTENSION race"

exec_in_pod "runs as uid 1654, the aspnet image's app user" "1654" id -u
exec_in_pod "root filesystem is read-only" "Read-only" sh -c 'touch /nope'
if "${KUBECTL[@]}" -n "$NS" get pod "$POD" -o jsonpath='{.spec.volumes[*].name}' | tr ' ' '\n' | grep -qv '^kube-api-access'; then
  note "the pod mounts a volume other than the service-account token"
else ok "no writable volume is needed at all"; fi

"${KUBECTL[@]}" -n "$NS" port-forward svc/ai-customer-service-dotnet 18082:8082 >/dev/null 2>&1 &
PF=$!; trap 'kill $PF 2>/dev/null || true' EXIT
sleep 4

contains "health is UP through the Service" "UP" curl -sf localhost:18082/healthz
contains "readiness reaches Postgres"       "UP" curl -sf localhost:18082/readyz
contains "the metrics endpoint serves .NET process metrics" "process_cpu_seconds_total" curl -sf localhost:18082/metrics
contains "the demo page is served" "AI Customer Service" curl -sf localhost:18082/

# Unconfigured has to mean the routes were never registered: a 404, not a 401.
status=$(curl -s -o /dev/null -w '%{http_code}' localhost:18082/api/admin/v1/me || echo 000)
[[ $status == 404 ]] && ok "with ADMIN_ENABLED false the admin API does not exist (404, not 401)" || bad "/api/admin/v1/me returned $status with the admin off, want 404"

# Turn it on the way an operator would: the switch in the ConfigMap, the seed in the Secret.
say "operations surface"
SEED_PASSWORD=$(openssl rand -base64 18)
"${KUBECTL[@]}" -n "$NS" patch configmap ai-customer-service-dotnet-config --type=merge -p '{"data":{"ADMIN_ENABLED":"true"}}' >/dev/null
"${KUBECTL[@]}" -n "$NS" patch secret ai-customer-service-dotnet-secrets --type=merge \
  -p "{\"stringData\":{\"ADMIN_SEED_USERNAME\":\"probe\",\"ADMIN_SEED_PASSWORD\":\"${SEED_PASSWORD}\"}}" >/dev/null
"${KUBECTL[@]}" -n "$NS" rollout restart deploy/ai-customer-service-dotnet >/dev/null
"${KUBECTL[@]}" -n "$NS" rollout status deploy/ai-customer-service-dotnet --timeout=180s >/dev/null
{ kill $PF && wait $PF; } 2>/dev/null || true
"${KUBECTL[@]}" -n "$NS" port-forward svc/ai-customer-service-dotnet 18082:8082 >/dev/null 2>&1 &
PF=$!; trap 'kill $PF 2>/dev/null || true' EXIT
sleep 4

status=$(curl -s -o /dev/null -w '%{http_code}' localhost:18082/api/admin/v1/me || echo 000)
[[ $status == 401 ]] && ok "the admin API refuses a request with no session (401)" || bad "/me with no session returned $status, want 401"

# Two replicas started against an empty staff_account under a table lock: one seed.
TOKEN=$(curl -s localhost:18082/api/admin/v1/session -H 'Content-Type: application/json' \
  -d "{\"username\":\"probe\",\"password\":\"${SEED_PASSWORD}\"}" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
[[ -n $TOKEN ]] && ok "the seeded admin signs in through the Service" || bad "sign-in with the seeded admin failed"
status=$(curl -s -o /dev/null -w '%{http_code}' localhost:18082/api/admin/v1/me -H "Authorization: Bearer ${TOKEN}" || echo 000)
[[ $status == 200 ]] && ok "a bearer session is accepted (200)" || bad "/me with a session returned $status, want 200"
seeds=$("${KUBECTL[@]}" -n "$NS" exec deploy/postgres -- psql -U csagent -d csagent -tAc "SELECT count(*) FROM staff_account" 2>/dev/null | tr -d '[:space:]')
[[ $seeds == 1 ]] && ok "two replicas seeding at once left exactly one account" || bad "staff_account has ${seeds:-?} rows after seeding, want 1"

say "operations UI"
"${KUBECTL[@]}" -n "$NS" rollout status deploy/ai-customer-service-dotnet-admin-ui --timeout=180s >/dev/null \
  && ok "the operations UI rolled out" || bad "the operations UI did not become ready"
"${KUBECTL[@]}" -n "$NS" port-forward svc/ai-customer-service-dotnet-admin-ui 18083:8083 >/dev/null 2>&1 &
PFUI=$!; trap 'kill $PF $PFUI 2>/dev/null || true' EXIT
sleep 4
contains "the operations UI is served" "<title>Operations" curl -sf localhost:18083/
if curl -sfI localhost:18083/ | grep -qi content-security-policy; then ok "the UI sends a Content-Security-Policy on the document itself"
else bad "no Content-Security-Policy on GET / from the UI"; fi
# The proxy is the whole point of the deployment shape: /api on the UI's origin must reach
# the API's Service inside the cluster. A 401 proves the request arrived at the API.
status=$(curl -s -o /dev/null -w '%{http_code}' localhost:18083/api/admin/v1/me || echo 000)
[[ $status == 401 ]] && ok "the UI proxies /api to the API Service (401 from the API, through nginx)" || bad "/api/admin/v1/me through the UI returned $status, want 401"
COMPONENT=admin-ui exec_in_pod "the UI runs as uid 101" "101" id -u
COMPONENT=admin-ui exec_in_pod "the UI's root filesystem is read-only" "Read-only" sh -c 'touch /nope'
COMPONENT=admin-ui exec_in_pod "the UI can write /tmp, where nginx keeps its pid and caches" "ok" sh -c 'touch /tmp/probe && echo ok'

# Retrieval runs before the model call, so this exercises the embedding path and then fails
# at the provider -- which must be a 502, not a 500 and not a healthy 200.
status=$(curl -s -o /dev/null -w '%{http_code}' localhost:18082/api/v1/chat -H 'Content-Type: application/json' \
  -d '{"message":"How long do I have to return an item?"}' || echo 000)
if [[ -n ${ANTHROPIC_API_KEY:-} ]]; then
  [[ $status == 200 ]] && ok "a real turn answered (200)" || bad "a real turn returned $status, want 200"
else
  [[ $status == 502 ]] && ok "a bad key surfaces as 502, not a healthy error" || bad "a bad key returned $status, want 502"
fi

say "footprint"
# What the pod thinks it has: .NET derives ProcessorCount from the cgroup CPU limit, and the
# embedding bound follows it unless set. The node's count is printed beside it.
node_cpus=$("${KUBECTL[@]}" get nodes -o jsonpath='{.items[0].status.capacity.cpu}')
seen=$("${KUBECTL[@]}" -n "$NS" logs "$(ready_pod)" 2>/dev/null | sed -n 's/.*listening on .*\(processors [0-9]*, embedding concurrency [0-9]*\).*/\1/p' | head -1)
note "inside the pod: ${seen:-not logged}; the node has ${node_cpus} CPUs"
# Time to Ready and CPU consumed to get there, from the pod's own status and cgroup.
for p in $("${KUBECTL[@]}" -n "$NS" get pods -l app.kubernetes.io/component=app -o jsonpath='{range .items[*]}{.metadata.name}{" "}{.metadata.deletionTimestamp}{"\n"}{end}' | awk '$2==""{print $1}'); do
  started=$("${KUBECTL[@]}" -n "$NS" get pod "$p" -o jsonpath='{.status.containerStatuses[0].state.running.startedAt}')
  ready=$("${KUBECTL[@]}" -n "$NS" get pod "$p" -o jsonpath='{.status.conditions[?(@.type=="Ready")].lastTransitionTime}')
  secs=$(( $(date -j -u -f %Y-%m-%dT%H:%M:%SZ "$ready" +%s 2>/dev/null || date -u -d "$ready" +%s) - $(date -j -u -f %Y-%m-%dT%H:%M:%SZ "$started" +%s 2>/dev/null || date -u -d "$started" +%s) ))
  cpu=$("${KUBECTL[@]}" -n "$NS" exec "$p" -- awk '/usage_usec/{printf "%.1f", $2/1000000}' /sys/fs/cgroup/cpu.stat 2>/dev/null || echo "?")
  note "$p: container start to Ready ${secs}s (probe period 2s, so quantised), ${cpu}s of CPU consumed so far"
done
# Every row reads its own cgroup from inside the pod it describes, so a row cannot lie
# about which pod it measures.
for p in $("${KUBECTL[@]}" -n "$NS" get pods -l app.kubernetes.io/component=app -o jsonpath='{range .items[*]}{.metadata.name}{" "}{.metadata.deletionTimestamp}{"\n"}{end}' | awk '$2==""{print $1}'); do
  "${KUBECTL[@]}" -n "$NS" exec "$p" -- sh -c '
    cur=$(cat /sys/fs/cgroup/memory.current); peak=$(cat /sys/fs/cgroup/memory.peak 2>/dev/null || echo 0); max=$(cat /sys/fs/cgroup/memory.max)
    anon=$(awk "/^anon /{print \$2}" /sys/fs/cgroup/memory.stat); file=$(awk "/^file /{print \$2}" /sys/fs/cgroup/memory.stat)
    rss=$(awk "/VmRSS/{print \$2}" /proc/1/status)
    printf "  %s  current %d MiB  peak %d MiB  anon %d MiB  file %d MiB  rss %d MiB  limit %s\n" "$HOSTNAME" $((cur/1048576)) $((peak/1048576)) $((anon/1048576)) $((file/1048576)) $((rss/1024)) "$max"' 2>/dev/null || true
done

say "result"
printf '  %d passed, %d failed\n' "$PASS" "$FAIL"
printf '  cluster left running; %s --down to remove it\n' "$0"
[[ $FAIL -eq 0 ]]
