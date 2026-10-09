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
shim around LiveSPICE's *actual* solver, built from a lightly modified fork of upstream (see
"The LiveSPICE fork" below). That makes it useful as an independent reference: if you're building
anything that simulates or transforms `.schx` circuits (an alternate solver, a code generator, a
dataset pipeline), you can diff your output against this binary's, and a disagreement is a real
bug rather than two implementations of the same bug agreeing with each other.

## Build

```sh
git submodule update --init --recursive
./build.sh          # → publish/livespice_cli
```

Requires the [.NET SDK](https://dotnet.microsoft.com/download) (net10.0). Note `--recursive`:
`extern/LiveSPICE` has its own nested submodule (`ComputerAlgebra`) — a plain `--init` without
`--recursive` leaves it empty and the build fails with `CS0246: 'Expression' could not be found`.

## The LiveSPICE fork

`extern/LiveSPICE` is [a fork](https://github.com/mrgeneko/LiveSPICE) of upstream LiveSPICE, not an
unmodified copy. It is upstream at `b2a4bb0` plus the commits that `./build.sh` counts when it
runs. The additions are: opt-in interelectrode capacitance and a Koren model for the pentode, a
fix to the center-tap transformer's ampere-turns equation, the JFET triode-region equation, the
export-to-C tool, and Newton-health counters with an optional line search and trust region. The
transformer and JFET fixes change results for existing circuits, and the line search is on by
default in `livespice_cli` (`--line-search 0` restores plain Newton), so the binary is not
upstream's solver; do not describe its results as "pristine LiveSPICE".

## Pinning

`extern/LiveSPICE` is a submodule pinned to a specific commit of that fork. Bumping the pin can
change simulation output for existing circuits — if you depend on stable output for regression
testing, pin your own consumer to a specific commit of *this* repo, not just `main`.

## License

MIT — see `LICENSE`. LiveSPICE itself is © Dillon Sharlet and contributors, also MIT-licensed;
see `extern/LiveSPICE/LICENSE` after checking out the submodule.
