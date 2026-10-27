#!/bin/sh
# Usage: measure.sh <label> <scenario>
#   env: HM_ARCH HM_PORT HM_CPUS HM_MEM HM_PROC HM_QUEUE HM_REPEAT HM_ENVFILE HM_WXE, HM_WORK (default /tmp/harbourops-photo-memory)
# Recreates the app container (fresh cgroup), samples cgroup v2 memory every 200 ms inside the Colima VM,
# runs the load scenario HM_REPEAT times, then records memory.peak, the sampled maxima, memory after each round,
# OOM events, and the process high-water mark. On a Linux host, replace "colima ssh --" with a local shell.
set -eu
M=$(cd "$(dirname "$0")" && pwd)
W=${HM_WORK:-/tmp/harbourops-photo-memory}
mkdir -p "$W/out"
label=$1; scenario=$2; port=${HM_PORT:-8098}
cd "$M"
docker compose -f compose.yml up -d --force-recreate app >/dev/null 2>&1
for i in $(seq 1 150); do curl -sf "http://localhost:$port/health/ready" >/dev/null && break; sleep 2; done
name="hm-${HM_ARCH}-app-1"; id=$(docker inspect "$name" --format '{{.Id}}'); pid=$(docker inspect "$name" --format '{{.State.Pid}}')
cg=/sys/fs/cgroup/docker/$id
sleep 3
idle=$(colima ssh -- sh -c "grep -E '^anon ' $cg/memory.stat | cut -d' ' -f2; cat $cg/memory.current")
colima ssh -- sh -c "while [ -d $cg ]; do echo \$(date +%s.%N) \$(grep -E '^anon ' $cg/memory.stat | cut -d' ' -f2) \$(cat $cg/memory.current); sleep 0.2; done" > "$W/out/$label.samples" 2>/dev/null &
sampler=$!
after=""; for r in $(seq 1 ${HM_REPEAT:-1}); do node harness.mjs load "http://localhost:$port" "$W/img" "$scenario" > "$W/out/$label.load.json"; sleep 2; after="$after $(colima ssh -- sh -c "grep -E '^anon ' $cg/memory.stat | cut -d' ' -f2")"; done; echo "$after" > "$W/out/$label.after"
sleep 1
peak=$(colima ssh -- cat $cg/memory.peak)
events=$(colima ssh -- cat $cg/memory.events | tr '\n' ' ')
hwm=$(colima ssh -- sudo grep VmHWM /proc/$pid/status | tr -s ' ')
kill $sampler 2>/dev/null || true
python3 -I - "$W/out" "$label" "$idle" "$peak" "$events" "$hwm" <<'PY'
import sys, json, os
out, label, idle, peak, events, hwm = sys.argv[1:]
os.chdir(out)
rows = [l.split() for l in open(f'{label}.samples') if len(l.split()) == 3]
anon = max(int(r[1]) for r in rows); cur = max(int(r[2]) for r in rows)
i_anon, i_cur = (int(x) for x in idle.split())
load = json.load(open(f'{label}.load.json'))
mib = lambda b: round(b / 1048576)
print(json.dumps({'label': label, 'samples': len(rows), 'idle_anon_MiB': mib(i_anon), 'idle_current_MiB': mib(i_cur),
  'peak_sampled_anon_MiB': mib(anon), 'peak_sampled_current_MiB': mib(cur), 'cgroup_memory_peak_MiB': mib(int(peak)),
  'process_VmHWM': hwm.strip(), 'anon_MiB_after_each_round': [round(int(x)/1048576) for x in open(f'{label}.after').read().split()], 'memory_events': events.strip(), 'load': load}, indent=1))
PY
