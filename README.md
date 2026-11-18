# Huginn — dictation and meeting recorder for Windows and Mac

Huginn does two things, on the machine you use it on, without uploading audio anywhere:

- **Dictation.** Hold a key, speak, let go: Whisper transcribes it and the text is pasted where the cursor is. An
  optional local LLM cleans the text up first.
- **Meetings.** Two tracks: your microphone is **Me**, everything the speakers play is **Them**. After a meeting the
  tracks are transcribed in speech chunks, your speakers' echo is removed from the Me track, and the transcript is
  written as `[hh:mm:ss] Me: …` / `Them: …` lines. With Kumospace, Huginn starts and stops the recording itself and
  puts each person's name on their lines. A finished meeting can be filed to Odin.

The Windows app was called **Munin** until 2026-11-18. Both apps now share one core; only the UI differs.

## Layout

```
core/      Huginn.Core: every decision. Transcription, speech chunks, echo gates, de-echo, the meeting pipeline,
           Kumospace auto-record and speaker names, the work timer, Odin filing, settings, the log, the recent list,
           notes stamps, and HuginnEngine (the state machine both apps run). Platform.cs is the contract each OS fills.
windows/   The Windows shell: WinForms tray app, NAudio capture (WaveIn mic, WASAPI loopback), keyboard hook, paste.
mac/       The Mac shell: AppKit menu bar app, AVCaptureSession mic, ScreenCaptureKit system audio, CGEvent-tap key, paste.
tests/     Tests for the core (xunit). They run on Windows and macOS in CI.
```

**The rule:** a feature goes into `core/`. If the same logic would have to be written in `windows/` and `mac/`, it
belongs in the core.

## What it does, menu by menu

| Item | What it does |
|---|---|
| Dictation (hold the key) | Turns push-to-talk on or off without quitting. Remembered across restarts. Meetings keep working while it is off. |
| Microphone | The mic for dictation and meetings. |
| Clean up text (AI) | Runs the dictated text through a local Qwen 2.5 3B before pasting. |
| Start / Stop meeting recording | Two-track recording. The recorder panel shows a live level meter. |
| Stop & discard recording | Stops and deletes the audio. Nothing is transcribed or filed. |
| Auto-file meetings to Odin | Posts each transcript (and your notes) to Odin's `/api/meetings/process`. Shown only when `OdinBaseUrl` is set in settings.json; Odin is Bohdan's own app, so by default the item is hidden and nothing is posted. |
| Auto-record Kumospace meetings | Starts when you are with someone and someone talks; stops 5 s after you are alone or after 3 min of quiet; deletes a chat with under 20 s of talk. |
| Kumospace status / today | Who you are with; your Kumospace availability as a work timer, today and this week (Monday first). |
| Show recorder panel | A floating window: level meter, Record/Stop, and the last 8 meetings: name (Odin's title or yours), time, status. ✕ cancels a job in flight; the copy icon copies the transcript path; double-click renames (the file follows); right-click opens or reveals it. |
| Notes window | A notepad for the meeting. Each line is stamped with the second you began it and saved as `{stamp}_notes.md`; Odin treats every line as a must-include. |

The cancel key (Escape) drops a dictation in progress, even mid-transcription.

## Keys

- **Windows:** hold **Left Ctrl + Space**.
- **Mac:** hold **Right Option** (Ctrl+Space switches input sources on a Mac).

Both are set in `settings.json` (`Hotkey` on Windows; `MacHotkeyKeyCode`, `MacHotkeyModifiers`, `MacCancelKeyCode` on Mac).

## Data folder

Windows `%LOCALAPPDATA%\Huginn`, Mac `~/Library/Application Support/Huginn`:

- the Whisper models (downloaded once on first use), `settings.json`, the switch files (`dictation.txt`, `cleanup.txt`, …)
- `huginn.log`: every meeting job, failure and Kumospace change
- `Meetings/`: WAV tracks, notes, speaker timelines, transcripts, `recent.json`
- `Presence/`: the work timer, one file per day
- `quit.flag`: another app writes it to make Huginn save and exit

On its first start the Windows app renamed `%LOCALAPPDATA%\Munin` to `%LOCALAPPDATA%\Huginn` (same drive, nothing
copied) and left a junction at the old path. The Mac app moves the Swift app's `Models/` model up into the data folder.

## Download

Every green build is published as a release:

- Mac: [Huginn-macOS.zip](https://github.com/BohdanRoshko/huginn/releases/latest/download/Huginn-macOS.zip)
  (Apple Silicon, macOS 13 or newer)
- Windows: [Huginn-Windows-x64.zip](https://github.com/BohdanRoshko/huginn/releases/latest/download/Huginn-Windows-x64.zip)
  (Windows 10 1903 or newer, x64)

All releases: [releases](https://github.com/BohdanRoshko/huginn/releases).

## Install on Windows

1. Install the **.NET 10 Desktop Runtime** once:
   `winget install Microsoft.DotNet.DesktopRuntime.10`
   (or the ".NET Desktop Runtime 10" x64 installer from https://dotnet.microsoft.com/download/dotnet/10.0).
2. Unzip `Huginn-Windows-x64.zip` to a folder that stays, for example `%LOCALAPPDATA%\Programs\Huginn`.
3. Run `Huginn.exe`. The app is not signed, so SmartScreen may warn: **More info → Run anyway**.
4. A dot appears in the tray, and the recorder panel opens. The speech model (~1.6 GB) downloads on first use.
   A GPU with a current Vulkan driver (NVIDIA, AMD or Intel) makes transcription fast; without one it falls back to
   the CPU, which is much slower.
5. To start it at logon: press Win+R, type `shell:startup`, and put a shortcut to `Huginn.exe` there.
6. Kumospace: Huginn adds `--remote-debugging-port=9222` to the Kumospace shortcuts. If Kumospace was already
   running, click **Kumospace: restart it…** in the tray menu once.

Right-click the tray dot for the menu. Hold **Left Ctrl + Space** to dictate; **Dictation** in the menu turns it off.

## Install on a Mac

1. Download `Huginn-macOS.zip` (above), unzip it, drag **Huginn.app** into Applications.
2. The app is ad-hoc signed, not notarized, so macOS blocks the first launch. Either open **System Settings → Privacy &
   Security** and click **Open Anyway**, or run once: `xattr -cr /Applications/Huginn.app`.
3. Launch it. A waveform appears in the menu bar; Huginn is a menu bar app, as the Swift app was. The menu has
   Start/Stop Recording, Stop & Discard (asks first), **Recent** with a submenu per meeting (Open Transcript,
   Rename…, Show in Finder, Copy Path, Cancel while it processes), Notes, Open Recordings Folder, Open Log, plus the
   dictation, microphone, cleanup and Kumospace items. While a meeting records, a red dot and the clock show in
   the menu bar. The recorder panel and the Notes window are off by default; turn them on in the menu.
   The speech model (~1.6 GB) downloads on first use.
4. macOS asks for three permissions. All are needed:
   - **Microphone**: dictation and your side of a meeting.
   - **Screen & System Audio Recording**: the other side of a meeting (ScreenCaptureKit; no virtual audio driver).
     It takes effect after quitting and reopening Huginn.
   - **Accessibility**: the dictation key and the paste.
5. Kumospace: the Dock starts it without the DevTools port Huginn reads. Click **Kumospace: restart it…** in Huginn's
   menu once; Huginn restarts it with `--remote-debugging-port=9222`.

Not on the Mac: pausing music while you dictate (macOS has no public API for it).

## Building

- **Windows:** `dotnet build -c Release` in `windows/` (needs the .NET 10 SDK). Output
  `windows\bin\Release\net10.0-windows10.0.19041.0\Huginn.exe`. GPU through Vulkan (the NVIDIA driver is enough).
- **Tests:** `dotnet test tests/Huginn.Core.Tests`.
- **Mac:** GitHub Actions is the compiler (no Mac on the authoring side). Push to `main`; the **Huginn** workflow tests
  the core on both systems, builds both apps, runs the shipped Mac app headless (`HUGINN_SELFTEST=transcribe` pushes
  synthesized speech through dictation and the full meeting pipeline; `capture` records a real meeting on the
  runner), photographs its windows, and uploads `Huginn-macOS`. On a Mac with the .NET 10 SDK and the `macos`
  workload: `dotnet build mac/Huginn.Mac.csproj -c Release`. Whisper and the cleanup LLM use their Metal builds.
- `HUGINN_DATA_DIR` points the app at another data folder (tests and CI use it).

## Known limits

- "Them" is everyone on the far side as one track. Names come only from Kumospace; other call apps get "Them".
- Echo removal works on what the mic re-captured; headphones still give the cleanest transcript.
- English by default (`Language` in settings.json).
