using System.Diagnostics;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ReplayPad.Core;

/// <summary>"Share app audio" preferences, kept in appshare.json next to the settings.</summary>
public sealed class AppSharePrefs
{
    /// <summary>Process name of the app shared last (the hotkey re-shares it).</summary>
    public string LastApp { get; set; } = "";
    public int Volume { get; set; } = 100;
    public string Hotkey { get; set; } = "Ctrl+Alt+A";

    private static string PrefsPath => Path.Combine(AppPaths.DataDir, "appshare.json");

    public static AppSharePrefs Load()
    {
        try
        {
            if (File.Exists(PrefsPath))
                return JsonSerializer.Deserialize<AppSharePrefs>(File.ReadAllText(PrefsPath)) ?? new();
        }
        catch (Exception ex)
        {
            Logger.Log("appshare.json unreadable, using defaults: " + ex.Message);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            AtomicFile.WriteAllText(PrefsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.Log("Could not save appshare.json: " + ex.Message);
        }
    }
}

/// <summary>
/// Shares one app's sound (a browser tab, music player, game…) into the
/// call: the app is captured with the per-app loopback API and a copy is
/// played into the call device (the virtual cable / Voicemod device the
/// soundboard uses — Windows mixes it with the pads). The app keeps playing
/// on your speakers as normal; stopping leaves no Windows settings behind.
/// </summary>
public sealed class AppAudioShare : IDisposable
{
    /// <summary>Apps that must never be shared: they'd feed the call back into itself.</summary>
    private static readonly string[] Blocked = ["ReplayPad", "Discord", "Voicemod", "VoicemodDesktop"];

    private readonly object _lock = new();
    private ProcessLoopbackCapture? _capture;
    private WasapiOut? _output;
    private StereoJitterBuffer? _ring;
    private Timer? _watchdog;
    private int _pid;
    private volatile float _gain = 1f;
    private float _peak;
    private float[] _floats = [];

    public string? App { get; private set; }
    public string? Error { get; private set; }
    public bool IsRunning { get { lock (_lock) return _capture != null; } }

    /// <summary>Recent level 0..1 of what's being shared.</summary>
    public float Peak => Interlocked.Exchange(ref _peak, _peak * 0.8f);

    /// <summary>Raised (from any thread) when sharing starts, stops or fails.</summary>
    public event Action? StateChanged;

    /// <summary>Apps that currently have an audio session on any playback device.</summary>
    public static List<string> ListAudioApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    uint pid = sessions[i].GetProcessID;
                    if (pid == 0 || pid == Environment.ProcessId)
                        continue;
                    try
                    {
                        string name = Process.GetProcessById((int)pid).ProcessName;
                        if (!IsBlocked(name))
                            names.Add(name);
                    }
                    catch { } // exited meanwhile
                }
            }
            catch { } // device busy/unplugged
        }
        return [.. names];
    }

    public static bool IsBlocked(string app)
        => Blocked.Any(b => app.StartsWith(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts sharing; throws with a user-readable message on failure.</summary>
    public void Start(string app, string callDevice, int volumePercent)
    {
        if (string.IsNullOrWhiteSpace(app))
            throw new InvalidOperationException("Pick an app to share first.");
        if (IsBlocked(app))
            throw new InvalidOperationException(
                $"\"{app}\" can't be shared — it would send the call back into itself (echo).");
        if (string.IsNullOrWhiteSpace(callDevice))
            throw new InvalidOperationException(
                "Pick your call device first: ⚙ Settings → Play to mic (your virtual cable or Voicemod).");

        Stop();
        SetVolume(volumePercent);
        try
        {
            lock (_lock)
            {
                var device = VoicePlayer.FindRenderDevice(callDevice)
                    ?? throw new InvalidOperationException(
                        $"Playback device \"{callDevice}\" was not found — pick it again in ⚙ Settings → Play to mic.");

                try
                {
                    _pid = ProcessLoopbackCapture.ResolvePid(app);
                }
                catch (InvalidOperationException)
                {
                    throw new InvalidOperationException($"\"{app}\" isn't running — open it and start its audio, then press Share.");
                }
                _capture = new ProcessLoopbackCapture(_pid, excludeTarget: false);
                _ring = new StereoJitterBuffer(_capture.WaveFormat.SampleRate);
                _output = OpenOutput(device, _ring);
                _capture.DataAvailable += OnData;
                _capture.RecordingStopped += (_, e) =>
                {
                    if (e.Exception != null)
                        Fail($"Sharing {app} stopped: {e.Exception.Message}");
                };
                _output.Play();
                _capture.StartRecording();
                App = app;
                Error = null;
                // The loopback stream just goes quiet when the app closes; notice that.
                _watchdog = new Timer(_ => CheckAppAlive(), null, 2000, 2000);
                Logger.Log($"Sharing {app} (pid {_pid}) → {device.FriendlyName}");
            }
        }
        catch
        {
            // The caller shows the message; Error is only for failures while running.
            StopDevices();
            throw;
        }
        StateChanged?.Invoke();
    }

    /// <summary>Stereo stream → the device's mix rate and channel layout → shared-mode WASAPI.</summary>
    private static WasapiOut OpenOutput(MMDevice device, StereoJitterBuffer ring)
    {
        var mix = device.AudioClient.MixFormat;
        ISampleProvider source = ring;
        if (mix.SampleRate != ring.WaveFormat.SampleRate)
            source = new WdlResamplingSampleProvider(source, mix.SampleRate);
        if (mix.Channels == 1)
            source = new StereoToMonoSampleProvider(source);
        else if (mix.Channels > 2)
        {
            var map = new MultiplexingSampleProvider([source], mix.Channels);
            for (int c = 0; c < mix.Channels; c++)
                map.ConnectInputToOutput(c % 2, c);
            source = map;
        }
        var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, 40);
        output.Init(source);
        return output;
    }

    /// <summary>Capture thread: 16-bit stereo → float, volume, into the jitter buffer.</summary>
    private void OnData(object? sender, WaveInEventArgs e)
    {
        int samples = e.BytesRecorded / 2;
        if (samples == 0)
            return;
        if (_floats.Length < samples)
            _floats = new float[samples];
        float gain = _gain, peak = 0;
        for (int i = 0; i < samples; i++)
        {
            float s = Math.Clamp(BitConverter.ToInt16(e.Buffer, i * 2) / 32768f * gain, -1f, 1f);
            _floats[i] = s;
            float a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        if (peak > _peak)
            _peak = peak;
        _ring?.Write(_floats, samples);
    }

    private void CheckAppAlive()
    {
        string? app = App;
        try
        {
            using var process = Process.GetProcessById(_pid);
            if (!process.HasExited)
                return;
        }
        catch { }
        Fail($"{app} was closed — sharing stopped.");
    }

    private void Fail(string message)
    {
        Logger.Log(message);
        Task.Run(() =>
        {
            StopDevices();
            Error = message;
            StateChanged?.Invoke();
        });
    }

    public void SetVolume(int percent) => _gain = Math.Clamp(percent, 0, 200) / 100f;

    public void Stop()
    {
        bool wasRunning = IsRunning;
        StopDevices();
        Error = null;
        if (wasRunning)
        {
            Logger.Log($"Stopped sharing {App}.");
            StateChanged?.Invoke();
        }
    }

    private void StopDevices()
    {
        lock (_lock)
        {
            _watchdog?.Dispose();
            _watchdog = null;
            try { _capture?.StopRecording(); } catch { }
            try { _output?.Stop(); } catch { }
            _capture?.Dispose();
            _output?.Dispose();
            _capture = null;
            _output = null;
            _ring = null;
            _peak = 0;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Interleaved-stereo cushion between the app's clock and the call device's
/// clock: ~40 ms normally; plays silence and re-primes if starved (e.g. the
/// app paused), drops the oldest audio if it runs ahead, so the shared
/// sound never drifts out of sync or builds up delay.
/// </summary>
internal sealed class StereoJitterBuffer : ISampleProvider
{
    private readonly float[] _buffer;
    private readonly int _target;
    private readonly int _max;
    private int _read;
    private int _count;
    private bool _primed;

    public WaveFormat WaveFormat { get; }

    public StereoJitterBuffer(int sampleRate)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);
        _buffer = new float[sampleRate * 2]; // 1 s of stereo
        _target = sampleRate * 2 * 40 / 1000;
        _max = sampleRate * 2 * 200 / 1000;
    }

    public void Write(float[] data, int count)
    {
        lock (_buffer)
        {
            for (int i = 0; i < count; i++)
            {
                if (_count == _buffer.Length)
                {
                    _read = (_read + 2) % _buffer.Length;
                    _count -= 2;
                }
                _buffer[(_read + _count) % _buffer.Length] = data[i];
                _count++;
            }
            if (_count > _max)
            {
                int drop = (_count - _target) & ~1; // whole frames only, keep L/R aligned
                _read = (_read + drop) % _buffer.Length;
                _count -= drop;
            }
        }
    }

    public int Read(float[] output, int offset, int count)
    {
        lock (_buffer)
        {
            if (!_primed)
            {
                if (_count < _target)
                {
                    Array.Clear(output, offset, count);
                    return count;
                }
                _primed = true;
            }
            int take = Math.Min(count, _count) & ~1;
            for (int i = 0; i < take; i++)
                output[offset + i] = _buffer[(_read + i) % _buffer.Length];
            _read = (_read + take) % _buffer.Length;
            _count -= take;
            if (take < count)
            {
                Array.Clear(output, offset + take, count - take);
                _primed = false;
            }
            return count; // never 0: that would stop the output device
        }
    }
}
