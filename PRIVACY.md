# ReplayPad Privacy Policy

_Last updated: September 27, 2026_

ReplayPad is a free, open-source Windows app. It has **no servers, no accounts and no analytics** — the developer never receives any of your data.

## What stays on your PC

Everything ReplayPad records or manages — the audio buffer, saved replays, soundboard sounds, labels, settings and logs — is stored only on your computer (your library folder and `%AppData%\ReplayPad`). The rolling audio buffer lives in memory and is never written anywhere until you save a replay.

## Google Drive backup (optional)

If you choose **Sign in with Google** in ReplayPad's *Backup & restore* window:

- ReplayPad connects **directly from your PC to Google** — no ReplayPad server is involved.
- It requests only the `drive.file` permission, which lets it create, see and manage **only the backup files it created itself**. It **cannot** see, read or change any other file in your Google Drive.
- Backups are uploaded to a **ReplayPad Backups** folder in **your own** Google Drive. They contain your soundboard sounds, category folders, replays (if you choose), transcripts, labels/colors/hotkeys and app settings. Your Groq API key and your library path are never included.
- Your Google sign-in token is stored **only on your PC**, encrypted with your Windows account. *Sign out* in ReplayPad deletes it and revokes ReplayPad's access; you can also remove access any time at <https://myaccount.google.com/permissions>.
- The developer has no access to your Google account, your Drive or your backups.

ReplayPad's use of information received from Google APIs adheres to the [Google API Services User Data Policy](https://developers.google.com/terms/api-services-user-data-policy), including the Limited Use requirements.

## Transcription (optional)

If you add a Groq API key and use *Transcribe…*, the selected audio is uploaded to Groq's API for speech-to-text, under Groq's own privacy terms. Nothing is sent unless you start a transcription.

## Update check

On startup ReplayPad asks GitHub's public API for the latest release version number. No personal data is sent.

## Contact

Questions: open an issue at <https://github.com/TareqAli-CS/ReplayPad/issues>.
