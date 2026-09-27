# ReplayPad — Architecture

> **Note:** This project pivoted. The original plan ([project.md](project.md)) was a service that converts OBS Replay Buffer MKVs to MP3. The current tool replaces OBS entirely: it maintains its own audio-only replay buffer and saves MP3 directly, with no video ever recorded. The code lives in [ReplayPad/](ReplayPad/).

---

# 1. What It Is

A Windows desktop application (WPF window + system tray icon) that continuously keeps the last N minutes (default 5) of audio in RAM. Pressing a global hotkey (default **Ctrl+Alt+S**) saves that buffer as an MP3. Nothing is written to disk until the hotkey is pressed.

The window shows live status (level meter, buffered duration), a save button, editable settings that apply instantly without restarting, and a recent-replays list. Closing the window hides it to the tray; capture continues until Exit is chosen from the tray menu.

Compared to OBS Replay Buffer:

| | OBS | ReplayPad |
|---|---|---|
| Idle cost | Constant video encoding (CPU/GPU) | ~0% CPU, ~70 MB RAM |
| Disk while idle | Writes MKV on save, needs conversion | Nothing |
| Output | MKV video → convert → MP3 | MP3 directly |

---

# 2. Signal Flow

```
 Default render device            Default capture device
 (desktop audio, WASAPI           (microphone, WASAPI)
  loopback)                              │
        │                                │
        ▼                                ▼
 BufferedWaveProvider             BufferedWaveProvider
 (10 s jitter buffer,             (silence when quiet)
  silence when quiet)                    │
        │                                │
   resample → 48 kHz              resample → 48 kHz
   force stereo, gain             force stereo, gain
        │                                │
        └────────────┬───────────────────┘
                     ▼
           MixingSampleProvider
                     │
        Mixer thread pulls at wall-clock
        rate, converts float → 16-bit PCM
                     │
                     ▼
          CircularAudioBuffer (RAM)
          N min × 192 000 B/s  (~55 MB at 5 min)
                     │
         hotkey → Snapshot() → MP3 encode
                     │
                     ▼
          Music\Replays\Replay_<timestamp>.mp3
```

Which sources exist is controlled by `CaptureMode` (`Desktop`, `Microphone`, `Both`). All internal audio is 48 kHz, 16-bit, stereo (192 KB/s in the ring buffer).

---

# 3. Components

All under [ReplayPad/](ReplayPad/):

| Component | File | Responsibility |
|---|---|---|
| `App` | App.xaml(.cs) | Single-instance mutex, config load, Media Foundation init, wires controller + window + tray |
| `AppController` | Core/AppController.cs | The brain shared by window and tray: owns ring/engine/saver/hotkey, save + live settings-apply logic, events |
| `AppPaths` | Core/AppPaths.cs | User data location: %AppData%\ReplayPad (settings, soundboard, log) so installer updates can't wipe it; one-time migration from the legacy exe-directory location |
| `AppSettings` | Configuration/AppSettings.cs | Load/validate/save `appsettings.json`; writes a commented default if missing |
| `AudioCaptureEngine` | Core/AudioCaptureEngine.cs | WASAPI captures, format conversion, mixing, ring-buffer feed, device-change recovery, peak level for the UI meter |
| `CircularAudioBuffer` | Core/CircularAudioBuffer.cs | Thread-safe PCM ring buffer; `Snapshot()` returns chronological copy |
| `ChannelDownmixProvider` | Core/ChannelDownmixProvider.cs | Reduces >2-channel device formats to stereo |
| `ReplaySaver` | Core/ReplaySaver.cs | PCM → MP3 (Media Foundation → ffmpeg fallback → WAV last resort) |
| `ProcessLoopbackCapture` | Core/ProcessLoopbackCapture.cs | Per-app capture via the Windows process-loopback API (own COM interop; NAudio has no wrapper). All COM work stays on one MTA thread — WASAPI interfaces can't cross apartments |
| `WaveformHistory` | Core/WaveformHistory.cs | Rolling peak-per-100 ms history mirroring the ring buffer's timeline |
| `WaveformView` | UI/WaveformView.cs | OnRender-based waveform display, redrawn a few times per second |
| `MainWindow` | UI/MainWindow.xaml(.cs) | Single-column dashboard: live status + waveform, save/clip buttons, recent replays with Play to mic + live mic-volume slider (debounce-persisted) |
| `SettingsWindow` | UI/SettingsWindow.xaml(.cs) | Sectioned settings dialog (Capture / Buffer & saving / Hotkeys / Play to mic / General); Save applies via AppController.ApplySettings, Cancel discards |
| `EditorWindow` | UI/EditorWindow.xaml(.cs) | Replay editor: decode to 48 kHz stereo floats in memory, trim/delete/fade/volume/normalize with undo, preview playback, re-encode (save-as-copy or safe overwrite via temp + swap) |
| `EditorWaveformView` | UI/EditorWaveformView.cs | Interactive waveform: drag-to-select, playhead, renders from 4096 precomputed peak buckets |
| `VoicePlayer` | Core/VoicePlayer.cs | "Play to mic": plays a replay into a chosen render device (virtual cable — Voicemod/VB-CABLE) whose paired virtual microphone is the call app's input; optional mirror to default speakers; owned by AppController so soundboard hotkeys work without a window |
| `SoundboardStore` | Core/SoundboardStore.cs | Persistent labels, per-sound volumes, pad colors, pins and slot assignments (soundboard.json in AppData); slots 1–9 map to Ctrl+Alt+1..9, Ctrl+Alt+0 stops |
| `LibraryBackup` | Core/LibraryBackup.cs | One-zip snapshot of the library (sounds + category folders incl. empty, replays + transcripts, per-file metadata, settings minus folder/API key); restore reuses same-size files, renames clashes " (1)", blocks path traversal, and fills metadata only where missing |
| `GoogleAuth` | Core/GoogleAuth.cs | Backend-free Google sign-in: OAuth loopback redirect (TcpListener on 127.0.0.1) + PKCE, `drive.file` scope; refresh token DPAPI-encrypted in `google-signin.dat`. Client ID/secret come from build metadata (`google-oauth.props`, git-ignored) |
| `GoogleDriveClient` | Core/GoogleDriveClient.cs | Raw Drive v3 REST: "ReplayPad Backups" folder, resumable 8 MB-chunk uploads that resume after drops, list via appProperties, download, delete |
| `CloudBackup` | Core/CloudBackup.cs | Orchestrates backup/restore (Drive or file), one operation at a time; prunes to N newest backups per PC; `backup.json` prefs; auto-backup timer lives in App |
| `AppAudioShare` | Core/AppAudioShare.cs | "Share app audio": `ProcessLoopbackCapture` of one app (48 kHz/16-bit stereo) → float + live volume → `StereoJitterBuffer` (40 ms cushion, trims above 200 ms, re-primes on underrun) → resample/channel-map to the call device's mix format → WASAPI shared. A watchdog stops it when the app exits; Discord/Voicemod/ReplayPad are blocked (feedback). Prefs + hotkey in `appshare.json` |
| `UpdateChecker` | Core/UpdateChecker.cs | Compares the running version to the latest GitHub release; quiet startup check + tray menu item |
| `RenameDialog` | UI/RenameDialog.xaml(.cs) | Per-sound dialog: label, file rename, per-sound volume, soundboard slot assignment |
| `LauncherWindow` | UI/LauncherWindow.xaml(.cs) | Global quick launcher (Ctrl+Alt+Q): borderless search popup, Enter plays into the call |
| `Theme` | UI/Theme.xaml | Dark control styles (buttons, combo, sliders, checkbox, scrollbars, …) |
| `HotkeyManager` | UI/HotkeyManager.cs | Global hotkey via `RegisterHotKey` on a hidden message window; re-registerable |
| `TrayIconManager` | UI/TrayIconManager.cs | Tray icon + menu, save/pause/autostart/exit, balloon notifications |
| `StartupRegistry` | UI/StartupRegistry.cs | HKCU Run key toggle (launches with `--minimized`) |

Only external dependency: **NAudio** (WASAPI + resampling + Media Foundation wrappers). UI is WPF; WinForms is referenced solely for `NotifyIcon` and the hotkey message window.

Branding: `app.ico` (multi-size, PNG-compressed entries; record-dot design) is the exe icon (`ApplicationIcon`), the window icon of all three windows, and the tray icon (loaded from the pack resource with a generated fallback).

Settings changed in the window apply live: the capture engine is rebuilt in place, and the ring buffer is preserved unless its size changed — so tweaking volume or hotkey doesn't lose captured audio.

---

# 4. Key Design Decisions

**Wall-clock-paced mixer.** The mixer thread wakes every 50 ms and pulls exactly as many samples as wall time dictates. Sources feed `BufferedWaveProvider`s with `ReadFully = true`, which yield silence when a device is quiet — so the buffer timeline always matches real time, whether or not anything is playing. If the machine sleeps, gaps over 2 s are skipped rather than zero-filled.

**Silence keep-alive.** WASAPI loopback delivers no data while nothing plays. A `WasapiOut` playing inaudible silence keeps the stream active (configurable via `KeepAliveSilence`, on by default).

**Device-change recovery.** An `IMMNotificationClient` watches for default-device changes (e.g., plugging in headphones) and a dying capture (device unplugged). Either schedules a debounced full engine restart; the ring buffer survives, so already-captured audio is not lost.

**Encoder chain.** Media Foundation's built-in MP3 encoder is primary (zero dependencies). If it fails: ffmpeg (config path or `PATH`). If that fails: raw WAV — a bigger file beats a lost recording.

**Snapshot-then-encode.** The hotkey handler copies the ring buffer synchronously (milliseconds) and encodes on a background task, so capture continues uninterrupted and a long encode can't distort the buffer. Re-presses during an encode are ignored, not queued.

**Two save granularities.** The main hotkey (Ctrl+Alt+S) saves the whole buffer; a clip hotkey (Ctrl+Alt+D) saves only the last `ClipSeconds` (default 30) by slicing the tail off the same snapshot. Clip files get a `-clip` name suffix.

**Per-app capture.** When `TargetApp` is set, the desktop source is a `ProcessLoopbackCapture` (Windows process-loopback API, Windows 10 2004+) targeting that process tree — optionally inverted (`TargetAppExclude`) to capture everything *except* it. It delivers audio in the engine's native format, needs no silence keep-alive, and is independent of the render device. The app picker in the UI lists processes with active audio sessions. Hard-won detail: activation and the read loop must share one MTA thread; activating from the WPF STA thread makes later `IAudioCaptureClient` calls fail with E_NOINTERFACE.

**Editor works on floats, saves defensively.** The editor (`Edit` button on a selected recent replay, or `ReplayPad.exe --edit <file>`) decodes the whole file to an in-memory float array; all tools are array operations with a 3-level undo stack (files over ~35 min are refused to bound memory). "Overwrite original" encodes to a temp file first and swaps, so a failed encode can never destroy the recording. Encoding reuses `ReplaySaver.EncodeTo` (Media Foundation → ffmpeg).

**Tray app, not a Windows Service.** Global hotkeys and WASAPI capture require the user's desktop session, which services (session 0) don't have. Autostart is a per-user Run registry key, toggleable from the tray menu.

---

# 5. Configuration

`appsettings.json` next to the exe (created with commented defaults on first run; edit → restart app):

| Setting | Default | Meaning |
|---|---|---|
| `CaptureMode` | `Desktop` | `Desktop`, `Microphone`, or `Both` |
| `BufferMinutes` | `5` | Minutes of audio the hotkey saves (1–120; ~11 MB RAM per minute) |
| `OutputFolder` | `""` | Empty = `Music\Replays` |
| `Bitrate` | `192` | MP3 kbps (64–320) |
| `Hotkey` | `Ctrl+Alt+S` | Modifiers + key, e.g. `F9`, `Ctrl+Shift+R` |
| `FileNamePrefix` | `Replay` | `Replay_2026-07-17_21-30-00.mp3` |
| `DesktopGain` / `MicrophoneGain` | `1.0` | Per-source volume multipliers (0–4) |
| `ShowNotifications` | `true` | Balloon tip on save |
| `KeepAliveSilence` | `true` | See §4 |
| `FFmpegPath` | `""` | Optional fallback encoder path |

---

# 6. Failure Handling

| Failure | Response |
|---|---|
| Hotkey already taken | Warn via notification; tray menu / double-click still saves |
| Audio device unplugged / default changed | Debounced engine restart; retry every 3 s until a device works |
| MF encoder fails | ffmpeg fallback, then WAV — audio is never dropped |
| Save pressed while encoding | Ignored (no queue) |
| Config invalid | Error dialog at startup, app exits |
| Unhandled UI exception | Logged to log.txt + tray warning; the app keeps recording |
| Second instance launched | Info dialog, exits (single-instance mutex) |
| Any unexpected error | Logged to `log.txt` next to the exe; capture keeps running |

---

# 7. Build & Run

```powershell
dotnet build ReplayPad -c Release          # or:
dotnet publish ReplayPad -c Release -r win-x64 /p:PublishSingleFile=true --self-contained false
```

Run `ReplayPad.exe`, look for the red-dot tray icon. Right-click for: save now, open output folder, open settings, pause/resume, start with Windows, exit. Double-clicking the icon also saves.

Measured on this machine: **0% CPU idle, ~70 MB RAM** (5-minute buffer), valid 192 kbps MP3 output.
