# Pentode E1: evidence and fit script (2026-09-30)

The equation change itself now lives in the LiveSPICE fork, not here: `Pentode.Model` (`PentodeModel`: `PlateScaled`, the original
equation and the default, or `Koren`), branch `pentode-koren-model`. A schematic selects it with `Model="Koren"`; one without the
attribute renders exactly as before (checked bit-identical on 8 shipped pentode amps, 16 renders: 6L6GC, 6V6GT, 6550A, EL34, EL84).
There is one `livespice_cli`; `build.sh` builds it.

## Why

`Pentode.cs` computed `E1 = vpk/Kp * Ln1Exp(Kp*(1/Mu + vgk/sqrt(Kvb + vg2k^2)))`, scaled by the PLATE voltage, so plate current rose
roughly as Vpk^Ex with no saturation above the knee (unchanged since the pentode was added, upstream commit 2942445). Koren's
published tetrode form uses the screen voltage: `E1 = Vg2/Kp * ln(1 + exp(Kp*(1/Mu + Vg1/Vg2)))` (Vg2 floored at 1 V in the model).

* The shared 6L6GC set (Mu 8.7, Ex 1.35, Kg1 1460, Kg2 4500, Kp 48, Kvb 12; 36 shipped files) draws about 62-66% of the Svetlana
  SV6L6GC datasheet current at Vp~Vs points. With the original equation no parameters fit the 300 V plate / 200 V screen point, and
  current at 100 V plate / 0 V grid is about 57 mA, so 4x6L6 amps clip at about 21 V peak (about 16 W into 8 ohm).
* With the Koren model and Mu 8.28, Ex 1.35, Kg1 683, Kg2 8436, Kp 300, Kvb 100 (`fit_6l6gc.py`) the rig reads 83.3 / 56.0 / 57.6 /
  73.7 / 55.1 mA at the five datasheet points (datasheet 80.4 / 59.2 / 54.0 / 72.5 / 60.8) and 256 mA at 100 V plate / 0 V grid.
  ENGL Powerball E645 (`parametric-devices/amps`) then clips at 42.9 V peak; the EVH 5150 Power Amp at about 44 V (about 121 W
  sine-equivalent into 8 ohm). The corrected Deluxe Reverb (6V6GT refit) reaches 23.9 W at 4% THD in its `sag ac` build against the
  shipped model's 10.2 W at 10.6%.
* Upstream (dsharlet/LiveSPICE): only PR #121 (pentode/diode models, merged without accuracy validation) and #147 (Koren model
  fixes: grid current and stability) touch this; no issue or PR found on plate-voltage dependence or output power (read through a
  summarising fetch tool, not verbatim).

## Before relying on it

* A parameter set is only valid for the equation it was fitted with. Only the 6L6GC and 6V6GT have Koren-model sets; EL34-JJ, 6550A and
  EL84 do not (their shipped sets were fitted to the original equation and are unchanged).
* The Koren equation was written from memory of Koren's paper; check it against the paper before adopting it fleet-wide.
* An older `livespice_cli` ignores the unknown `Model` attribute and renders a Koren file silently wrongly: rebuild every render machine.

`fit_6l6gc.py` reproduces the fits for both equations (needs numpy and scipy).
