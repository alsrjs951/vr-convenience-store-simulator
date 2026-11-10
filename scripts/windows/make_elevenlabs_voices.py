"""Voices the game's lines with ElevenLabs and writes them where the game reads them.

The words are Data/voice_lines.json, the voices and the model Data/voice_cast.json. A customer line is made twice, in the
female and the male customer's voice (<id>_f.wav, <id>_m.wav); the manager's lines once (<id>.wav). Every clip is trimmed
like the other game sounds (make_game_sounds.py), brought to one loudness and written as 22.05 kHz mono WAV.

What ElevenLabs sends back is kept in a cache keyed by voice, model and words, so running this again costs credits only
for lines that changed. The API key is read from the ELEVENLABS_API_KEY environment variable and is never written anywhere.

Usage:
  python -I scripts/windows/make_elevenlabs_voices.py Assets/Audio/Generated            # every line
  python -I scripts/windows/make_elevenlabs_voices.py <dir> --only voice_greet_1 ...    # some lines
  python -I scripts/windows/make_elevenlabs_voices.py <dir> --model eleven_v3 --voice manager=<voice_id>
  python -I scripts/windows/make_elevenlabs_voices.py <dir> --dry-run                    # what it would cost
"""
import argparse
import hashlib
import json
import os
import subprocess
import sys
import tempfile

import numpy as np
import requests
from scipy.io import wavfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
sys.path.insert(0, HERE)
from make_game_sounds import finish, save, trim  # noqa: E402  (the same trimming and levelling as the other clips)

API = 'https://api.elevenlabs.io/v1/text-to-speech/'
RATE = 22050
# Three different voices: levelled to the same loudness, not the same peak, or the quieter speaker sounds further away.
LOUDNESS_DB, PEAK_CEILING = -18.0, 0.89


def fetch(key, voice_id, model, text, cache_dir):
    """The MP3 ElevenLabs makes of the words, from the cache when the same request was made before."""
    name = hashlib.sha256(f'{voice_id}|{model}|{text}'.encode('utf-8')).hexdigest()[:24] + '.mp3'
    path = os.path.join(cache_dir, name)
    if os.path.exists(path) and os.path.getsize(path) > 0:
        return path, False
    response = requests.post(API + voice_id, params={'output_format': 'mp3_44100_128'},
                             headers={'xi-api-key': key, 'accept': 'audio/mpeg'},
                             json={'text': text, 'model_id': model}, timeout=120)
    if response.status_code != 200:
        raise RuntimeError(f'ElevenLabs HTTP {response.status_code} for voice {voice_id}: {response.text[:400]}')
    with open(path, 'wb') as f:
        f.write(response.content)
    return path, True


def decode(mp3_path):
    """Mono float samples at the game's rate."""
    with tempfile.TemporaryDirectory() as tmp:
        wav = os.path.join(tmp, 'clip.wav')
        subprocess.run(['ffmpeg', '-v', 'error', '-y', '-i', mp3_path, '-ac', '1', '-ar', str(RATE), '-c:a', 'pcm_s16le', wav], check=True)
        rate, samples = wavfile.read(wav)
    return samples.astype(float) / 32768.0, rate


def level(clip):
    """The same average loudness for every clip, turned down again only if a peak would clip."""
    rms = float(np.sqrt(np.mean(clip ** 2))) or 1.0
    clip = clip * (10 ** (LOUDNESS_DB / 20) / rms)
    peak = float(np.max(np.abs(clip)))
    return clip * (PEAK_CEILING / peak) if peak > PEAK_CEILING else clip


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('out_dir')
    parser.add_argument('--only', nargs='*', help='line ids to make; all when left out')
    parser.add_argument('--model', help='overrides the model in Data/voice_cast.json')
    parser.add_argument('--voice', action='append', default=[], help='role=voice_id, overrides a voice of Data/voice_cast.json')
    parser.add_argument('--suffix', default='', help='added to every file name, for trying a voice beside the one in use')
    parser.add_argument('--cache', default=os.path.join(ROOT, 'capture', 'elevenlabs'), help='where the raw MP3s are kept')
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()

    with open(os.path.join(ROOT, 'Data', 'voice_lines.json'), encoding='utf-8') as f:
        lines = json.load(f)['lines']
    with open(os.path.join(ROOT, 'Data', 'voice_cast.json'), encoding='utf-8') as f:
        cast = json.load(f)
    model = args.model or cast['model']
    voices = {role: entry['voice_id'] for role, entry in cast['voices'].items()}
    for override in args.voice:
        role, voice_id = override.split('=', 1)
        if role not in voices:
            sys.exit(f'Unknown role {role}; the roles are {", ".join(voices)}')
        voices[role] = voice_id
    if args.only:
        unknown = set(args.only) - {line['id'] for line in lines}
        if unknown:
            sys.exit(f'Unknown line ids: {", ".join(sorted(unknown))}')
        lines = [line for line in lines if line['id'] in args.only]

    # (file name, role, words)
    jobs = []
    for line in lines:
        if line['speaker'] == 'customer':
            jobs.append((line['id'] + '_f', 'customer_f', line['text']))
            jobs.append((line['id'] + '_m', 'customer_m', line['text']))
        else:
            jobs.append((line['id'], line['speaker'], line['text']))
    characters = sum(len(text) for _, _, text in jobs)
    print(f'{len(jobs)} clips, {characters} characters, model {model}')
    if args.dry_run:
        return

    key = os.environ.get('ELEVENLABS_API_KEY')
    if not key:
        sys.exit('ELEVENLABS_API_KEY is not set')
    os.makedirs(args.cache, exist_ok=True)
    os.makedirs(args.out_dir, exist_ok=True)
    spent = 0
    for name, role, text in jobs:
        mp3, fresh = fetch(key, voices[role], model, text, args.cache)
        if fresh:
            spent += len(text)
        samples, rate = decode(mp3)
        clip = level(finish(trim(samples, rate), -3, sr=rate))
        save(args.out_dir, name + args.suffix, clip, rate)
        peak = float(np.max(np.abs(clip))) if len(clip) else 0.0
        if len(clip) < rate * 0.3 or peak < 0.05:
            print(f'  WARNING {name}: {len(clip) / rate:.2f} s, peak {peak:.2f}')
    print(f'made {len(jobs)} clips; {spent} characters were new and used credits')


if __name__ == '__main__':
    main()
