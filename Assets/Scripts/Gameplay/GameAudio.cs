using System;
using System.Collections.Generic;
using UnityEngine;

public enum StoreSound { Bell, Hurry, Thud, Tape, Flap, Place, Recycle, Dispose, Error, Door, Delivery, Ding, Tip, DrawerOpen, DrawerClose, Coin, Note, SetDown, Bag }

/// <summary>Customer lines of implementation doc 6.1: two greetings, the hurry, the leaving line, the four requests, and the two thanks.</summary>
public enum CustomerLine { Greet, Bag, Hurry, Leave, Microwave, HotWater, OnePlus, Thanks, ThanksFast, Waited }

/// <summary>What the store manager says on the way in and at the end (game design 10.0, 2026-11-09).</summary>
/// <summary>New lines go at the end: the scene builder fills GameAudio.ManagerLines in this order, and a line without a clip is silent.</summary>
public enum ManagerLine { Welcome, PunchIn, PunchOut, EndingS, EndingA, EndingB, EndingC, FirstCustomer, Alone, Graduated, StoreComplete }

/// <summary>One voice's set of customer lines (the scene builder fills a female and a male set).</summary>
[Serializable]
public class CustomerVoice
{
    public AudioClip[] Greet = new AudioClip[0];
    public AudioClip Bag, Hurry, Leave, Microwave, HotWater, OnePlus, Thanks, ThanksFast, Waited;

    public AudioClip Get(CustomerLine line, int variant)
    {
        switch (line)
        {
            case CustomerLine.Greet: return Greet != null && Greet.Length > 0 ? Greet[Mathf.Abs(variant) % Greet.Length] : null;
            case CustomerLine.Bag: return Bag;
            case CustomerLine.Hurry: return Hurry;
            case CustomerLine.Leave: return Leave;
            case CustomerLine.Microwave: return Microwave;
            case CustomerLine.HotWater: return HotWater;
            case CustomerLine.Thanks: return Thanks;
            case CustomerLine.ThanksFast: return ThanksFast;
            case CustomerLine.Waited: return Waited;
            default: return OnePlus;
        }
    }
}

/// <summary>
/// The game's sounds (doc 5.1, 5.7, 6.1). The clips are the generated ones in Assets/Audio/Generated (scripts/windows/
/// make_game_sounds.py), assigned by the scene builder. A clip left empty falls back to a generated tone, so a scene built
/// without them still beeps. Counter and intake sounds play where they happen; narration and the store ambience are 2D.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class GameAudio : MonoBehaviour
{
    const int SampleRate = 44100;
    const float EffectVolume = 0.8f;
    const float VoiceVolume = 1f;
    const float NarrationVolume = 0.9f;
    const float AmbienceVolume = 0.22f;
    const int EnvelopeRate = 25;
    // A manager standing in the store is heard from where she stands, but never so far off that the instructions get lost.
    const float PlacedNarrationBlend = 0.7f;
    const float PlacedNarrationMinDistance = 3f;

    [Header("Counter (doc 5.1)")]
    public AudioClip ErrorClip;
    public AudioClip ApprovedClip, CancelClip;

    [Header("Till (doc 5.5): the drawer let go and pushed shut, a coin, a note")]
    public AudioClip DrawerOpenClip;
    public AudioClip DrawerClip, CoinClip, NoteClip;

    [Header("Bag (doc 5.4): the film as something goes in")]
    public AudioClip BagClip;

    [Header("Store")]
    public AudioClip BellClip;
    public AudioClip DoorClip, HurryClip, DeliveryClip, Ambience;
    /// <summary>Store music, once the store has it (an equipment unlock). Empty: the unlock still works, in silence.</summary>
    public AudioClip Music;

    [Header("Intake (doc 5.7)")]
    public AudioClip ThudClip;
    public AudioClip TapeClip, FlapClip, PlaceClip, RecycleClip, DisposeClip;

    [Header("Requests (doc 6.4)")]
    public AudioClip DingClip;

    [Header("Rewards (game design 11, 2026-11-09)")]
    public AudioClip TipClip;

    [Header("Voices (doc 6.1, 5.6, 5.7.4)")]
    public CustomerVoice Female = new CustomerVoice();
    public CustomerVoice Male = new CustomerVoice();
    /// <summary>Day 1 tutorial narration, T1 to T6.</summary>
    public AudioClip[] TutorialLines = new AudioClip[0];
    /// <summary>Day 2 intake narration, I1 to I4.</summary>
    public AudioClip[] IntakeLines = new AudioClip[0];
    /// <summary>Game design 10.3: the first time each request comes up. Microwave, hot water, 1+1.</summary>
    public AudioClip[] SpecialGuideLines = new AudioClip[0];
    /// <summary>In the order of <see cref="ManagerLine"/>.</summary>
    public AudioClip[] ManagerLines = new AudioClip[0];

    const float MusicVolume = 0.16f;
    AudioSource speaker, narrator, ambience, music;
    readonly Dictionary<AudioClip, float[]> envelopes = new Dictionary<AudioClip, float[]>();

    /// <summary>How many times each sound was asked for, for the checks. Sounds are not listened to.</summary>
    public readonly Dictionary<StoreSound, int> Played = new Dictionary<StoreSound, int>();
    public readonly Dictionary<CustomerLine, int> Spoken = new Dictionary<CustomerLine, int>();
    public int Narrations { get; private set; }
    public string LastNarration { get; private set; } = "";
    public bool AmbiencePlaying => ambience != null && ambience.isPlaying;
    /// <summary>While someone is there to say the narration (the manager, 2026-11-09), it comes from here. Null: in the head.</summary>
    public Transform NarrationSource { get; set; }
    public event Action<AudioClip> NarrationStarted;

    void Awake()
    {
        speaker = GetComponent<AudioSource>();
        speaker.spatialBlend = 1f;
        speaker.playOnAwake = false;
        if (ErrorClip == null) ErrorClip = Tone("Error", 220f, 0.35f);
        if (ApprovedClip == null) ApprovedClip = Tone("Approved", 1320f, 0.25f);
        if (DrawerClip == null) DrawerClip = Tone("Drawer", 440f, 0.08f);
        if (DrawerOpenClip == null) DrawerOpenClip = Chime("Drawer Open", 2350f, 2350f, 0.4f);
        if (CoinClip == null) CoinClip = Tone("Coin", 4300f, 0.08f);
        if (NoteClip == null) NoteClip = Noise("Note", 0.1f, 0.2f);
        if (BagClip == null) BagClip = Noise("Bag", 0.25f, 0.25f);
        if (CancelClip == null) CancelClip = Tone("Cancel", 880f, 0.06f);
        if (BellClip == null) BellClip = Chime("Bell", 1046f, 784f, 0.6f);
        if (DoorClip == null) DoorClip = Chime("Door", 1319f, 1047f, 0.8f);
        if (HurryClip == null) HurryClip = Chime("Hurry", 660f, 660f, 0.3f);
        if (DeliveryClip == null) DeliveryClip = Chime("Delivery", 784f, 1175f, 0.5f);
        if (ThudClip == null) ThudClip = Tone("Thud", 140f, 0.12f);
        if (TapeClip == null) TapeClip = Noise("Tape", 0.07f, 0.35f);
        if (FlapClip == null) FlapClip = Noise("Flap", 0.15f, 0.25f);
        if (PlaceClip == null) PlaceClip = Tone("Place", 600f, 0.05f);
        if (RecycleClip == null) RecycleClip = Tone("Recycle", 110f, 0.22f);
        if (DisposeClip == null) DisposeClip = Tone("Dispose", 330f, 0.12f);
        if (DingClip == null) DingClip = Chime("Ding", 1568f, 1568f, 0.6f);
        if (TipClip == null) TipClip = Chime("Tip", 1568f, 2093f, 0.35f);

        // On its own object: it moves to whoever is speaking, and the counter sounds on this object must stay at the counter.
        var voice = new GameObject("Narrator");
        voice.transform.SetParent(transform, false);
        narrator = voice.AddComponent<AudioSource>();
        narrator.spatialBlend = 0f;
        narrator.playOnAwake = false;
        narrator.volume = NarrationVolume;
        narrator.minDistance = PlacedNarrationMinDistance;
        if (Ambience != null)
        {
            ambience = gameObject.AddComponent<AudioSource>();
            ambience.spatialBlend = 0f;
            ambience.loop = true;
            ambience.volume = AmbienceVolume;
            ambience.clip = Ambience;
            ambience.Play();
        }
    }

    public bool MusicOn { get; private set; }
    public bool MusicPlaying => music != null && music.isPlaying;

    /// <summary>Switches the store music on or off. It plays under everything else, like the ambience.</summary>
    public void SetMusic(bool on)
    {
        MusicOn = on;
        if (Music == null) return;
        if (music == null)
        {
            music = gameObject.AddComponent<AudioSource>();
            music.spatialBlend = 0f;
            music.loop = true;
            music.playOnAwake = false;
            music.volume = MusicVolume;
            music.clip = Music;
        }
        if (on && !music.isPlaying) music.Play();
        else if (!on && music.isPlaying) music.Stop();
    }

    public void PlayError() { Count(StoreSound.Error); speaker.PlayOneShot(ErrorClip); }
    public void PlayApproved() => speaker.PlayOneShot(ApprovedClip);
    public void PlayCancel() => speaker.PlayOneShot(CancelClip);

    /// <summary>A sound at the place it belongs to: the box, the shelf, the bin, the customer, the door.</summary>
    public void PlayAt(StoreSound sound, Vector3 position)
    {
        Count(sound);
        AudioClip clip;
        switch (sound)
        {
            case StoreSound.Bell: clip = BellClip; break;
            case StoreSound.Hurry: clip = HurryClip; break;
            case StoreSound.Thud: clip = ThudClip; break;
            case StoreSound.Tape: clip = TapeClip; break;
            case StoreSound.Flap: clip = FlapClip; break;
            case StoreSound.Place: clip = PlaceClip; break;
            case StoreSound.Recycle: clip = RecycleClip; break;
            case StoreSound.Dispose: clip = DisposeClip; break;
            case StoreSound.Door: clip = DoorClip; break;
            case StoreSound.Delivery: clip = DeliveryClip; break;
            case StoreSound.Ding: clip = DingClip; break;
            case StoreSound.Tip: clip = TipClip; break;
            case StoreSound.DrawerOpen: clip = DrawerOpenClip; break;
            case StoreSound.DrawerClose: clip = DrawerClip; break;
            case StoreSound.Coin: clip = CoinClip; break;
            case StoreSound.Note: clip = NoteClip; break;
            // A customer's goods put down on the counter: the same small sound as a product placed on a shelf.
            case StoreSound.SetDown: clip = PlaceClip; break;
            case StoreSound.Bag: clip = BagClip; break;
            default: clip = ErrorClip; break;
        }
        if (clip != null) AudioSource.PlayClipAtPoint(clip, position, EffectVolume);
    }

    /// <summary>A customer speaks at <paramref name="mouth"/>. Returns the clip length (0 without a clip) so lines can follow each other.</summary>
    public float Say(CustomerLine line, bool female, Vector3 mouth, int variant = 0)
    {
        Spoken[line] = SpokenCount(line) + 1;
        var clip = (female ? Female : Male)?.Get(line, variant);
        if (clip == null) return 0f;
        AudioSource.PlayClipAtPoint(clip, mouth, VoiceVolume);
        return clip.length;
    }

    /// <summary>How loud the line is over its length, 0 to 1, for the speaker's mouth. Null if the clip cannot be read.</summary>
    public float[] EnvelopeOf(CustomerLine line, bool female, int variant = 0) => EnvelopeOf((female ? Female : Male)?.Get(line, variant));

    /// <summary>The same for any clip, the manager's lines included.</summary>
    public float[] EnvelopeOf(AudioClip clip)
    {
        if (clip == null) return null;
        if (envelopes.TryGetValue(clip, out var known)) return known;
        var samples = new float[clip.samples * clip.channels];
        float[] result = null;
        if (clip.GetData(samples, 0))
        {
            int frames = Mathf.Max(1, Mathf.CeilToInt(clip.length * EnvelopeRate));
            result = new float[frames];
            int perFrame = Mathf.Max(1, samples.Length / frames);
            float loudest = 0.0001f;
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0f;
                int count = 0;
                for (int i = frame * perFrame; i < Mathf.Min(samples.Length, (frame + 1) * perFrame); i++, count++) sum += samples[i] * samples[i];
                result[frame] = count > 0 ? Mathf.Sqrt(sum / count) : 0f;
                loudest = Mathf.Max(loudest, result[frame]);
            }
            for (int frame = 0; frame < frames; frame++) result[frame] = Mathf.Sqrt(result[frame] / loudest);
        }
        envelopes[clip] = result;
        return result;
    }

    /// <summary>Tutorial step 1 to 6; a new step cuts the one still talking.</summary>
    public void NarrateTutorial(int step) => Narrate(TutorialLines, step - 1);

    /// <summary>Intake step 1 to 4, and 5 for the disposal step of the training.</summary>
    public void NarrateIntake(int step) => Narrate(IntakeLines, step - 1);

    /// <summary>The guide line for a request that has come up for the first time.</summary>
    public void NarrateSpecial(string special) =>
        Narrate(SpecialGuideLines, special == Order.Microwave ? 0 : special == Order.HotWater ? 1 : special == Order.OnePlus ? 2 : -1);

    public void NarrateManager(ManagerLine line) => Narrate(ManagerLines, (int)line);

    void Narrate(AudioClip[] lines, int index)
    {
        if (lines == null || index < 0 || index >= lines.Length || lines[index] == null) return;
        narrator.Stop();
        narrator.spatialBlend = NarrationSource != null ? PlacedNarrationBlend : 0f;
        if (NarrationSource != null) narrator.transform.position = NarrationSource.position;
        narrator.clip = lines[index];
        narrator.Play();
        Narrations++;
        LastNarration = lines[index].name;
        NarrationStarted?.Invoke(lines[index]);
    }

    /// <summary>The Rocketbox avatars are named Female_... and Male_...; anything else (the placeholder) speaks with the male voice.</summary>
    public static bool IsFemale(string avatar) => avatar != null && avatar.StartsWith("Female", StringComparison.Ordinal);

    public int CountOf(StoreSound sound) => Played.TryGetValue(sound, out var n) ? n : 0;
    public int SpokenCount(CustomerLine line) => Spoken.TryGetValue(line, out var n) ? n : 0;

    void Count(StoreSound sound) => Played[sound] = CountOf(sound) + 1;

    static AudioClip Tone(string name, float frequency, float seconds)
    {
        var samples = new float[(int)(SampleRate * seconds)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = 0.4f * Mathf.Sin(2f * Mathf.PI * frequency * i / SampleRate) * (1f - (float)i / samples.Length);
        return Clip(name, samples);
    }

    static AudioClip Chime(string name, float first, float second, float seconds)
    {
        var samples = new float[(int)(SampleRate * seconds)];
        for (int i = 0; i < samples.Length; i++)
        {
            float f = i < samples.Length / 2 ? first : second;
            samples[i] = 0.35f * Mathf.Sin(2f * Mathf.PI * f * i / SampleRate) * (1f - (float)(i % (samples.Length / 2)) / (samples.Length / 2));
        }
        return Clip(name, samples);
    }

    static AudioClip Noise(string name, float seconds, float level)
    {
        var random = new System.Random(name.GetHashCode());
        var samples = new float[(int)(SampleRate * seconds)];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = level * (float)(random.NextDouble() * 2.0 - 1.0) * (1f - (float)i / samples.Length);
        return Clip(name, samples);
    }

    static AudioClip Clip(string name, float[] samples)
    {
        var clip = AudioClip.Create(name, samples.Length, 1, SampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}
