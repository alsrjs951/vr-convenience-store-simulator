"""Generates the game's placeholder-free sound set (P3-K3, done by synthesis on 2026-11-08 at the user's request).

Effects are synthesized from tones and filtered noise. Voices come from the Windows Korean TTS voice (Microsoft Heami,
see make_game_voices.ps1) and are post-processed here: trimmed, normalized, and for male customers pitch-shifted down
with WSOLA so the length stays the same. Nothing is downloaded.

Usage:
  python scripts/windows/make_game_sounds.py <tts_raw_dir> <out_dir>
The TTS step writes <tts_raw_dir>/<name>.wav first. <out_dir> is Assets/Audio/Generated.
"""
import os
import sys

import numpy as np
from scipy.io import wavfile
from scipy.signal import butter, sosfilt, resample

SR = 44100
rng = np.random.default_rng(20261108)


def env(n, attack=0.005, release=None, sr=SR):
    t = np.arange(n) / sr
    a = np.clip(t / max(attack, 1e-4), 0, 1)
    if release is None:
        return a
    return a * np.exp(-t / release)


def tone(freq, seconds, partials=((1, 1.0),), attack=0.003, release=0.1, sr=SR):
    n = int(seconds * sr)
    t = np.arange(n) / sr
    y = sum(w * np.sin(2 * np.pi * freq * k * t) for k, w in partials)
    return y * env(n, attack, release, sr)


def noise(seconds, lo=None, hi=None, sr=SR, order=4):
    n = int(seconds * sr)
    x = rng.standard_normal(n)
    if lo and hi:
        sos = butter(order, [lo, hi], btype='band', fs=sr, output='sos')
    elif hi:
        sos = butter(order, hi, btype='low', fs=sr, output='sos')
    elif lo:
        sos = butter(order, lo, btype='high', fs=sr, output='sos')
    else:
        return x
    return sosfilt(sos, x)


def cat(*parts, gap=0.0, sr=SR):
    out = []
    for i, p in enumerate(parts):
        if i and gap:
            out.append(np.zeros(int(gap * sr)))
        out.append(p)
    return np.concatenate(out)


def mix(*parts):
    n = max(len(p) for p in parts)
    out = np.zeros(n)
    for p in parts:
        out[:len(p)] += p
    return out


def finish(y, peak_db=-3.0, fade=0.004, sr=SR):
    y = np.asarray(y, dtype=float)
    f = int(fade * sr)
    if f and len(y) > 2 * f:
        y[:f] *= np.linspace(0, 1, f)
        y[-f:] *= np.linspace(1, 0, f)
    m = np.max(np.abs(y)) or 1.0
    return y / m * (10 ** (peak_db / 20))


def save(out_dir, name, y, sr=SR):
    path = os.path.join(out_dir, name + '.wav')
    wavfile.write(path, sr, (np.clip(y, -1, 1) * 32767).astype(np.int16))
    print(f'{name}.wav {len(y) / sr:.2f}s')


# ---------- effects ----------

def effects(out_dir):
    bell = ((1, 1.0), (2.76, 0.45), (5.4, 0.25), (8.9, 0.1))      # inharmonic, bell-like
    chime = ((1, 1.0), (2.0, 0.35), (3.0, 0.15))

    # Scanner: the familiar short square-ish beep.
    n = int(0.11 * SR)
    t = np.arange(n) / SR
    beep = np.sign(np.sin(2 * np.pi * 2800 * t)) * 0.35 + np.sin(2 * np.pi * 2800 * t) * 0.65
    save(out_dir, 'sfx_scan', finish(beep * env(n, 0.002) * np.where(t > 0.095, np.linspace(1, 0, n), 1), -6))

    # Card approved "띠르릉": a quick rising three-note chime.
    save(out_dir, 'sfx_card_approved', finish(cat(tone(1568, 0.09, chime, release=0.06), tone(1976, 0.09, chime, release=0.06),
                                                  tone(2349, 0.32, chime, release=0.12)), -5))

    # Cash drawer "철컥": a metal latch click, the drawer sliding, a stop.
    latch = noise(0.03, 2500, 9000) * env(int(0.03 * SR), 0.0005, 0.006)
    slide = noise(0.16, 300, 2500) * env(int(0.16 * SR), 0.02, 0.08) * 0.35
    stop = mix(tone(160, 0.12, ((1, 1.0), (2.3, 0.4)), release=0.03), noise(0.05, 1500, 6000) * env(int(0.05 * SR), 0.0005, 0.01) * 0.6)
    save(out_dir, 'sfx_drawer', finish(cat(latch, slide, stop), -4))

    # Error "땡": a dull low two-tone buzz.
    n = int(0.42 * SR)
    t = np.arange(n) / SR
    buzz = np.sign(np.sin(2 * np.pi * 196 * t)) * 0.5 + np.sign(np.sin(2 * np.pi * 233 * t)) * 0.5
    buzz = sosfilt(butter(2, 2500, 'low', fs=SR, output='sos'), buzz)
    save(out_dir, 'sfx_error', finish(buzz * env(n, 0.004, 0.25), -6))

    # Scan cancel "띡": a short high blip.
    save(out_dir, 'sfx_cancel', finish(tone(1900, 0.06, ((1, 1.0), (2, 0.2)), release=0.02), -8))

    # Entrance chime "딩동": the two-note door chime of a convenience store.
    save(out_dir, 'sfx_door_chime', finish(cat(tone(1319, 0.55, bell, release=0.35), tone(1047, 1.1, bell, release=0.55), gap=0.0), -7))

    # Counter call bell: one bright ding, for a customer arriving while the clerk is away (Day 2+).
    save(out_dir, 'sfx_counter_bell', finish(tone(2093, 0.9, bell, release=0.4), -7))

    # Hurry: two short soft knocks on the counter (the voice says the words).
    knock = mix(tone(220, 0.08, ((1, 1.0), (3.1, 0.3)), release=0.02), noise(0.02, 800, 4000) * env(int(0.02 * SR), 0.0005, 0.004) * 0.5)
    save(out_dir, 'sfx_hurry', finish(cat(knock, knock, gap=0.12), -6))

    # Box set down on the stand "툭".
    save(out_dir, 'sfx_box_thud', finish(mix(tone(95, 0.18, ((1, 1.0), (2.2, 0.3)), release=0.04),
                                             noise(0.04, 400, 3000) * env(int(0.04 * SR), 0.001, 0.01) * 0.5), -4))

    # Tape cut "찌익": a short tearing rasp, amplitude-modulated noise.
    n = int(0.12 * SR)
    t = np.arange(n) / SR
    tear = noise(0.12, 1800, 7000) * (0.6 + 0.4 * np.sign(np.sin(2 * np.pi * 90 * t))) * env(n, 0.005, 0.07)
    save(out_dir, 'sfx_tape', finish(tear, -6))

    # Flap "펄럭": a cardboard flap swinging over.
    n = int(0.28 * SR)
    t = np.arange(n) / SR
    swoosh = noise(0.28, 200, 1800) * np.sin(np.pi * t / t[-1]) ** 2
    slap = noise(0.03, 500, 3000) * env(int(0.03 * SR), 0.001, 0.008)
    save(out_dir, 'sfx_flap', finish(cat(swoosh * 0.7, slap), -6))

    # Shelved "착": a light tap of a packet set in place.
    save(out_dir, 'sfx_place', finish(mix(noise(0.05, 900, 5000) * env(int(0.05 * SR), 0.0005, 0.012),
                                          tone(420, 0.05, release=0.015) * 0.3), -7))

    # Empty box into the bin "털썩": a hollow thump and a little rattle.
    thump = mix(tone(80, 0.3, ((1, 1.0), (1.9, 0.5), (3.3, 0.2)), release=0.07), noise(0.06, 300, 2500) * env(int(0.06 * SR), 0.001, 0.02) * 0.6)
    rattle = noise(0.12, 1500, 5000) * env(int(0.12 * SR), 0.01, 0.04) * 0.25
    save(out_dir, 'sfx_box_recycle', finish(cat(thump, rattle), -4))

    # Product into the waste bin: a plastic lid flap and a drop.
    save(out_dir, 'sfx_dispose', finish(cat(noise(0.04, 1200, 6000) * env(int(0.04 * SR), 0.0005, 0.01),
                                            mix(tone(140, 0.16, ((1, 1.0), (2.6, 0.3)), release=0.04), noise(0.05, 300, 2000) * env(int(0.05 * SR), 0.001, 0.015) * 0.4)), -5))

    # Delivery arrived (Day 3 mid-day delivery): three rising notes.
    save(out_dir, 'sfx_delivery', finish(cat(tone(784, 0.16, chime, release=0.1), tone(988, 0.16, chime, release=0.1), tone(1175, 0.45, chime, release=0.25)), -7))

    # Store ambience: a quiet air-conditioning hiss and a refrigerator hum, 20 s, crossfaded to loop without a seam.
    seconds, xf = 20.0, 2.0
    n, m = int(seconds * SR), int(xf * SR)
    hiss = noise(seconds + xf, hi=1800, order=2) * 0.25 + noise(seconds + xf, 2000, 6000, order=2) * 0.03
    t = np.arange(n + m) / SR
    hum = (np.sin(2 * np.pi * 60 * t) * 0.10 + np.sin(2 * np.pi * 120 * t) * 0.06 + np.sin(2 * np.pi * 180 * t) * 0.02) * (1 + 0.15 * np.sin(2 * np.pi * 0.07 * t))
    room = hiss + hum
    loop = room[:n].copy()
    fade_in = np.linspace(0, 1, m)
    loop[:m] = room[:m] * fade_in + room[n:n + m] * (1 - fade_in)
    save(out_dir, 'amb_store_loop', finish(loop, -18, fade=0))


# ---------- voices ----------

def wsola(x, rate, frame=1024, hop=256, search=192):
    """Time-scale x by rate (output length ~ len(x) / rate) without changing pitch."""
    win = np.hanning(frame)
    out_len = int(len(x) / rate) + frame
    out = np.zeros(out_len)
    norm = np.zeros(out_len)
    prev_tail = None
    pos_out = 0
    while True:
        center = int(pos_out * rate)
        if center + frame + search >= len(x):
            break
        if prev_tail is None:
            best = center
        else:
            lo = max(0, center - search)
            hi = min(len(x) - frame, center + search)
            best, best_c = center, -np.inf
            for s in range(lo, hi, 4):
                c = np.dot(x[s:s + hop], prev_tail)
                if c > best_c:
                    best, best_c = s, c
        seg = x[best:best + frame]
        out[pos_out:pos_out + frame] += seg * win
        norm[pos_out:pos_out + frame] += win
        prev_tail = x[best + hop:best + 2 * hop]
        pos_out += hop
    norm[norm < 1e-6] = 1
    return (out / norm)[:pos_out + frame]


def pitch_shift(x, factor):
    """factor < 1 lowers the pitch; the length stays about the same."""
    stretched = wsola(x, 1.0 / factor)               # shorter by factor, same pitch
    return resample(stretched, int(len(stretched) / factor))   # back to the length, pitch times factor


def trim(x, sr, threshold=0.02, pad=0.06):
    a = np.abs(x) / (np.max(np.abs(x)) or 1)
    idx = np.where(a > threshold)[0]
    if len(idx) == 0:
        return x
    s = max(0, idx[0] - int(pad * sr))
    e = min(len(x), idx[-1] + int(pad * sr))
    return x[s:e]


# Lines said by customers come as a female (Heami) and a male (pitched down) version. Narration is Heami only.
CUSTOMER_LINES = ['voice_greet_1', 'voice_greet_2', 'voice_bag', 'voice_hurry', 'voice_leave',
                  'voice_special_microwave', 'voice_special_hotwater', 'voice_special_oneplus']
MALE_FACTOR = 0.68   # about 200 Hz down to about 136 Hz


def voices(raw_dir, out_dir):
    for name in sorted(os.listdir(raw_dir)):
        if not name.endswith('.wav'):
            continue
        base = name[:-4]
        sr, x = wavfile.read(os.path.join(raw_dir, name))
        x = x.astype(float) / 32768.0
        x = trim(x, sr)
        if base in CUSTOMER_LINES:
            save(out_dir, base + '_f', finish(x, -3, sr=sr), sr)
            save(out_dir, base + '_m', finish(pitch_shift(x, MALE_FACTOR), -3, sr=sr), sr)
        else:
            save(out_dir, base, finish(x, -3, sr=sr), sr)


if __name__ == '__main__':
    raw, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)
    effects(out)
    voices(raw, out)
