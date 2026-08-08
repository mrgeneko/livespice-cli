#!/usr/bin/env bash
# Build livespice_cli against PRISTINE LiveSPICE (extern/LiveSPICE).
# See README.md — never patch the submodule in place.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [ ! -f "$HERE/extern/LiveSPICE/Circuit/Circuit.csproj" ]; then
    echo "  extern/LiveSPICE is empty — run: git submodule update --init --recursive" >&2
    exit 1
fi

# Guard against an accidentally-patched submodule. If you've forked this repo and patched
# the simulator on purpose, that's fine — just know the binary is no longer an independent
# reference implementation once you do.
if grep -rq "EMITTER FORK\|SymbolicWiper" "$HERE/extern/LiveSPICE/Circuit/Components/Potentiometer.cs" 2>/dev/null; then
    echo "  WARNING: extern/LiveSPICE contains fork markers — this is no longer pristine" \
         "upstream LiveSPICE. If unintentional, re-pin the submodule." >&2
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
