using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Films the store from several cameras at once, each into its own video file, all in step (2026-11-14, for the demo
/// video). The game is put on a fixed step per frame (Time.captureFramerate), so no frame is dropped however long the
/// drawing and the encoding take, and the files line up frame for frame; which camera is shown when is decided
/// afterwards, at the cutting. Each camera draws into its own texture, never the screen, so the size of the film does
/// not depend on the window. Frames go raw to one ffmpeg per camera. Editor only: a built player cannot start ffmpeg.
///
/// The game's own sound goes into sound.wav, in step as well: while filming, Unity mixes exactly one frame's worth of
/// sound when asked (AudioRenderer) instead of playing to the speakers, so nothing is heard during a take.
/// </summary>
public class FilmRecorder : MonoBehaviour
{
    public class Track
    {
        public string Name;
        public Camera Camera;
        /// <summary>Called every frame once the game has moved: puts the camera where it should be. Seconds of film so far.</summary>
        public Action<Camera, float> Drive;
        internal RenderTexture Texture;
        internal System.Diagnostics.Process Encoder;
        internal Stream Pipe;
        internal Thread Writer;
        internal BlockingCollection<byte[]> Queue;
        internal readonly Dictionary<int, byte[]> Early = new Dictionary<int, byte[]>();
        internal int NextToQueue;
        internal volatile bool Broken;
    }

    public static FilmRecorder Current { get; private set; }

    public int Frame { get; private set; }
    public float Seconds => Frame / (float)fps;
    public readonly List<Track> Tracks = new List<Track>();

    readonly ConcurrentBag<byte[]> spare = new ConcurrentBag<byte[]>();
    string folder, ffmpeg;
    int width, height, fps;
    StreamWriter marks;
    FileStream sound;
    int soundChannels, soundRate;
    long soundBytes;
    byte[] soundScratch = new byte[0];
    bool rolling, closed;
    float begunAt;

    public static FilmRecorder Begin(string folder, string ffmpeg, int width, int height, int fps)
    {
        var recorder = new GameObject("Film Recorder").AddComponent<FilmRecorder>();
        recorder.folder = folder;
        recorder.ffmpeg = ffmpeg;
        recorder.width = width;
        recorder.height = height;
        recorder.fps = fps;
        Directory.CreateDirectory(folder);
        recorder.marks = new StreamWriter(Path.Combine(folder, "marks.csv"), false, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        recorder.marks.WriteLine("frame,seconds,mark");
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = -1;
        Time.captureFramerate = fps;
        recorder.BeginSound();
        recorder.begunAt = Time.realtimeSinceStartup;
        recorder.rolling = true;
        recorder.StartCoroutine(recorder.Roll());
        Current = recorder;
        Debug.Log($"[Film] rolling: {width}x{height} at {fps} fps into {folder}");
        return recorder;
    }

    public Track Add(string name, float fieldOfView, Action<Camera, float> drive)
    {
        var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { antiAliasing = 1, name = "Film " + name };
        texture.Create();
        var camera = new GameObject("Film Camera " + name).AddComponent<Camera>();
        camera.fieldOfView = fieldOfView;
        camera.nearClipPlane = 0.05f;
        camera.farClipPlane = 80f;
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.targetTexture = texture;
        camera.depth = -20f;
        // Drawn the way the player's own view is: the same post-processing and edge smoothing.
        var main = Camera.main != null ? Camera.main.GetUniversalAdditionalCameraData() : null;
        var data = camera.GetUniversalAdditionalCameraData();
        if (main != null)
        {
            data.renderPostProcessing = main.renderPostProcessing;
            data.antialiasing = main.antialiasing;
            data.antialiasingQuality = main.antialiasingQuality;
            data.renderShadows = main.renderShadows;
        }

        var track = new Track { Name = name, Camera = camera, Drive = drive, Texture = texture, Queue = new BlockingCollection<byte[]>(4) };
        string file = Path.Combine(folder, name + ".mkv");
        // Matroska, not MP4: a file cut short by a crash can still be played. Rows come bottom first.
        var start = new System.Diagnostics.ProcessStartInfo(ffmpeg,
            $"-y -loglevel error -f rawvideo -pix_fmt rgba -video_size {width}x{height} -framerate {fps} -i - -vf vflip -c:v libx264 -preset veryfast -crf 16 -pix_fmt yuv420p \"{file}\"")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        track.Encoder = System.Diagnostics.Process.Start(start);
        track.Pipe = track.Encoder.StandardInput.BaseStream;
        track.Writer = new Thread(() => Write(track)) { IsBackground = true, Name = "Film " + name };
        track.Writer.Start();
        track.NextToQueue = Frame;
        Tracks.Add(track);
        marks.WriteLine($"{Frame},{Seconds:0.000},camera {name} fov {fieldOfView:0}");
        return track;
    }

    void BeginSound()
    {
        switch (AudioSettings.speakerMode)
        {
            case AudioSpeakerMode.Mono: soundChannels = 1; break;
            case AudioSpeakerMode.Quad: soundChannels = 4; break;
            case AudioSpeakerMode.Surround: soundChannels = 5; break;
            case AudioSpeakerMode.Mode5point1: soundChannels = 6; break;
            case AudioSpeakerMode.Mode7point1: soundChannels = 8; break;
            default: soundChannels = 2; break;
        }
        soundRate = AudioSettings.outputSampleRate;
        if (!AudioRenderer.Start())
        {
            Debug.LogWarning("[Film] the sound could not be taken over: a silent film");
            return;
        }
        sound = new FileStream(Path.Combine(folder, "sound.wav"), FileMode.Create, FileAccess.Write);
        sound.Write(new byte[44], 0, 44);   // the header, written at the end when the length is known
        Debug.Log($"[Film] sound: {soundRate} Hz, {soundChannels} channels");
    }

    /// <summary>One frame's worth of the game's sound, 16 bit.</summary>
    void RecordSound()
    {
        if (sound == null) return;
        int samples = AudioRenderer.GetSampleCountForCaptureFrame();
        var mixed = new NativeArray<float>(samples * soundChannels, Allocator.Temp);
        if (AudioRenderer.Render(mixed))
        {
            int bytes = mixed.Length * 2;
            if (soundScratch.Length < bytes) soundScratch = new byte[bytes];
            for (int i = 0; i < mixed.Length; i++)
            {
                short value = (short)Mathf.RoundToInt(Mathf.Clamp(mixed[i], -1f, 1f) * 32767f);
                soundScratch[i * 2] = (byte)value;
                soundScratch[i * 2 + 1] = (byte)(value >> 8);
            }
            sound.Write(soundScratch, 0, bytes);
            soundBytes += bytes;
        }
        mixed.Dispose();
    }

    void EndSound()
    {
        if (sound == null) return;
        AudioRenderer.Stop();
        using (var header = new BinaryWriter(new MemoryStream(44)))
        {
            header.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            header.Write((uint)(36 + soundBytes));
            header.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            header.Write(16);
            header.Write((short)1);
            header.Write((short)soundChannels);
            header.Write(soundRate);
            header.Write(soundRate * soundChannels * 2);
            header.Write((short)(soundChannels * 2));
            header.Write((short)16);
            header.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            header.Write((uint)soundBytes);
            header.Flush();
            sound.Seek(0, SeekOrigin.Begin);
            sound.Write(((MemoryStream)header.BaseStream).ToArray(), 0, 44);
        }
        sound.Close();
        Debug.Log($"[Film] sound: {soundBytes / (2.0 * soundChannels * soundRate):0.0} s");
        sound = null;
    }

    public void Mark(string mark)
    {
        if (closed) return;
        marks.WriteLine($"{Frame},{Seconds:0.000},{mark}");
        Debug.Log($"[Film] {Seconds:0.00}s {mark}");
    }

    void LateUpdate()
    {
        if (!rolling) return;
        foreach (var track in Tracks)
        {
            try { track.Drive?.Invoke(track.Camera, Seconds); }
            catch (Exception problem) { Debug.LogWarning($"[Film] camera {track.Name}: {problem.Message}"); }
        }
    }

    IEnumerator Roll()
    {
        var endOfFrame = new WaitForEndOfFrame();
        while (rolling)
        {
            yield return endOfFrame;
            if (!rolling) break;
            RecordSound();
            int frame = Frame;
            foreach (var track in Tracks)
            {
                var its = track;
                AsyncGPUReadback.Request(track.Texture, 0, TextureFormat.RGBA32, request => Deliver(its, frame, request));
            }
            Frame++;
        }
    }

    /// <summary>On the main thread. Frames are handed on in order; a full queue holds the game back rather than drop one.</summary>
    void Deliver(Track track, int frame, AsyncGPUReadbackRequest request)
    {
        if (frame < track.NextToQueue) return;
        int size = width * height * 4;
        if (!spare.TryTake(out var buffer)) buffer = new byte[size];
        if (request.hasError) Array.Clear(buffer, 0, size);
        else request.GetData<byte>().CopyTo(buffer);
        track.Early[frame] = buffer;
        while (track.Early.TryGetValue(track.NextToQueue, out var next))
        {
            track.Early.Remove(track.NextToQueue);
            track.NextToQueue++;
            if (track.Broken) spare.Add(next);
            else track.Queue.Add(next);
        }
    }

    void Write(Track track)
    {
        foreach (var buffer in track.Queue.GetConsumingEnumerable())
        {
            if (!track.Broken)
            {
                try { track.Pipe.Write(buffer, 0, buffer.Length); }
                catch (Exception) { track.Broken = true; }
            }
            spare.Add(buffer);
        }
        try { track.Pipe.Close(); } catch (Exception) { }
    }

    public static void CloseCurrent()
    {
        if (Current != null) Current.Close();
    }

    public void Close()
    {
        if (closed) return;
        rolling = false;
        EndSound();
        Time.captureFramerate = 0;
        AsyncGPUReadback.WaitAllRequests();
        float real = Time.realtimeSinceStartup - begunAt;
        marks.WriteLine($"{Frame},{Seconds:0.000},closed after {real:0.0} real seconds");
        closed = true;
        marks.Close();
        foreach (var track in Tracks)
        {
            track.Queue.CompleteAdding();
            track.Writer.Join(60000);
            bool ended = track.Encoder.WaitForExit(60000);
            Debug.Log($"[Film] {track.Name}: {track.NextToQueue} frames; encoder {(ended ? "exit " + track.Encoder.ExitCode : "still running")}{(track.Broken ? "; THE PIPE BROKE" : "")}");
            if (track.Camera != null)
            {
                track.Camera.targetTexture = null;
                Destroy(track.Camera.gameObject);
            }
            track.Texture.Release();
        }
        Debug.Log($"[Film] closed: {Frame} frames ({Seconds:0.0} s of film) in {real:0.0} real seconds, {Tracks.Count} cameras");
        if (Current == this) Current = null;
    }

    void OnDestroy() => Close();
}
