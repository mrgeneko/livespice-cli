# Newton diagnostics (branch `newton-diagnostics`)

`livespice_cli` now reports how healthy each render's Newton solve was, and can show why it was not.
Measured on `optiplex7010` on 2026-10-02; see "What the measurements showed".

## What was added

| Feature | Default | Use |
|---|---|---|
| Unconverged-solve counters | always on, one line on stderr per render | `newton: solves=N unconverged=M (x%) first_sample=S last_sample=L max_abs_output=V` |
| Magnitude check | on, 1e6 V | a finite output beyond the limit raises `SimulationDiverged` (previously only NaN/inf did) |
| `--magnitude-limit V` | 1e6 | `0` restores the old behaviour |
| `--trace-newton K` | off | per-iteration trajectory (largest step, largest unknown, dominant unknown) of the first K unconverged solves; slows the solve |
| `--tap NET` | off | render an internal net's voltage instead of a Speaker (also `"tap"` in `--jobs` lines) |
| `--trust-region V` | off, EXPERIMENTAL | scale the whole Newton step when its norm exceeds V volts |

An "unconverged" solve is one whose iteration cap ran out before the step test passed. With every
flag off, output is byte-identical to the previous build (checked on four circuits, `test.sh`).

## What the measurements showed

* **Bluesbreaker hot window (os 8, 256 iterations):** the three Gain=1.0 corners ran away (peaks 1e5 V). Cause: 23-30 unconverged
  solves of 10.4 M, early in the render. 4096 iterations: 0-1 unconverged, bounded output.
* **Deluxe Reverb `sag ac c12q tubes2`, all-max corner, os 16:** diverged at about 2.28 s. Its failing solves (first at sample 63002)
  are a Newton two-cycle on a power-tube plate: +525 V <-> -253 V, step +-778 V, for all 4096 iterations. More iterations do not help:
  without the magnitude check the output reached 5e37 V and the render reported success.
* **`--trust-region 50`** removed that divergence (0 unconverged, peak 28.66 V, identical at 256 and 4096 iterations) and shifted the
  two stable sibling builds by ESR 5e-3 to 7e-3 (their old renders had ~7000 unconverged solves).
* **Across 32 circuit/knob cases** (8 amps, E645 in two modes, 6 pedals; radius 0.1 x the largest `Rail`): neutral in 26 cases
  (ESR <= 4e-8 against a 1024-iteration reference), fixed one (OD-3 at max, whose control diverged), and made one amp worse
  (Twin Reverb: 42,346 unconverged solves, because the rule gave a 5 V radius for a ~450 V amp). A fixed radius is
  therefore not safe as a default; the useful radius depends on the circuit's voltage scale.
* Unconverged-solve counts alone are not a verdict: the EVH 5150 had about 1000 in the control, trust-region and 1024-iteration
  renders, with identical output.

Not tested validly: re-solving only the unconverged solves with a clamped or trust-region step. My experimental restart
failed even with no scaling at all, so those experiments say nothing about the idea.

## Test

`test.sh` runs the default-identical, counter, trace, tap and trust-region checks against fixtures in `~/work` (paths at the top).
