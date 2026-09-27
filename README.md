> [!WARNING]
> **This project was fully written by AI** (Claude, by Anthropic). It may contain bugs or unexpected behavior. Use it at your own risk — **we are not responsible** for any problems, data loss, or damage resulting from its use.

# 🔴 ReplayPad

**A soundboard that records its own material.** Two tools in one Windows app:

- 🎛 A **soundboard** — a pad grid of sounds you fire into your Discord/voice call with a click or a global hotkey, organized into categories, with per-sound colors and volumes.
- 🎙 An **audio replay buffer** — like OBS Replay Buffer but audio-only: the last minutes of your PC's sound are always in RAM, and one hotkey saves the moment as an MP3. Trim it in the built-in editor, and it becomes your next soundboard pad.

Someone said something legendary in the call? `Ctrl+Alt+D` clips it, trim it in two drags, drop it on the board — and replay it at them forever. All at ~0% CPU while idle.

![ReplayPad](docs/screenshot.png)

## Features

**Soundboard**
- Pad grid with its own library — **drag & drop MP3/WAV files** to import, or promote replays with one click
- **Categories** (chips above the grid, folder-backed) — drag pads onto chips to move sounds, Ctrl+drag to copy, drop Explorer files on a chip to import straight into it
- Per-sound **volume** (10–300%), **pad colors**, **pin to top**, labels independent of file names, playing pads show a **progress bar**
- **Global hotkeys**: bind sounds to `Ctrl+Alt+1`–`9` — or give any sound a fully **custom hotkey** (`Ctrl+Shift+B`, `F9`, …); stop everything with `Ctrl+Alt+0` or **Stop all** (with a smooth fade-out)
- **Export / Import** the whole board as a zip — share your soundboard with friends, sounds + labels + colors + hotkeys included
- **Quick launcher** — `Ctrl+Alt+Q` anywhere (even mid-game): type two letters, Enter, the sound plays into your call
- **Overlap or interrupt** mode: layer sounds over each other, or let each new sound cut the previous one
- **📤 Share app audio** — send one app's sound (a YouTube tab, Spotify, a game…) into the call through your mic, mixed with your sounds; you keep hearing it normally, with its own volume and a start/stop hotkey (`Ctrl+Alt+A`)
- **🔁 Loop** toggle: keep auto-replaying each sound (great for looping music or ambience) until you turn it off or stop the sound

**Replay buffer**
- Rolling in-RAM buffer (1–30 min): nothing written to disk until you save; `Ctrl+Alt+S` saves everything, `Ctrl+Alt+D` the last 30 s (all configurable)
- Capture the **whole desktop**, **one specific app** (just the game, just Discord), everything **except** one app, or the **microphone** — with live level meter and waveform
- Explicit **Start/Stop** control; always launches recording
- Recent replays list with **pagination** (15/30/50 per page or **All**), a live **search box** and a count: play to mic, edit, rename, delete (to Recycle Bin)

**Editor**
- Two **range handles** under the waveform — saving exports exactly the enclosed range, so trimming is drag → Save as copy
- Cut sections, fade in/out, volume, normalize, undo; preview with a live playhead
- **Effects:** reverse, 🐿 chipmunk (fast + high) and 🐻 deep voice (slow + low) — applied to the selection or the whole sound
- Safe overwrite (encodes to a temp file first — a failed save can never destroy the original)

**Transcription**
- Right-click any replay or pad → **Transcribe…** — speech-to-text via Groq's hosted Whisper (free API key), with Arabic/English/mixed-language support, copy button, and a `.txt` saved next to the audio
- **Synced playback**: play the audio inside the transcript window — the current line highlights and scrolls with the sound, and **clicking any line jumps playback to that moment**
- Needs a free key from [console.groq.com](https://console.groq.com) (⚙ Settings → Transcription). Note: the audio is uploaded to Groq for processing.

**Backup & restore**
- **☁ Google Drive backup** — sign in with Google and back up every sound, category (even empty ones), replay, transcript, label, color, hotkey and your settings to **your own Drive**; restore it all on any PC. No ReplayPad account, no server
- **Automatic backups** (daily / every 3 days / weekly), keeping the last few per PC
- **Backup to a file** — the same complete backup as one `.zip` for a USB stick or anywhere else
- Restores never overwrite or delete anything

**App**
- Dark UI with an icon rail switching between full-window Soundboard and Replay modes
- Closes to the system tray and keeps recording; optional start with Windows (hidden)
- Settings live in `%AppData%\ReplayPad` and **survive updates**; built-in **update checker**
- Crash-resilient: unexpected errors are logged (`log.txt`) and the app keeps recording

## Requirements

- **Windows 10 (version 2004+) or Windows 11** (per-app capture needs 2004+)
- **For "play into the call":** a virtual audio cable — [VB-CABLE](https://vb-audio.com/Cable/) (free) or [Voicemod](https://www.voicemod.net/). Not needed for recording or local playback.
- MP3 encoding uses Windows' built-in Media Foundation; [ffmpeg](https://ffmpeg.org/) is an automatic fallback if present.
- The installer and portable zip are **self-contained** — no .NET installation needed. Building from source needs the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Installation

**Easiest:** download **`ReplayPad-Setup-x.x.x.exe`** from the **[Releases page](https://github.com/TareqAli-CS/ReplayPad/releases)** and run it — per-user install, no admin rights, Start Menu shortcut, clean uninstaller. A **portable zip** (unzip & run) is also there. The app checks for new releases on startup and via tray → *Check for updates*.

> Windows SmartScreen may warn about the unsigned exe — click *More info → Run anyway*.

**From source:**

```powershell
git clone https://github.com/TareqAli-CS/ReplayPad.git
cd ReplayPad
dotnet publish ReplayPad -c Release -r win-x64 --self-contained true -o publish
publish\ReplayPad.exe
```

The installer itself is built from [installer/ReplayPad.iss](installer/ReplayPad.iss) with [Inno Setup](https://jrsoftware.org/isinfo.php).

Google Drive backup needs an OAuth client of your own when building from source — see [docs/google-drive-setup.md](docs/google-drive-setup.md). Without it everything works except the Drive part (file backups still do).

## Quick Start

1. **Run the app** — it opens on the soundboard and immediately starts buffering desktop audio (red dot in the rail = recording).
2. **Set up the call output once:** ⚙ Settings → *Play to mic* → pick your virtual cable device (see below).
3. **Add sounds:** drag MP3/WAV files onto the board, or clip a live moment with `Ctrl+Alt+D` and use *Send to soundboard*.
4. **Click a pad** — your friends hear it. `Ctrl+Alt+Q` mid-game does the same without leaving your game.
5. **Protect your library (optional):** click **☁** in the rail → *Sign in with Google* → *Back up now*, and tick *Back up automatically*.

## Connecting It to Discord (or any call app)

Windows doesn't let apps inject audio into a physical microphone, so every soundboard works through a **virtual audio device**:

1. Install [VB-CABLE](https://vb-audio.com/Cable/), or use Voicemod's virtual device if you have it.
2. In **⚙ Settings → Play to mic**, pick the cable's *playback* side (e.g. `CABLE Input (VB-Audio Virtual Cable)` or `Line (Voicemod Virtual Audio Device)`).
3. In Discord, set the **input device** to the cable's *microphone* side (e.g. `CABLE Output`). **Voicemod users:** keep Discord on the Voicemod mic — your voice and the sounds get mixed automatically.

**Hearing an echo?** Untick *"Hear it too"* — the echo is the sound playing on your speakers and being picked up again by your real mic (or doubled by Voicemod's own monitoring).

## Using the Soundboard

- **Play:** click a pad (green border = playing; click again stops it). *Stop all* or `Ctrl+Alt+0` silences everything instantly.
- **Scrub / pause:** while a sound plays, a transport bar appears at the bottom — drag the seek slider to jump to any point (great for starting a music track from the middle), pause/resume, and see position / duration.
- **Loop:** click **🔁 Loop** (top-right) to auto-replay — every sound repeats from the start when it ends and keeps going until you click Loop again or stop the sound. Toggle it on before or during playback.
- **Organize:** ＋ Category creates a category (a subfolder of the library — everything stays visible in Explorer). Drag pads onto chips to move sounds (**Ctrl+drag copies**), or right-click → *Move / copy to category…*.
- **Customize:** right-click → *Rename / hotkey / volume…* for the label, file name, **pad color**, per-sound volume, category, a `Ctrl+Alt+1..9` slot **or any custom hotkey combo** — click the hotkey field and just **press the combo** to set it (Backspace removes it). *Pin / unpin* keeps favorites at the top.
- **Share:** the **Export** button zips the whole board (sounds + labels + colors + hotkeys + order); **Import** merges someone else's board into yours without overwriting anything. (Export is for sharing a board; to protect *your whole library* — replays and settings included — use **☁ Backup & restore**.)
- **Reorder:** drag a pad **onto another pad** to place it before it, or onto empty grid space to send it to the end — the order is remembered.
- **Edit:** right-click → *Edit sound…* opens the full editor; saved copies appear on the board immediately.
- **Find:** the search box filters pads as you type; `Ctrl+Alt+Q` opens the global launcher with the same search.
- **Share an app:** the **📤 Share app audio** bar above the pads sends one app's sound into the call. Start the app's audio (e.g. play a YouTube video), pick the app from the list (it shows apps that are playing sound), press **▶ Share** — friends now hear it through your mic, mixed with your voice (Voicemod) and your pads. You keep hearing it normally; the slider sets how loud *they* hear it. `Ctrl+Alt+A` (or the tray menu) starts/stops sharing the last app from anywhere. Sharing stops by itself if the app closes. Discord and Voicemod can't be shared (the call would echo back into itself).

## The Replay Buffer

Switch to 🎙 in the rail for the recorder: live level meter, buffer fill, waveform of the buffered audio, **■ Stop / ▶ Start** control, and the recent replays list (double-click a replay to fire it into the call, right-click for everything else). The buffer keeps only the last N minutes in RAM and writes nothing until you save — silence stays accurate, device switches (plugging in headphones) are handled, and the app always launches recording.

**Capture sources** (⚙ Settings → Capture): entire desktop, microphone only, both mixed — or a **single app** picked from the apps currently playing audio (also invertible: everything *except* that app). Per-app capture means your music never ends up in the clip.

## Backup & Restore

Click **☁** in the rail.

- **Google Drive:** *Sign in with Google* → approve in the browser → **☁ Back up now**. Your backups appear in a **ReplayPad Backups** folder in your Drive, and in the list in the window (with date, PC name and contents). On another PC: install ReplayPad, sign in with the same account, pick a backup → **Restore selected**.
- **Automatic:** tick *Back up automatically* and pick how often; ReplayPad keeps the newest 3 / 5 / 10 backups of each PC and deletes older ones (never another PC's).
- **File:** *Save backup to file…* / *Restore from file…* — same content, no account needed.

Only one backup or restore runs at a time; a running one shows a progress bar and can be **cancelled** safely (nothing half-written is left behind). Automatic backups run quietly in the background — first check about 2 minutes after the app starts, then every 30 minutes — and only warn you (tray balloon) if one fails.

A backup contains the soundboard with every category folder, the replays (optional) with their transcripts, all labels / colors / volumes / pins / order / hotkeys, and your settings (never your library path or Groq API key). **Restoring only adds:** files you already have are kept (a different file with the same name is restored as ` (1)`), labels and colors only fill in where yours are empty, and settings are only replaced if you tick *also restore settings*.

**Privacy:** ReplayPad talks to Google directly — there's no ReplayPad server. It asks only for the `drive.file` permission, so it can see the backups it created and **nothing else in your Drive**. Your sign-in is stored on your PC only, encrypted with your Windows account; *Sign out* revokes it. Full details: [PRIVACY.md](PRIVACY.md).

## Default Hotkeys

| Hotkey | Action |
|---|---|
| `Ctrl+Alt+S` | Save the whole buffer as MP3 |
| `Ctrl+Alt+D` | Save the last 30 seconds |
| `Ctrl+Alt+1`–`9` | Play soundboard slot into the call (press again to stop it) |
| `Ctrl+Alt+0` | Stop all soundboard playback |
| `Ctrl+Alt+Q` | Quick launcher (search & play any sound) |
| `Ctrl+Alt+A` | Start/stop sharing the last app's audio into the call |

Sound hotkeys toggle: pressing a sound's combo while it plays **stops it** instead of restarting it. All hotkey fields are set by **clicking and pressing the combo** (✕ removes it). Custom sound hotkeys only apply to sounds in your **current** library folder, so an old library or a restored copy elsewhere can't steal a combo.

All configurable in ⚙ Settings.

## Configuration

Everything ReplayPad keeps about you lives in `%AppData%\ReplayPad` and **survives updates and reinstalls**:

| File | What's in it |
|---|---|
| `appsettings.json` | App settings (table below), edited in ⚙ Settings |
| `soundboard.json` | Labels, pad colors, per-sound volumes, pins, pad order, `Ctrl+Alt+1..9` slots and custom hotkeys |
| `backup.json` | Backup preferences, edited in ☁ Backup & restore (table below) |
| `appshare.json` | Share app audio: last shared app, its volume, and the start/stop hotkey |
| `google-signin.dat` | Your Google sign-in — only if you signed in; encrypted with your Windows account, so it's useless if copied to another PC or user. Deleted on *Sign out* |
| `log.txt` | Error / event log |
| `*.bak` | Automatic previous-version copies, used to recover if a file gets damaged |

Your audio itself is in the library folder (`OutputFolder`, below). Uninstalling ReplayPad never deletes either.

**App settings** (`appsettings.json`):

| Setting | Default | Meaning |
|---|---|---|
| `CaptureMode` / `TargetApp` / `TargetAppExclude` | `Desktop` / — / `false` | What the buffer records |
| `BufferMinutes` / `Bitrate` | `5` / `192` | Buffer length (≈11 MB RAM per minute) and MP3 quality |
| `Hotkey` / `ClipHotkey` / `ClipSeconds` / `LauncherHotkey` / `StopHotkey` | see table above | Hotkeys (all configurable) |
| `OutputFolder` | `Music\Replays` | Replays folder; the soundboard library is its `Soundboard` subfolder |
| `VoiceDevice` / `VoiceVolume` / `VoiceAlsoSpeakers` | — / `100` / `true` | Call output device, master volume, self-monitor |
| `SoundboardOverlap` | `false` | Sounds layer over each other instead of cutting |
| `GroqApiKey` | — | Free key for transcription (stored only on your PC) |
| `DesktopGain` / `MicrophoneGain` | `1.0` | Capture volume per source |

**Backup preferences** (`backup.json`):

| Setting | Default | Meaning |
|---|---|---|
| `AutoBackup` | `false` | Back up to Google Drive automatically |
| `EveryDays` | `1` | How often: `1`, `3` or `7` days |
| `KeepCount` | `5` | Newest Drive backups kept **per PC** (`3` / `5` / `10`); older ones from this PC are deleted |
| `IncludeReplays` | `true` | Include replays, not just soundboard sounds |
| `LastBackupUtc` | — | When this PC last backed up (drives the automatic schedule) |

## Troubleshooting

- **"Hotkey is already in use"** — another app owns that combo; change it in Settings.
- **Friends can't hear sounds** — Discord's input must be the *cable's microphone* side (step 3 above).
- **Per-app capture says the app is not running** — start the target app first, or switch back to *All apps*.
- **Echo in the call** — untick *"Hear it too"*, or use headphones.
- **My app isn't in the Share list** — the list only shows apps that are playing sound *right now*: start the video/music first, then open the list again. Browsers appear as e.g. `chrome` / `msedge`.
- **Friends don't hear the shared app** — sharing goes to your ⚙ *Play to mic* device, so Discord's input must be that device's mic side (see *Connecting It to Discord*), and the bar must show "● Sharing …".
- **Saves blocked by Windows Ransomware Protection** — ReplayPad rescues the clip to a safe fallback folder and shows a dialog with one-click fixes: allow the app in Windows Security (recommended), or switch the library to an unprotected folder.
- **"Google hasn't verified this app"** when signing in — expected for a small open-source app; click *Continue* (or *Advanced → Go to ReplayPad*). ReplayPad only gets access to its own backup files.
- **"Access blocked" / "has not completed the Google verification process"** — you're running a build from source whose Google project is still in *Testing*: add your Gmail as a test user, or publish the project (see [docs/google-drive-setup.md](docs/google-drive-setup.md)). Official releases don't have this problem.
- **"Your Google sign-in expired or was removed"** or an *Automatic backup failed* balloon — open ☁ and sign in again (happens if you removed ReplayPad's access at [myaccount.google.com/permissions](https://myaccount.google.com/permissions), or after 6 months without using it).
- **Backup is slow** — the first backup uploads everything (replays can be large; untick *Include replays* to back up just the soundboard). If the connection drops, it resumes instead of starting over.
- **"Google backup isn't set up in this build"** — you built from source without a `google-oauth.props`; file backups still work.
- Errors are logged to `%AppData%\ReplayPad\log.txt`; unexpected errors won't kill the app.

## Tech Notes

C# / .NET 10, WPF (dark UI, custom-drawn waveforms), [NAudio](https://github.com/naudio/NAudio) for WASAPI capture/playback and Media Foundation encoding. Per-app capture is a hand-written COM interop of the Windows process-loopback API — used both for recording one app and for *Share app audio*, which copies an app's stream into the call device through a self-trimming jitter buffer (so it never drifts out of sync or builds up delay). Google Drive backup uses the Drive v3 REST API directly (no SDK, no backend): OAuth sign-in via a loopback redirect with PKCE, resumable chunked uploads, and the refresh token protected with Windows DPAPI. Design details in [Architecture.md](Architecture.md); [project.md](project.md) is the original concept document.

## License

Free for **personal, non-commercial** use — see [LICENSE](LICENSE). Commercial use requires permission.
