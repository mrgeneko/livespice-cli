#!/usr/bin/env bash
# Build livespice_cli against the pinned LiveSPICE fork (extern/LiveSPICE).
# The fork is upstream (b2a4bb0) plus a small set of solver/model commits; see README.md.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ ! -f "$HERE/extern/LiveSPICE/Circuit/Circuit.csproj" ]; then
    echo "  extern/LiveSPICE is empty — run: git submodule update --init --recursive" >&2
    exit 1
fi

# Report how far the pinned submodule is from upstream. This is a fork, not pristine upstream:
# it adds Newton-health counters, line search, the Koren pentode model and a few model fixes.
# A binary built from it is therefore NOT an unmodified-upstream reference.
UPSTREAM_BASE=b2a4bb0
if git -C "$HERE/extern/LiveSPICE" cat-file -e "$UPSTREAM_BASE^{commit}" 2>/dev/null; then
    N="$(git -C "$HERE/extern/LiveSPICE" rev-list --count "$UPSTREAM_BASE..HEAD" 2>/dev/null || echo ?)"
    echo "  LiveSPICE fork: $(git -C "$HERE/extern/LiveSPICE" rev-parse --short HEAD), $N commit(s) beyond upstream $UPSTREAM_BASE"
fi

# ReadyToRun: AOT-compile IL to native code at publish, cutting per-invocation JIT
# warm-up. This binary is typically spawned once per render (thousands of times across a
# batch job), so startup time is a real cost. R2R needs a concrete RuntimeIdentifier;
# detect the host's, and fall back to the portable publish if detection fails.
# (Full NativeAOT is NOT possible: the solver Lambda.Compile()s at runtime.)
RID="$("${DOTNET:-dotnet}" --info 2>/dev/null | awk '/RID:/{print $2; exit}')"
if [ -n "$RID" ]; then
    "${DOTNET:-dotnet}" publish "$HERE/livespice_cli" -c Release -o "$HERE/publish" --nologo -v q \
        -r "$RID" --self-contained false -p:PublishReadyToRun=true
else
    echo "  (could not detect host RID — portable publish, no ReadyToRun)" >&2
    "${DOTNET:-dotnet}" publish "$HERE/livespice_cli" -c Release -o "$HERE/publish" --nologo -v q
fi
echo "  → $HERE/publish/livespice_cli"
