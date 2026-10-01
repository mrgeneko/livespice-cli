#!/usr/bin/env python3
"""Fit 6L6GC pentode parameters to the Svetlana SV6L6GC datasheet, for both E1 forms.

  current  : LiveSPICE Pentode.cs as shipped  E1 = Vpk/Kp * ln(1+exp(Kp*(1/Mu + Vgk/sqrt(Kvb+Vg2k^2))))
  koren    : Koren's published tetrode form   E1 = Vg2/Kp * ln(1+exp(Kp*(1/Mu + Vg1/Vg2)))   (pentode_koren.patch)
  both     : Ip = E1^Ex / Kg1 * atan(Vpk/Kvb),  Ig2 = E1^Ex / Kg2

The Python replica matches the livespice_cli rig (tools/tube_fit.py in parametric-devices) to ~0.4%.

Datasheet points (per tube; the datasheet's two-tube figures halved), Svetlana SV6L6GC
(https://www.drtube.com/datasheets/6l6gc-sed2001.pdf):
  class A  250/250 V, Rk 167 ohm: Ip 75 mA, Ig2 5.4 mA   (Vg1 = -(Ip+Ig2)*Rk = -13.4 V)
  class A  200/200 V, Rk 186 ohm: Ip 55, Ig2 4.2         (Vg1 -11.0 V)
  class A  300/200 V, Rk 218 ohm: Ip 51, Ig2 3.0         (Vg1 -11.8 V)
  push-pull A1 270/270 V, -17.5 V: 134/2 = 67 mA, 11/2 = 5.5 mA
  push-pull AB1 450/400 V, -37 V: 116/2 = 58 mA, 5.6/2 = 2.8 mA
Not in the datasheet: peak plate current at low plate voltage. The large-signal rows printed below are model
predictions, not datasheet values; a real 6L6GC gives roughly 250-300 mA near 100 V plate.

Usage: python3 fit_6l6gc.py
"""
import numpy as np
from scipy.optimize import least_squares

sp = lambda x: x if x > 50 else np.log1p(np.exp(x))


def current(P, Va, Vs, Vg):
    Mu, Ex, Kg1, Kg2, Kp, Kvb = P
    E1 = Va / Kp * sp(Kp * (1 / Mu + Vg / np.sqrt(Kvb + Vs ** 2)))
    i = E1 ** Ex if E1 > 0 else 0
    return i / Kg1 * np.arctan(Va / Kvb), i / Kg2


def koren(P, Va, Vs, Vg):
    Mu, Ex, Kg1, Kg2, Kp, Kvb = P
    E1 = Vs / Kp * sp(Kp * (1 / Mu + Vg / Vs))
    i = E1 ** Ex if E1 > 0 else 0
    return i / Kg1 * np.arctan(Va / Kvb), i / Kg2


# Va, Vg2, Vg1, Ip mA, Ig2 mA
DATASHEET = [(250, 250, -13.4, 75, 5.4), (200, 200, -11.0, 55, 4.2), (300, 200, -11.8, 51, 3.0),
             (270, 270, -17.5, 67, 5.5), (450, 400, -37, 58, 2.8)]


def fit(model, pts, ex=1.35):
    def res(p):
        Mu, Kp, lk1, lk2, Kvb = p
        P = (Mu, ex, np.exp(lk1), np.exp(lk2), Kp, Kvb)
        r = []
        for Va, Vs, Vg, Ip, Is in pts:
            a, b = model(P, Va, Vs, Vg)
            r += [np.log(max(a * 1000, 1e-6) / Ip), np.log(max(b * 1000, 1e-6) / Is)]
        return r
    best = None
    for mu in (5, 8, 11):
        for kp in (10, 30, 80, 200):
            for kvb in (8, 20, 50):
                s = least_squares(res, [mu, kp, np.log(1000), np.log(3000), kvb],
                                  bounds=([4, 5, 3, 3, 3], [12, 300, 12, 14, 100]))
                if best is None or s.cost < best.cost:
                    best = s
    Mu, Kp, lk1, lk2, Kvb = best.x
    return (Mu, ex, np.exp(lk1), np.exp(lk2), Kp, Kvb), best.cost


if __name__ == "__main__":
    for name, model in (("current LiveSPICE form", current), ("published Koren form", koren)):
        for lab, pts in (("all 5 points", DATASHEET),
                         ("4 points, no 300/200 V", [d for d in DATASHEET if d[:2] != (300, 200)])):
            F, cost = fit(model, pts)
            print(f"\n{name}, {lab}: Mu {F[0]:.2f} Ex {F[1]} Kg1 {F[2]:.0f} Kg2 {F[3]:.0f} Kp {F[4]:.0f} Kvb {F[5]:.0f} (cost {cost:.3f})")
            print("  datasheet -> fitted (Ip/Ig2 mA): " + "  ".join(
                f"{Ip:.0f}/{Is:.1f}->{1000 * a:.0f}/{1000 * b:.1f}"
                for (Va, Vs, Vg, Ip, Is) in DATASHEET for a, b in [model(F, Va, Vs, Vg)]))
            print("  model Ip at Vg2=425 (mA): " + "  ".join(
                f"Va{Va}/Vg{Vg:+d}: {1000 * model(F, Va, 425, Vg)[0]:.0f}"
                for Va, Vg in [(440, 0), (200, 0), (100, 0), (60, 0), (100, 10), (60, 10)]))
