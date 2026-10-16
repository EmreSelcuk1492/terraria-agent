#!/usr/bin/env bash
# After sim/night-c.sh: the station-levelling + land-before-reach build (mods-night4) through nav, survival and the
# LLM track (2026-10-16).
set -uo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"; log="$HOME/Library/Caches/terraria-sim/night"
export SIM_MOD="$HOME/Library/Caches/terraria-sim/mods-night4/TerrariaAgentBridge.tmod"
until grep -q 'all done' "$log/queue.log" 2>/dev/null; do sleep 60; done
sleep 10
for suite in nav survival; do
  echo "$(date '+%H:%M:%S') $suite-d start" >> "$log/queue.log"
  rep=3; [ "$suite" = nav ] && rep=1
  "$here/sim/scenarios.sh" 2 --repeat $rep --tag $suite > "$log/$suite-d.log" 2>&1
  echo "$(date '+%H:%M:%S') $suite-d exit $?" >> "$log/queue.log"; sleep 10
done
echo "$(date '+%H:%M:%S') llm night4 start" >> "$log/queue.log"
REC=1 TA_RENDER=1 REP=night4 "$here/sim/llm_ab.sh" primitive --speed 4 > "$log/llm-night4.log" 2>&1
echo "$(date '+%H:%M:%S') llm night4 exit $?" >> "$log/queue.log"
docker rm -f tsim-llm >/dev/null 2>&1
echo "$(date '+%H:%M:%S') d done" >> "$log/queue.log"
