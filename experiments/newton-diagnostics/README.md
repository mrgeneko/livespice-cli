# Newton diagnostics and convergence aids (branch `newton-linesearch`)

`livespice_cli` now reports how healthy each render's Newton solve was, can show why it was not, and can
rescue solves that are not converging. Measured on `optiplex7010`, 2026-10-02/03.

## What was added

| Feature | Default | Use |
|---|---|---|
| Unconverged-solve counters | always on, one stderr line per render | `newton: solves=N unconverged=M (x%) severe=K first_sample=S last_sample=L max_abs_output=V line_search=N` |
| `severe` | part of the line above | unconverged solves whose last step was still larger than 1 (V or A); the rest are harmless chatter at a rounding floor |
| Magnitude check | on, 1e6 V | a finite output beyond the limit raises `SimulationDiverged` (previously only NaN/inf did) |
| `--magnitude-limit V` | 1e6 | `0` restores the old behaviour |
| `--trace-newton K` | off | per-iteration trajectory of the first K unconverged solves; slows the solve |
| `--tap NET` | off | render an internal net's voltage instead of a Speaker (also `"tap"` in `--jobs` lines) |
| `--line-search N` | 8 (on); `0` = plain Newton | residual-monitored backtracking for solves that are not converging, up to N halvings (8 works) |
| `--line-search-after K` | 20 | plain Newton iterations before backtracking may engage |
| `--line-search-slack X` | 0.1 | relative growth of the squared residual norm tolerated before a step is undone |
| `--trust-region V` | off, EXPERIMENTAL | scale the whole Newton step when its norm exceeds V volts; the right V depends on the circuit |

An "unconverged" solve is one whose iteration cap ran out before the step test passed. Line search is on by default (`--line-search 0` restores plain Newton): output differs from the previous
build only for circuits with solves that exceed 20 iterations.

## How the line search works

After each Newton update, the next iteration's matrix assembly gives the squared residual norm at the new
point for free. If the iteration number is past `--line-search-after` and that norm grew by more than the
slack, the update is undone and a halved step from the saved point is taken, up to N times. A solve that is
already below the step tolerance is never backtracked, and the convergence test always uses the full
(unscaled) step. Solves that converge within K iterations run exactly as before.

Implementation note: re-evaluating the residual at a trial point from the compiled expressions does not
work. They reuse shared temporaries computed during the assembly, so they return the old point's value
(the first prototype always accepted or always rejected for that reason).

## What the measurements showed

* **Bluesbreaker hot window (os 8, 256 iterations):** the three Gain=1.0 corners ran away (peaks 1e5 V) because 2 solves per render
  were severe. 4096 iterations fixes it. `--line-search 8`: 0 unconverged on all five corners tested, outputs equal to the
  1024-iteration reference (ESR 7e-19 to 1.5e-9), at 256 iterations.
* **Deluxe Reverb `sag ac c12q tubes2`, all-max corner, os 16:** diverged at about 2.28 s. The failing solves (first at sample 63002)
  are a Newton two-cycle on a power-tube plate: +525 V <-> -253 V, step +-778 V, for all 4096 iterations. More iterations do not help;
  without the magnitude check the output reached 5e37 V and the render reported success. Residual norms at the two cycle points
  are 0.156 and 0.094. `--line-search 8`: 0 unconverged, peak 28.66 V, and the result matches the `--trust-region 50` render to ESR 4e-11.
* **32-case regression** (8 amps, E645 in two modes, 6 pedals, two knob settings each, os 8, 256 iterations), `--line-search 8` against
  plain Newton: 30 cases within ESR 1e-13 of the 1024-iteration reference (most bit-identical), total run time 989 s against 1031 s.
  The OD-3 at max, whose plain run diverged at 0.85 s, now completes (it differs from the 1024-iteration reference by ESR 0.13 in the
  first second, where that reference holds a 1.39 V spike against an rms of 0.013 V). Nothing got worse.
* **Why `--line-search-after` exists:** an always-on version of the same check was not safe. It fixed the failures but introduced
  hundreds to thousands of unconverged solves on healthy circuits (Twin Reverb, JCM800, Friedman, E645) and shifted the E645 Lead Hi
  render by ESR 0.48, because a full Newton step often raises the residual for an iteration and still converges faster.
* **A fixed `--trust-region` radius is not safe as a default.** With radius 0.1 x the largest `Rail` it was neutral in 26 of 32 cases,
  fixed the OD-3, and made the Twin Reverb worse (42,346 unconverged solves) because the rule gave 5 V for a ~450 V amp.
* **Unconverged counts alone are not a verdict.** The EVH 5150 has about 1000 unconverged solves in every run, all with `severe=0`:
  a reverse-biased rectifier diode (`VD3`, ~300 V) chatters with a 2.4e-3 V step, and the output is identical to the 1024-iteration render.
* The stable Deluxe siblings had about 7000 unconverged solves at the all-max corner under plain Newton; with a trust region or the
  line search their renders shift by ESR 5e-3 to 7e-3, so earlier renders there carried that much error.

Not tested validly: re-solving only the unconverged solves with a clamped or trust-region step. My experimental restart failed even
with no scaling at all, so those experiments say nothing about the idea.

## Test

`test.sh` runs the default-identical, counter, trace, tap, trust-region and line-search checks against fixtures in `~/work` (paths at the top).
