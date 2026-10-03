# JFET triode-region equation fix (branch jfet-triode-fix)

`Circuit/Components/JunctionFieldEffectTransistor.cs` (inherited unchanged from upstream LiveSPICE) computes the
linear-region current as `AbsVds * (2 * Vgds_t0 - 1)`. The Shichman-Hodges / SPICE level-1 form is
`AbsVds * (2 * Vgds_t0 - AbsVds)`. This branch changes that one term.

## Reference
ngspice `src/spicelib/devices/jfet/jfetload.c`, normal mode, default model (b = 1, so Bfac = 0):
triode (vgst >= vds): `cdrain = betap * vds * (2*vgst - vds)`; saturation: `cdrain = betap * vgst * vgst`;
`betap = beta * (1 + lambda * vds)` in both. https://github.com/ngspice/ngspice (path above). The ngspice manual
(JFET DC model) gives the same equations. LiveSPICE\x27s saturation branch, lambda factor and region test already match;
only the `- 1` differs.

## Why the old term is wrong
With the `- 1` the current is discontinuous at the triode/saturation boundary (Vds = Vgs - Vt0) unless Vgs - Vt0 = 1 V,
and negative for Vgs - Vt0 < 0.5 V. Beta 8.2 mA/V^2, Vt0 -0.7 V, lambda 0.015 (the BD-2\x27s 2SK184-GR):

| Vgs (V) | Vds just below Vgs-Vt0 | Id old (mA) | Id new (mA) | Id at Vds just above (mA) |
|---|---|---|---|---|
| 0.00 | 0.700 | 2.3201 | 4.0602 | 4.0602 |
| -0.30 | 0.400 | -0.6599 | 1.3199 | 1.3199 |
| -0.50 | 0.200 | -0.9869 | 0.3290 | 0.3290 |
| -0.60 | 0.100 | -0.6570 | 0.0821 | 0.0821 |

(Old column = just below the boundary; the last column is the saturation value just above it. The new column is continuous with it.)

## Effect on renders (optiplex, os 8, 256 iterations, `--line-search 8` on both builds, 4 s guitar-style input)
Base = branch newton-linesearch (c41aef3), fix = this branch. ESR = fix vs base.

```
circuit                      knob | base              unconv    sev   peak V | fix               unconv    sev   peak V |       ESR    dB rms
Boss BD-2 Blues Driver       low  | ok                   426    172    7.967 | ok                     0      0    7.964 |   2.9e-03      0.24
Boss BD-2 Blues Driver       mid  | ok                   527    166    7.966 | ok                     0      0    7.963 |   2.6e-03      0.24
Boss BD-2 Blues Driver       max  | ok                   267     59    7.948 | ok                     0      0    7.899 |   1.1e-03      0.25
Boss HM-2 Heavy Metal        low  | ok                     0      0    0.000 | ok                     0      0    0.000 |   9.4e-04      0.03
Boss HM-2 Heavy Metal        mid  | ok                     0      0    0.000 | ok                     0      0    0.000 |   1.8e-03      0.04
Boss HM-2 Heavy Metal        max  | ok                     0      0    0.000 | ok                     0      0    0.000 |   3.1e-02      0.17
Boss OD-3 OverDrive          low  | ok                     0      0    0.003 | ok                     0      0    0.003 |   8.1e-06      0.02
Boss OD-3 OverDrive          mid  | ok                     0      0    0.021 | ok                     0      0    0.021 |   1.8e-03     -0.00
Boss OD-3 OverDrive          max  | ok                     0      0    0.066 | ok                     0      0    0.066 |   3.7e-06      0.01
Dumble OTS-183 Full          low  | ok                     0      0    0.190 | ok                     0      0    0.190 |   2.5e-10     -0.00
Dumble OTS-183 Full          mid  | ok                     0      0    1.178 | ok                     0      0    1.178 |   9.2e-11     -0.00
Dumble OTS-183 Full          max  | ok                     0      0    1.522 | ok                     0      0    1.522 |   1.7e-10     -0.00
Dumble OTS-183 Preamp (WIP)  low  | ok                     0      0    1.628 | ok                     0      0    1.628 |   0.0e+00      0.00
Dumble OTS-183 Preamp (WIP)  mid  | ok                     0      0   43.790 | ok                     0      0   43.790 |   0.0e+00      0.00
Dumble OTS-183 Preamp (WIP)  max  | ok                     0      0  182.100 | ok                     0      0  182.100 |   0.0e+00      0.00
Roland JC-120 CH1            low  | DIVERGED@0 s + 0       8      0    0.000 | ok                     0      0    8.344 |       n/a       n/a
Roland JC-120 CH1            mid  | DIVERGED@0 s + 0       8      0    0.000 | DIVERGED@0 s + 1      14      0   30.230 |       n/a       n/a
Roland JC-120 CH1            max  | DIVERGED@0 s + 1      16      1   30.850 | DIVERGED@0 s + 1     245      1   30.990 |       n/a       n/a
```

* BD-2: the Newton two-cycle in the Q10/Q11 compound stage disappears (527 -> 0 unconverged at mid); audio shifts by ESR 1e-3..3e-3 (+0.24 dB).
* HM-2, OD-3: ESR 4e-6..3e-2, no convergence change. Dumble OTS-183 Full: ESR ~1e-10 (the JFETs there stay in saturation). Dumble Preamp (WIP): bit-identical.
* JC-120 CH1: low now renders (it diverged at sample 0); mid and max still diverge at about sample 1536 (peak ~30 V), a separate problem.
* Open: the HM-2 renders peak 0.000 V (silent) both before and after; not examined here. No ngspice cross-check of the BD-2 audio yet.
