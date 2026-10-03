#!/bin/bash
# Regression checks for the Newton diagnostics (see README.md). Needs a fleet livespice_cli (FLEET), this build (NEW), a parametric-devices
# checkout, and the fixtures bb_hot27.wav, sweep_mono_6s.wav, bc/in_bc.wav in ~/work/tmp/ls-fix.
cd ~/work/tmp/ls-fix; mkdir -p ft
export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
NEW=$HOME/work/tmp/ls-feat/publish/livespice_cli; FLEET=$HOME/work/livespice-cli/publish/livespice_cli
A=$HOME/work/parametric-devices/amps; P=$HOME/work/parametric-devices/pedals
BB="$P/Marshall Bluesbreaker.schx"; DLX="$A/Fender Deluxe Reverb Full (sag ac c12q, tubes2).schx"; DSAC="$A/Fender Deluxe Reverb Full (sag ac).schx"; TS="$P/Ibanez TS-9.schx"
echo "== T1 default output byte-identical to the fleet binary (no flags)"
t1(){ tag=$1; circ=$2; inp=$3; prm=$4; spk=$5; os=$6; it=$7
  $FLEET --input $inp --output ft/f_$tag.wav --circuit "$circ" --params "$prm" --speaker $spk --oversample $os --iterations $it >/dev/null 2>ft/f_$tag.log
  $NEW   --input $inp --output ft/n_$tag.wav --circuit "$circ" --params "$prm" --speaker $spk --oversample $os --iterations $it >/dev/null 2>ft/n_$tag.log
  if cmp -s ft/f_$tag.wav ft/n_$tag.wav; then echo "   $tag: identical"; else echo "   $tag: DIFFERENT"; fi; }
t1 bb_1_0.2 "$BB" bb_hot27.wav "Gain=1,Tone=0.2,Volume=0.6" S_OUT 8 256 &
t1 bb_0.75_0.5 "$BB" bb_hot27.wav "Gain=0.75,Tone=0.5,Volume=0.6" S_OUT 8 256 &
t1 dsac_max "$DSAC" bc/in_bc.wav "Volume=1,Treble=1,Bass=1" S_OUT 8 256 &
t1 ts9 "$TS" bc/in_bc.wav "Drive=1,Tone=0.5,Level=0.5" S1 8 256 &
wait
echo "== T2 counters vs the experiment build (Bluesbreaker, os8): expected it256 -> 26/30/23, it4096 -> 0/0/1"
c(){ g=$1; t=$2; it=$3; $NEW --input bb_hot27.wav --output ft/c_${g}_${t}_$it.wav --circuit "$BB" --params "Gain=$g,Tone=$t,Volume=0.6" --speaker S_OUT --oversample 8 --iterations $it 2>&1 >/dev/null | grep '^newton:' | sed "s/^/   G=$g T=$t it=$it /"; }
for it in 256 4096; do for gt in "1 0.2" "1 0.5" "1 0.8"; do c $gt $it & done; done; wait
echo "== T3 --tap vs the earlier probe tap (Deluxe ac+c12q, os16, it4096), T6 magnitude check, T4 trace"
$NEW --input sweep_mono_6s.wav --output ft/tap_nSpk.wav --circuit "$DLX" --params "Volume=1,Treble=1,Bass=1" --tap nSpk --oversample 16 --iterations 4096 --trace-newton 3 > ft/tap.out 2> ft/tap.err; echo "   exit code $?"
grep -E "iverge|^newton:" ft/tap.err | cut -c1-200
grep -E "^newton-trace: solve" ft/tap.err | cut -c1-200
grep -E "^newton-trace:   it=(1|2|3|4) " ft/tap.err | head -6 | cut -c1-200
echo "== T5 --trust-region 50 on the failing Deluxe build (expect 0 unconverged, peak 28.66)"
$NEW --input sweep_mono_6s.wav --output ft/tr50.wav --circuit "$DLX" --params "Volume=1,Treble=1,Bass=1" --speaker S_OUT --oversample 16 --iterations 256 --trust-region 50 2>&1 >/dev/null | grep -E "iverge|^newton:" | cut -c1-200
