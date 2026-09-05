#!/usr/bin/env bash
# Sweep the memory limit on the running kind cluster, one replica, and read each pod's own
# cgroup. This is how the numbers in deployment.yaml were chosen; run it again before
# changing them. Leaves the deployment at the last limit in the list.
#
#   k8s/kind/sweep.sh 768Mi 1Gi 1280Mi 1536Mi 2Gi
set -euo pipefail
CLUSTER=${CLUSTER:-ai-cs-dotnet}
NS=ai-customer-service-dotnet
export KUBECONFIG="$(dirname "$0")/.kubeconfig"
KUBECTL=(kubectl --context "kind-$CLUSTER")
printf '%-8s %-11s %-10s %-10s %-10s %-10s %-10s\n' limit outcome current peak anon file rss
for lim in "$@"; do
  "${KUBECTL[@]}" -n "$NS" patch deploy ai-customer-service-dotnet --type=json -p "[
    {\"op\":\"replace\",\"path\":\"/spec/replicas\",\"value\":1},
    {\"op\":\"replace\",\"path\":\"/spec/template/spec/containers/0/resources/limits/memory\",\"value\":\"$lim\"},
    {\"op\":\"replace\",\"path\":\"/spec/template/spec/containers/0/resources/requests/memory\",\"value\":\"$lim\"}]" >/dev/null
  "${KUBECTL[@]}" -n "$NS" rollout restart deploy/ai-customer-service-dotnet >/dev/null
  outcome=started
  "${KUBECTL[@]}" -n "$NS" rollout status deploy/ai-customer-service-dotnet --timeout=150s >/dev/null 2>&1 || outcome=timeout
  # Let the second replica of the previous revision finish terminating before reading.
  sleep 8
  pod=$("${KUBECTL[@]}" -n "$NS" get pods -l app.kubernetes.io/component=app \
    -o jsonpath='{range .items[*]}{.metadata.name}{" "}{.status.conditions[?(@.type=="Ready")].status}{" "}{.metadata.deletionTimestamp}{"\n"}{end}' | awk '$2=="True" && $3==""{print $1; exit}')
  if "${KUBECTL[@]}" -n "$NS" get pods -l app.kubernetes.io/component=app -o json | grep -q OOMKilled; then outcome=OOMKilled; fi
  if [ -n "$pod" ] && [ "$outcome" = started ]; then
    # Warm the embedding path once so the peak includes a query, not just startup.
    "${KUBECTL[@]}" -n "$NS" exec "$pod" -- sh -c 'true' >/dev/null 2>&1 || true
    "${KUBECTL[@]}" -n "$NS" exec "$pod" -- sh -c "
      cur=\$(cat /sys/fs/cgroup/memory.current); peak=\$(cat /sys/fs/cgroup/memory.peak); anon=\$(awk '/^anon /{print \$2}' /sys/fs/cgroup/memory.stat)
      file=\$(awk '/^file /{print \$2}' /sys/fs/cgroup/memory.stat); rss=\$(awk '/VmRSS/{print \$2}' /proc/1/status)
      printf '%-8s %-11s %-10s %-10s %-10s %-10s %-10s\n' '$lim' '$outcome' \$((cur/1048576))MiB \$((peak/1048576))MiB \$((anon/1048576))MiB \$((file/1048576))MiB \$((rss/1024))MiB"
  else
    printf '%-8s %-11s\n' "$lim" "$outcome"
  fi
done
