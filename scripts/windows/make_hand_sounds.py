"""Generates the sounds of things in the hand (2026-11-11): a film bag taken, turned and set down, a carton tapped.

Synthesized like make_game_sounds.py, nothing downloaded. A film bag is a run of very short bright clicks, many at
first and fewer as the film settles, over a little hiss of the film moving. A carton is a short hollow knock.
Each sound has several takes; the game picks one at random and shifts its pitch a little, so no two grabs sound alike.

Usage:
  python scripts/windows/make_hand_sounds.py <out_dir> [<review_dir>]
<out_dir> is Assets/Audio/Generated. With <review_dir>, a spectrogram sheet and a table of numbers are written there
(the sounds have not been listened to by whoever wrote this; the sheet is what was looked at).
"""
import os
import sys

import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, sosfilt

SR = 44100


def band(x, lo=None, hi=None, order=4):
    if lo and hi:
        sos = butter(order, [lo, hi], btype='band', fs=SR, output='sos')
    elif hi:
        sos = butter(order, hi, btype='low', fs=SR, output='sos')
    else:
        sos = butter(order, lo, btype='high', fs=SR, output='sos')
    return sosfilt(sos, x)


def finish(y, peak_db, fade=0.003, press=0.0):
    """Trims the ends and sets the peak. press > 0 squeezes the loudest clicks toward the rest (a film bag is all
    spikes: left alone it peaks high and still sounds faint)."""
    y = y - np.mean(y)
    n = int(fade * SR)
    y[:n] *= np.linspace(0, 1, n)
    y[-n:] *= np.linspace(1, 0, n)
    y = y / (np.max(np.abs(y)) + 1e-9)
    if press > 0:
        y = np.tanh(press * y) / np.tanh(press)
    return y * 10 ** (peak_db / 20)


def film(seconds, clicks_per_second, settle, seed, hiss=0.22, bursts=((0.0, 1.0),)):
    """Film crackling: clicks whose rate and loudness fall off after each burst.

    bursts: (start in seconds, strength) of each squeeze of the film.
    """
    rng = np.random.default_rng(seed)
    n = int(seconds * SR)
    t_axis = np.arange(n) / SR
    # How lively the film is over time: the sum of the bursts, each dying away.
    lively = np.zeros(n)
    for start, strength in bursts:
        after = np.clip(t_axis - start, 0, None)
        lively += strength * np.exp(-after / settle) * (t_axis >= start)
    clicks = np.zeros(n)
    t = 0.0
    while True:
        i = int(t * SR)
        if i >= n:
            break
        rate = clicks_per_second * (0.12 + lively[i])
        t += rng.exponential(1.0 / rate)
        i = int(t * SR)
        if i >= n:
            break
        m = int(rng.uniform(0.0006, 0.0035) * SR)
        grain = rng.standard_normal(m) * np.exp(-np.arange(m) / (m * 0.3))
        loud = rng.lognormal(-0.6, 0.75) * (0.25 + lively[i])
        end = min(n, i + m)
        clicks[i:end] += loud * grain[:end - i]
    clicks = band(clicks, 2200, 15000)
    # The film itself moving: quieter, lower, following the same liveliness.
    moving = band(rng.standard_normal(n), 900, 6000) * np.clip(lively, 0, 1.2)
    return clicks + hiss * moving * (np.max(np.abs(clicks)) / (np.max(np.abs(moving)) + 1e-9))


def knock(seed, body=(310.0, 560.0), ring=0.028, click=0.5):
    """A carton tapped: a low thump of the board, two short hollow tones of the box, a small click of the contact."""
    rng = np.random.default_rng(seed)
    n = int(0.11 * SR)
    t = np.arange(n) / SR
    thump = band(rng.standard_normal(n), 140, 900) * np.exp(-t / 0.016)
    hollow = sum(w * np.sin(2 * np.pi * f * (1 + rng.uniform(-0.03, 0.03)) * t) for f, w in zip(body, (1.0, 0.55))) * np.exp(-t / ring)
    contact = band(rng.standard_normal(n), 1800, 6500) * np.exp(-t / 0.004)
    return thump / np.max(np.abs(thump)) + 0.7 * hollow / np.max(np.abs(hollow)) + click * contact / np.max(np.abs(contact))


def bag_down(seed):
    """A bag set down: the soft weight of it landing, then the film settling."""
    rng = np.random.default_rng(seed)
    settle = film(0.2, 420, 0.035, seed + 1, hiss=0.3, bursts=((0.012, 1.0),))
    n = len(settle)
    t = np.arange(n) / SR
    weight = band(rng.standard_normal(n), 70, 320) * np.exp(-t / 0.03)
    return weight / np.max(np.abs(weight)) + 0.55 * settle / np.max(np.abs(settle))


def sounds():
    return {
        # Taken in the hand: a burst that settles in a quarter of a second. The third take is two squeezes.
        'sfx_crinkle_1': finish(film(0.28, 1500, 0.06, 101, hiss=0.3), -7, press=4),
        'sfx_crinkle_2': finish(film(0.23, 1800, 0.045, 102, hiss=0.3), -7, press=4),
        'sfx_crinkle_3': finish(film(0.34, 1300, 0.05, 103, hiss=0.3, bursts=((0.0, 1.0), (0.13, 0.7))), -7, press=4),
        # Turned or squeezed while held: shorter, thinner.
        'sfx_rustle_1': finish(film(0.11, 900, 0.03, 201, hiss=0.35), -11, press=3),
        'sfx_rustle_2': finish(film(0.14, 750, 0.04, 202, hiss=0.35), -11, press=3),
        'sfx_rustle_3': finish(film(0.09, 1100, 0.022, 203, hiss=0.35), -11, press=3),
        'sfx_rustle_4': finish(film(0.13, 820, 0.035, 204, hiss=0.35, bursts=((0.0, 0.8), (0.06, 0.6))), -11, press=3),
        'sfx_carton_1': finish(knock(301), -6),
        'sfx_carton_2': finish(knock(302, body=(275.0, 610.0), ring=0.024, click=0.6), -6),
        'sfx_bag_down_1': finish(bag_down(401), -6),
        'sfx_bag_down_2': finish(bag_down(402), -6),
    }


def review(made, folder):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt

    os.makedirs(folder, exist_ok=True)
    names = sorted(made)
    fig, axes = plt.subplots(len(names), 2, figsize=(11, 1.5 * len(names)), gridspec_kw={'width_ratios': [1, 1.4]})
    lines = ['name,seconds,peak_db,rms_db,centroid_hz,share_above_2khz,clicks']
    for row, name in enumerate(names):
        y = made[name]
        t = np.arange(len(y)) / SR
        axes[row][0].plot(t, y, linewidth=0.4)
        axes[row][0].set_ylim(-1, 1)
        axes[row][0].set_ylabel(name.replace('sfx_', ''), rotation=0, ha='right', va='center', fontsize=7)
        axes[row][1].specgram(y, NFFT=256, Fs=SR, noverlap=192, cmap='magma', vmin=-110, vmax=-30)
        axes[row][1].set_ylim(0, 16000)
        for ax in axes[row]:
            ax.tick_params(labelsize=6)
        spectrum = np.abs(np.fft.rfft(y)) ** 2
        freqs = np.fft.rfftfreq(len(y), 1 / SR)
        # A click is where the level over a millisecond jumps well above the half-millisecond before it.
        frame = int(0.0005 * SR)
        level = np.sqrt(np.convolve(y ** 2, np.ones(frame) / frame, mode='same'))
        jumps = (level[frame:] > 2.5 * level[:-frame] + 0.01)
        clicks = int(np.sum(jumps[1:] & ~jumps[:-1]))
        lines.append(f"{name},{len(y) / SR:.3f},{20 * np.log10(np.max(np.abs(y))):.1f},{20 * np.log10(np.sqrt(np.mean(y ** 2))):.1f},"
                     f"{np.sum(freqs * spectrum) / np.sum(spectrum):.0f},{np.sum(spectrum[freqs > 2000]) / np.sum(spectrum):.2f},{clicks}")
    fig.tight_layout()
    fig.savefig(os.path.join(folder, 'hand_sounds_review.png'), dpi=110)
    with open(os.path.join(folder, 'hand_sounds_review.csv'), 'w', encoding='utf-8') as out:
        out.write('\n'.join(lines) + '\n')
    print('\n'.join(lines))


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    out_dir = sys.argv[1]
    os.makedirs(out_dir, exist_ok=True)
    made = sounds()
    for name, y in made.items():
        wavfile.write(os.path.join(out_dir, name + '.wav'), SR, (np.clip(y, -1, 1) * 32767).astype(np.int16))
    print(f'wrote {len(made)} sounds to {out_dir}')
    if len(sys.argv) > 2:
        review(made, sys.argv[2])


if __name__ == '__main__':
    main()
