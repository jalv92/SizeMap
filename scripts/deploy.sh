#!/usr/bin/env bash
# Deploy SizeMap into NinjaTrader 8's Custom folder.
#
# The repo is the source of truth, always. Never edit inside Custom/ — a change there
# is invisible to git and dies on the next deploy.
#
# A task is not finished until the files are HERE. Compiling with nt8c and committing
# does not count: Javier cannot run it from the repo.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NT8="/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom"
IND="$NT8/Indicators/SizeMap"
STY="$NT8/ChartStyles"
STR="$NT8/Strategies"

[ -d "$NT8" ] || { echo "NT8 Custom folder not found: $NT8" >&2; exit 1; }
mkdir -p "$IND" "$STY" "$STR"

fail=0

deploy() {   # deploy <src> <dest-dir>
  local src="$1" dst="$2/$(basename "$1")"
  cp -f "$src" "$dst"                      # a real copy, never a symlink
  if cmp -s "$src" "$dst"; then
    echo "  ok    $(basename "$src")  ->  ${dst#$NT8/}"
  else
    echo "  FAIL  $(basename "$src")  differs after copy"; fail=1
  fi
}

echo "SizeMap -> NinjaTrader 8"
# Route by the NAMESPACE the file declares, not by a hand-kept list of names: the list
# silently omitted SizeMapRecorder.cs, and a missing sibling is CS0246 for the whole
# Custom.dll — the same blast radius as a duplicate type, from a typo nobody can see.
for f in "$REPO"/nt8/*.cs; do
  [ -f "$f" ] || continue
  if grep -q '^namespace NinjaTrader\.NinjaScript\.ChartStyles' "$f"; then
    deploy "$f" "$STY"
  elif grep -q '^namespace NinjaTrader\.NinjaScript\.Strategies' "$f"; then
    deploy "$f" "$STR"
  else
    deploy "$f" "$IND"
  fi
done
if [ -d "$REPO/engine" ]; then
  for f in "$REPO"/engine/*.cs; do [ -f "$f" ] && deploy "$f" "$IND"; done
fi

# NT8 compiles every .cs under Custom/ into ONE assembly, so a duplicated basename
# across Indicators/ and Strategies/ is a duplicate-type error that takes the whole
# Custom.dll down — every indicator and strategy on the machine, not just ours.
echo "checking for basename collisions..."
dupes=$(
  { find "$NT8/Indicators" "$NT8/Strategies" "$NT8/AddOns" "$NT8/ChartStyles" \
      -name 'SizeMap*.cs' -printf '%f\n' 2>/dev/null; } | sort | uniq -d
)
if [ -n "$dupes" ]; then
  echo "  FAIL  duplicated basenames:"; echo "$dupes" | sed 's/^/        /'; fail=1
else
  echo "  ok    no SizeMap basename appears twice"
fi

# TradingRadar.Engine is already compiled into Custom.dll at AddOns/LiquidityRadar/.
# Anything we ship under that namespace is CS0101 for every type in it.
if grep -rlq 'namespace[[:space:]]\+TradingRadar' "$IND" 2>/dev/null; then
  echo "  FAIL  a deployed file declares namespace TradingRadar — collides with"
  echo "        AddOns/LiquidityRadar/. Rewrite it to SizeMap.Engine."; fail=1
else
  echo "  ok    no TradingRadar namespace under Indicators/SizeMap"
fi

echo "deployed: $(ls -1 "$IND" | wc -l) file(s) in Indicators/SizeMap, $(ls -1 "$STY"/SizeMap*.cs 2>/dev/null | wc -l) chart style(s), $(ls -1 "$STR"/SizeMap*.cs 2>/dev/null | wc -l) strategy/ies"
exit $fail
