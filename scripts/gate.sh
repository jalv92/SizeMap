#!/usr/bin/env bash
# The real compile gate for SizeMap.
#
# `nt8c check <file>` is useless here and always will be: it compiles ONE file in
# isolation, and every SizeMap file references sibling types (ColumnRing, Palette,
# RasterView, DepthCell, RawFile). Every one of those is a false CS0246.
#
# NinjaTrader compiles every .cs under Custom/ into ONE assembly, so the honest
# equivalent is to stage the repo in that shape and build it as one — which is what
# `nt8c build --custom-dir` does, with NT8's own compiler and reference set.
#
# Usage: scripts/gate.sh [--full]
#   default : stage SizeMap alone. Fast (~1 s). Catches everything internal.
#   --full  : stage SizeMap ON TOP of the live Custom/ tree. Slower (~4 s) but it is
#             the only run that can find a CS0101 collision with the 266 files already
#             on the machine — which would take down every indicator, not just ours.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIVE="/mnt/c/Users/javlo/Documents/NinjaTrader 8/bin/Custom"
STAGE="$(mktemp -d)/Custom"
trap 'rm -rf "$(dirname "$STAGE")"' EXIT

mkdir -p "$STAGE/Indicators/SizeMap" "$STAGE/ChartStyles"

if [ "${1:-}" = "--full" ]; then
    # Compile the LIVE Custom/ tree, in place. Not a staged copy: nt8c still aliases the real
    # NinjaTrader.Custom.dll when the sources live somewhere else, and every file then fails on
    # `extern alias NtCustom` — a tooling artifact that looks exactly like 2,600 real errors.
    # Building the live tree has no such problem and is literally what F5 will compile.
    # Requires scripts/deploy.sh to have run first.
    if [ ! -d "$LIVE/Indicators/SizeMap" ]; then
        echo "run scripts/deploy.sh first — --full compiles the deployed tree, not the repo" >&2
        exit 1
    fi
    TARGET="$LIVE"
else
    TARGET="$STAGE"
fi

if [ "$TARGET" = "$STAGE" ]; then
cp "$REPO"/engine/*.cs "$STAGE/Indicators/SizeMap/"
for f in "$REPO"/nt8/*.cs; do
    if grep -q '^namespace NinjaTrader\.NinjaScript\.ChartStyles' "$f"; then
        cp "$f" "$STAGE/ChartStyles/"
    else
        cp "$f" "$STAGE/Indicators/SizeMap/"
    fi
done
fi

# Through a file, not an argument: a --full run over 266 files produces a JSON blob
# far past ARG_MAX, and the failure looks like "Argument list too long" rather than
# like a compile error.
nt8c build --custom-dir "$TARGET" --no-emit --agent > "$STAGE/../out.json" 2>&1 || true

python3 - "$STAGE/../out.json" <<'PY'
import json, sys, re
raw = open(sys.argv[1]).read()
try:
    d = json.loads(raw)
except Exception:
    print(raw); sys.exit(1)
res = d.get("results", {})
errs = res.get("errors", []) or []
n = d.get("meta", {}).get("files_compiled", "?")
# On a --full run the stock @*.cs files spray errors NT8's own F5 compiles fine, so only
# errors naming OUR files count. Anything else is noise from the tree, not from us.
# NT8 appends a "NinjaScript generated code" region to every indicator in Custom/ that
# references a partial-class member nt8c cannot reconstruct. Measured: 266 of these across
# 129 files, including NinjaTrader's own @ADL.cs and @BarTimer.cs. Not ours, never was.
errs = [e for e in errs if not (e.get("code") == "CS0103" and "'indicator'" in e.get("message",""))]
mine = [e for e in errs if re.search(r'/(SizeMap[^/]*|Palette|ColumnRing|Rasterizer|RawRecord|Primitives|RadarConfig|BookMirror|WallDetector|WallTracker|LiquidityMemory|EpisodeClassifier|ConsumptionTracker|DepthBaseline|BigPrintTracker)\.cs$', e.get("file",""))]
dup = [e for e in errs if e.get("code") == "CS0101"]
print("files compiled: %s | errors total: %d | in SizeMap files: %d | CS0101: %d"
      % (n, len(errs), len(mine), len(dup)))
for e in (mine + dup)[:25]:
    print("  %s(%s,%s): %s %s" % (e.get("file","?").split("/")[-1], e.get("line"),
                                  e.get("col"), e.get("code"), e.get("message")))
sys.exit(1 if (mine or dup) else 0)
PY
