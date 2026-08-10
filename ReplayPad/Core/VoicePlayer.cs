using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ReplayPad.Core;

/// <summary>
/// Plays sounds into a chosen render device — typically a virtual audio
/// cable (Voicemod, VB-CABLE, …) whose paired virtual microphone is
/// selected as the input in Discord or any call app — optionally mirrored
/// to the default speakers so the user hears what their friends hear.
///
/// Playback is session-based: each Play() creates an independent session
/// (its outputs plus a per-sound gain), so soundboard sounds can either
/// interrupt the previous one or overlap it.
/// </summary>
public sealed class VoicePlayer : IDisposable
{
    private sealed class Session
    {
        public string Path = "";
        public float SoundGain = 1f;
        public int Remaining;
        public int MirrorIndex = -1;
        public readonly List<(WasapiOut Output, AudioFileReader Reader)> Outputs = [];
    }

    private readonly object _lock = new();
    private readonly List<Session> _sessions = [];
    private float _masterVolume = 1f;

    /// <summary>Raised (from a playback thread) when the last playing sound finished naturally.</summary>
    public event Action? PlaybackEnded;

    public bool IsPlaying
    {
        get { lock (_lock) return _sessions.Count > 0; }
    }

    public bool IsPlayingPath(string path)
    {
        lock (_lock)
            return _sessions.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public static List<string> ListRenderDevices()
    {
        var names = new List<string>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            names.Add(device.FriendlyName);
        return names;
    }

    /// <param name="soundGain">Per-sound volume multiplier, on top of the master volume.</param>
    /// <param name="overlap">False: stop whatever is playing first. True: play on top of it.</param>
    public void Play(string filePath, string voiceDeviceName, bool alsoSpeakers,
                     float soundGain = 1f, bool overlap = false)
    {
        if (!overlap)
            Stop();

        var session = new Session
        {
            Path = filePath,
            SoundGain = Math.Clamp(soundGain, 0.1f, 3f)
        };

        lock (_lock)
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? voiceDevice = null;
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (voiceDevice == null &&
                    device.FriendlyName.Contains(voiceDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    voiceDevice = device;
                }
            }
            if (voiceDevice == null)
                throw new InvalidOperationException(
                    $"Playback device \"{voiceDeviceName}\" was not found — pick your virtual mic device again in Settings.");

            var targets = new List<MMDevice> { voiceDevice };
            if (alsoSpeakers)
            {
                var speakers = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                if (speakers.ID != voiceDevice.ID)
                    targets.Add(speakers);
            }

            for (int i = 0; i < targets.Count; i++)
            {
                var reader = new AudioFileReader(filePath) { Volume = _masterVolume * session.SoundGain };
                var output = new WasapiOut(targets[i], AudioClientShareMode.Shared, false, 200);
                output.Init(reader);
                output.PlaybackStopped += (_, _) => OnOutputStopped(session);
                session.Outputs.Add((output, reader));
                if (i == 1)
                    session.MirrorIndex = session.Outputs.Count - 1; // the speakers copy
            }

            session.Remaining = session.Outputs.Count;
            foreach (var (output, _) in session.Outputs)
                output.Play();
            _sessions.Add(session);
        }
    }

    private void OnOutputStopped(Session session)
    {
        bool wasLast;
        lock (_lock)
        {
            // Late event from a session that Stop()/StopPath() already
            // removed and disposed — ignore.
            if (!_sessions.Contains(session))
                return;
            if (--session.Remaining > 0)
                return;
            _sessions.Remove(session);
            wasLast = _sessions.Count == 0;
        }
        DisposeSessionAsync(session);
        if (wasLast)
            PlaybackEnded?.Invoke();
    }

    /// <summary>Disposal off the playback callback thread to avoid re-entrancy.</summary>
    private static void DisposeSessionAsync(Session session) => Task.Run(() =>
    {
        foreach (var (output, reader) in session.Outputs)
        {
            try { output.Stop(); } catch { }
            output.Dispose();
            reader.Dispose();
        }
    });

    /// <summary>
    /// Short volume ramp before disposal so manual stops don't pop; also
    /// gives a natural crossfade when a new sound interrupts the previous.
    /// </summary>
    private static void FadeOutAndDispose(List<Session> sessions)
    {
        if (sessions.Count == 0)
            return;
        Task.Run(async () =>
        {
            var readers = sessions
                .SelectMany(s => s.Outputs.Select(o => (o.Reader, Initial: o.Reader.Volume)))
                .ToList();
            const int steps = 8;
            for (int i = steps - 1; i >= 0; i--)
            {
                foreach (var (reader, initial) in readers)
                    try { reader.Volume = initial * i / steps; } catch { }
                await Task.Delay(16);
            }
            foreach (var session in sessions)
                foreach (var (output, reader) in session.Outputs)
                {
                    try { output.Stop(); } catch { }
                    output.Dispose();
                    reader.Dispose();
                }
        });
    }

    /// <summary>Playback position 0..1 of a file currently playing, or null.</summary>
    public double? ProgressOf(string path)
    {
        lock (_lock)
        {
            foreach (var session in _sessions)
            {
                if (!string.Equals(session.Path, path, StringComparison.OrdinalIgnoreCase) ||
                    session.Outputs.Count == 0)
                    continue;
                try
                {
                    var reader = session.Outputs[0].Reader;
                    if (reader.Length > 0)
                        return Math.Clamp((double)reader.Position / reader.Length, 0, 1);
                }
                catch { }
            }
            return null;
        }
    }

    // ---------- transport control (for the soundboard seek bar) ----------
    // Operations target the most recently started session — the sound the
    // user just triggered and would expect a scrubber to control.

    /// <summary>State of the sound currently under transport control, or null.</summary>
    public sealed record Transport(string Path, TimeSpan Position, TimeSpan Duration, bool IsPaused);

    public Transport? ActiveTransport()
    {
        lock (_lock)
        {
            for (int i = _sessions.Count - 1; i >= 0; i--)
            {
                var session = _sessions[i];
                if (session.Outputs.Count == 0)
                    continue;
                try
                {
                    var (output, reader) = session.Outputs[0];
                    return new Transport(session.Path, reader.CurrentTime, reader.TotalTime,
                        output.PlaybackState == PlaybackState.Paused);
                }
                catch { }
            }
            return null;
        }
    }

    /// <summary>Jumps the active sound to a fraction (0..1) of its length.</summary>
    public void SeekActive(double fraction)
    {
        lock (_lock)
        {
            var session = ActiveSessionLocked();
            if (session == null)
                return;
            foreach (var (_, reader) in session.Outputs)
                try { reader.CurrentTime = reader.TotalTime * Math.Clamp(fraction, 0, 1); }
                catch { }
        }
    }

    /// <summary>Pauses or resumes the active sound; returns true if now playing.</summary>
    public bool TogglePauseActive()
    {
        lock (_lock)
        {
            var session = ActiveSessionLocked();
            if (session == null)
                return false;
            bool paused = session.Outputs[0].Output.PlaybackState == PlaybackState.Paused;
            foreach (var (output, _) in session.Outputs)
                try { if (paused) output.Play(); else output.Pause(); } catch { }
            return paused; // was paused → now playing
        }
    }

    private Session? ActiveSessionLocked()
    {
        for (int i = _sessions.Count - 1; i >= 0; i--)
            if (_sessions[i].Outputs.Count > 0)
                return _sessions[i];
        return null;
    }

    /// <summary>
    /// Silences the speakers copies of everything currently playing (the
    /// virtual-mic copies keep going). The echo escape hatch.
    /// </summary>
    public void StopMirror()
    {
        lock (_lock)
        {
            foreach (var session in _sessions)
            {
                if (session.MirrorIndex < 0 || session.MirrorIndex >= session.Outputs.Count)
                    continue;
                try { session.Outputs[session.MirrorIndex].Output.Stop(); } catch { }
                session.MirrorIndex = -1;
            }
        }
    }

    /// <summary>Sets the master volume (1.0 = 100%); applies live to everything playing.</summary>
    public void SetVolume(float volume)
    {
        lock (_lock)
        {
            _masterVolume = Math.Clamp(volume, 0f, 2f);
            foreach (var session in _sessions)
                foreach (var (_, reader) in session.Outputs)
                    reader.Volume = _masterVolume * session.SoundGain;
        }
    }

    /// <summary>Stops only the sessions playing this file (pad toggle).</summary>
    public void StopPath(string path)
    {
        List<Session> hits;
        lock (_lock)
        {
            hits = _sessions.Where(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var session in hits)
                _sessions.Remove(session);
        }
        FadeOutAndDispose(hits);
    }

    public void Stop()
    {
        List<Session> all;
        lock (_lock)
        {
            all = [.. _sessions];
            _sessions.Clear();
        }
        FadeOutAndDispose(all);
    }

    public void Dispose() => Stop();
}
