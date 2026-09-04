# Huginn — meeting recorder + local transcription for macOS

Munin's twin raven. Munin is the Windows dictation + meeting app; Huginn is
**meeting recording only**, for macOS, and it transcribes on the machine that
recorded — nothing is uploaded anywhere.

Menu bar app. Start recording, stop recording, get a transcript.

## What it does

Two-track capture, the same design Munin uses on Windows:

| Track | Source | Labelled |
|---|---|---|
| **Me** | microphone (AVAudioEngine) | your voice, no ML needed |
| **Them** | system audio (ScreenCaptureKit) | everyone else, since they come out of the speakers |

Both tracks are written as 16 kHz mono WAV, transcribed locally by whisper.cpp
with Metal acceleration, and interleaved by timestamp into:

```
Recorded Aug 5, 2026 at 2:30 PM

[00:00:04] Me: Morning — can you hear me?
[00:00:07] Them: Loud and clear.
```

Output lands in `~/Library/Application Support/Huginn/Recordings/` — reachable
from the menu via **Open Recordings Folder**.

**No virtual audio driver.** ScreenCaptureKit captures system audio natively, so
there is no BlackHole or Loopback install and no Audio MIDI Setup fiddling. The
cost is that system audio sits behind the Screen Recording permission.

## Requirements

- **Apple Silicon** (M1 or newer) — the build is arm64 only, no Intel slice
- macOS 13 Ventura or newer
- ~2 GB free disk for the speech model (downloaded once, on first launch)

## Install

1. Download `Huginn.zip` from the build artifact and unzip it.
2. Drag **Huginn.app** into your Applications folder.
3. The app is not notarized (that needs a paid Apple account), so macOS will
   block it the first time. Pick whichever is easier:

   **Without Terminal** — double-click Huginn, let it get blocked, then open
   **System Settings → Privacy & Security**, scroll down, and click
   **Open Anyway** next to the message about Huginn.

   **With Terminal** — one command, and it never asks again:
   ```bash
   xattr -cr /Applications/Huginn.app
   ```

4. Launch it. A waveform icon appears in the menu bar.
5. It immediately starts downloading the speech model (~1.6 GB). The menu shows
   progress. **Start Recording** stays greyed out until that finishes — once
   only, not per meeting.
6. The first time you record, macOS asks for two permissions. Both are required:
   - **Microphone** — your side of the call
   - **Screen & System Audio Recording** — everyone else's side

   If you miss the Screen Recording prompt, enable Huginn under
   **System Settings → Privacy & Security → Screen & System Audio Recording**,
   then quit and reopen Huginn (that permission only takes effect on relaunch).

## Using it

- **Start Recording** — begins capture, the menu bar icon turns into a red dot,
  and the menu shows a running timer. The red dot is the consent indicator;
  leave it visible.
- **Stop Recording** — returns to idle immediately. Transcription runs in the
  background, so you can start the next meeting while the previous one is still
  processing. A dialog offers to open the transcript when it's ready.
- **Stop & Discard** — stops and deletes the audio, transcribes nothing. For a
  meeting you decide you don't want to keep.
- **Recent** — the last 5 meetings and their status. Each one is a submenu:
  **Open Transcript**, **Rename…** (the transcript file takes the name too:
  `{stamp}_{name}.txt`), **Show in Finder**, **Copy Path**. A name given while
  a meeting is still transcribing applies when the transcript is written.
- **Notes** — a floating notepad for the meeting, off by default. **Always On**
  keeps it open; **Open When Recording Starts** opens it (or clears it) at
  Start Recording. Its header shows the running time and the same Stop /
  Discard as the menu; after Stop it reads "Transcribing…" and then offers
  **Open transcript** / **Copy path** until the next recording starts. Every
  line is saved to `{stamp}_notes.md` beside the WAV files, stamped
  `[hh:mm:ss]` with the second you began the line — the transcript's own
  clock — so the two can be read side by side. Lines typed before Start carry
  `[00:00:00]`. Start clears the previous meeting's untouched notes; anything
  you typed since stays.
- **Open Log** — if something goes wrong, this is the file to send back.

Transcription takes roughly a fifth of the meeting's length on Apple Silicon, so
about 10 minutes for an hour-long call.

## Building

There is no Mac in the loop on the authoring side, so **GitHub Actions is the
compiler**. Push to `main` (or run the workflow manually) and the macOS runner
produces a signed, zipped `Huginn.app` as the `Huginn-macOS` artifact.

The workflow builds whisper.cpp from source rather than installing it from
Homebrew, because the Homebrew formula leaves Metal disabled unless
`GGML_METAL_PATH_RESOURCES` is set — which is not dependable from inside an app
bundle, and without Metal an M-series Mac transcribes about 5x slower. Building
with `GGML_METAL_EMBED_LIBRARY=ON` puts the shaders in the binary and removes
the problem entirely.

Metal is treated as a build requirement, not a preference: the workflow asserts
`GGML_METAL` and `GGML_METAL_EMBED_LIBRARY` actually stuck in `CMakeCache.txt`
and fails the build if either did not, so a silently CPU-only binary can never
reach a user. `GGML_NATIVE` is left OFF so the result is a baseline arm64 build
rather than one tuned to whichever M-series chip the runner happened to be;
that setting is CPU tuning and has no bearing on Metal.

Output is **arm64 only**. There is no Intel slice — it would not run on an Intel
Mac at all. Adding one means restoring `-DCMAKE_OSX_ARCHITECTURES="arm64;x86_64"`
and building Swift with `--arch arm64 --arch x86_64`, which also moves the
SwiftPM output path, so the assemble step would need updating too.

Signing is ad-hoc (`codesign --sign -`), which is free. Apple Silicon refuses to
run a completely unsigned binary, but ad-hoc is enough; Gatekeeper's
"unidentified developer" prompt is then handled once, on the user's side, per
the install steps above. Hardened runtime is deliberately off — enabling it
would require a real Developer ID to pair with the microphone entitlement.

To build locally instead, on a Mac with Xcode command line tools:

```bash
swift build -c release
```

...then assemble the bundle the same way the workflow does. A bare
`swift run` also works for quick iteration, but TCC permissions are keyed to the
bundle, so permission behaviour is only representative from a real `.app`.

## Layout

```
Sources/Huginn/
  main.swift            NSApplication bootstrap, menu-bar-only activation policy
  AppDelegate.swift     menu, state machine, transcription pipeline, rename, notes lifecycle
  NotesWindow.swift     the floating notepad (status, Stop/Discard, Open/Copy, stamped lines)
  Recorder.swift        2-track capture: AVAudioEngine mic + SCStream system audio
  AudioTrackWriter.swift  format conversion to 16 kHz mono WAV
  Transcriber.swift     whisper-cli subprocess + JSON parsing
  ModelDownloader.swift first-run model fetch
  JobStore.swift        the recent-meetings list, persisted (with the user's name per meeting)
  Paths.swift           locations + config constants
Sources/HuginnCore/     pure Foundation, covered by `swift test`
  Transcript.swift      timestamp interleave into the Me/Them format
  TranscriptReconciler.swift  text-based de-echo
  MeetingNotes.swift    the notes file shape ([hh:mm:ss] text per line)
  LineStamps.swift      one stamp per notepad line, kept aligned as you edit
  TranscriptNaming.swift  {stamp}_{name}.txt, file-safe
```

## Known limits

- **No diarization.** "Them" is everyone on the far side as one speaker, exactly
  like Munin Phase 1. Telling apart individual remote participants needs
  diarization (sherpa-onnx or pyannote) and is not built here.
- **Echo removal is text-based, not acoustic.** On speakers the mic re-captures
  the far side, so their lines come back as `Me:` lines. `TranscriptReconciler`
  drops those by comparing each `Me` line against nearby `Them` speech, resting
  on the invariant that the `Them` track cannot contain the local mic. It is
  deliberately conservative — when unsure it keeps the line — so some echo can
  survive, and the pre-de-echo text is kept as `*_transcript.raw.txt` whenever
  anything was removed. Headphones still give the cleanest result.
- **English only** by default (`Config.language`).
- Transcription assumes both tracks start at the same instant. There is a small
  skew between the mic engine starting and the capture stream starting, so
  timestamps can drift by a fraction of a second between speakers.
