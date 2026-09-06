# livespice_cli

A minimal command-line wrapper around [LiveSPICE](https://github.com/dsharlet/LiveSpice)'s own
C# circuit simulator. Give it a `.schx` schematic, an input WAV, and a set of parameter values —
it runs LiveSPICE's simulator and writes the output WAV.

```sh
livespice_cli \
    --input  in.wav  --output out.wav \
    --circuit "path/to/circuit.schx" \
    --params  "Gain=0.5,Treble=0.7" \
    --speaker S1
```

`--jobs N` renders multiple parameter permutations in one process, amortizing schematic-parse
and assembly-load cost across N runs — useful for batch dataset generation.

**`--params` matching is case-SENSITIVE, and an unmatched name is a HARD ERROR.** `Vol` is not
`vol`; passing the latter fails fast, listing the circuit's real pot/switch names, rather than
silently rendering at defaults (which would make every "swept" permutation identical).

## Why this exists

Most SPICE tooling either requires you to script the simulator's own GUI/library API, or embeds
its own reimplementation of circuit solving. This is neither — it's the smallest possible CLI
shim around LiveSPICE's *actual* solver, built from a copy of upstream carrying exactly one
upstream-authored patch (see **Divergence from upstream**). That makes it
useful as an independent reference: if you're building anything that simulates or transforms
`.schx` circuits (an alternate solver, a code generator, a dataset pipeline), you can diff your
output against this binary's and know any disagreement is a real bug, not two implementations of
the same bug agreeing with each other.

## Build

```sh
git submodule update --init --recursive
./build.sh          # → publish/livespice_cli
```

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (net10.0). Note `--recursive`:
`extern/LiveSPICE` has its own nested submodule (`ComputerAlgebra`) — a plain `--init` without
`--recursive` leaves it empty and the build fails with `CS0246: 'Expression' could not be found`.

## Divergence from upstream

`extern/LiveSPICE` is **not** pristine upstream. It sits on branch `capacitor-current-unknown`,
which is upstream `master` plus a **single cherry-picked upstream commit**:

| | |
|---|---|
| commit | `5398a63` — *"Add a variable to the system for capacitor currents…"* |
| author | dsharlet (upstream maintainer) — cherry-picked, not written by us |
| upstream status | **unmerged**; lives on open PR [#238](https://github.com/dsharlet/LiveSPICE/pull/238) |
| change | one line in `Circuit/Components/Capacitor.cs`: uncomments `i = Mna.AddUnknownEqualTo("i" + Name, i)` |

**Why we carry it.** Stock LiveSPICE throws
`System.Exception: Failed to eliminate differentials from system of equations` whenever a circuit
enables `Triode.SimulateCapacitances` (interelectrode Cgp/Cgk/Cpk) — even on a single tube. The
patch adds an intermediate unknown for capacitor currents so the solver no longer has to eliminate
differentials before discretization, which is exactly the failing step. Upstream issue
[#260](https://github.com/dsharlet/LiveSPICE/issues/260) is the same exception from a different
cause, so the limitation is broader than the capacitance flag.

**Measured effects** (EVH 5150 full-sag build, 125 components, 2026-09-06):

- `SimulateCapacitances=True` renders where stock throws.
- Existing circuits shift by **0.0031% rel-rms** — numerical noise. Adopting the patch does *not*
  invalidate previously rendered datasets.
- Cost: **~+6%** render time; **~+9%** more with capacitances actually enabled.
- Zero-input residue improves (→ exactly 0 on that circuit).

> **The binary in `publish/` has NOT been rebuilt with this patch.** The source pin diverges; the
> shipped binary is still stock. This is deliberate — rebuilding invalidates nothing, but the fleet
> renders against the existing binary and a rebuild was judged not worth the churn. Run `./build.sh`
> when you actually want the fix, and expect the ~6% cost and the 0.003% output shift from that
> point on.

**Re-pinning.** If you bump the submodule to a newer upstream commit, re-apply this cherry-pick, or
drop it deliberately and know that `SimulateCapacitances` goes back to crashing.

## Pinning

`extern/LiveSPICE` is a submodule pinned to a specific upstream commit. Bumping the pin can
change simulation output for existing circuits — if you depend on stable output for regression
testing, pin your own consumer to a specific commit of *this* repo, not just `main`.

## License

MIT — see `LICENSE`. LiveSPICE itself is © Dillon Sharlet and contributors, also MIT-licensed;
see `extern/LiveSPICE/LICENSE` after checking out the submodule.
