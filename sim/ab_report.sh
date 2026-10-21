#!/usr/bin/env bash
# Score a Director A/B night: per run, plan steps done, deaths, whether a home got built, build_room timeouts.
#   sim/ab_report.sh [night dir]   (default ~/Library/Caches/terraria-sim/night7)
#   ARMS="old old2 new" for a night with a second baseline arm (A/A/B: old vs old2 is the noise floor)
log="${1:-$HOME/Library/Caches/terraria-sim/night7}"
for name in ${ARMS:-old new}; do
  echo "== $name"
  tot=0; dead=0; homes=0; runs=0
  for f in $(ls "$log/$name"[0-9]*.log 2>/dev/null | sort -V); do
    r=$(basename "$f" .log); [[ "${r#$name}" =~ ^[0-9]+$ ]] || continue   # not old2-start1
    other=0; for o in ${ARMS:-old new}; do [ ${#o} -gt ${#name} ] && [[ "$r" == "$o"* ]] && [[ "${r#$o}" =~ ^[0-9]+$ ]] && other=1; done
    [ $other = 1 ] && continue   # old21 is old2's run 1, not old's run 21
    steps=$(grep -oE 'Session over: [0-9]+' "$f" | tail -1 | grep -oE '[0-9]+$')
    d=$(grep -c '^\[.*\] Died' "$f"); h=$(grep -c 'Done: Build a home' "$f"); t=$(grep -c 'ERR build_house.*build_room timed out' "$f")
    r=$(basename "$f" .log); grep -q "$name run${r#$name} exit 143" "$log/queue.log" && { echo "  $r     stopped early (code swap), not counted"; continue; }
    printf "  %-8s steps %-4s deaths %-4s home %-3s build_room timeouts %s\n" "$(basename "$f" .log)" "${steps:-(running)}" "$d" "$h" "$t"
    [ -n "$steps" ] && { runs=$((runs + 1)); tot=$((tot + steps)); dead=$((dead + d)); [ "$h" -gt 0 ] && homes=$((homes + 1)); }
  done
  [ $runs -gt 0 ] && echo "  finished $runs: avg steps $(echo "scale=1; $tot/$runs" | bc), avg deaths $(echo "scale=1; $dead/$runs" | bc), homes $homes/$runs"
done
