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
- **Recent** — the last 5 meetings and their status. Click a finished one to
  open its transcript.
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
  AppDelegate.swift     menu, state machine, transcription pipeline
  Recorder.swift        2-track capture: AVAudioEngine mic + SCStream system audio
  AudioTrackWriter.swift  format conversion to 16 kHz mono WAV
  Transcriber.swift     whisper-cli subprocess + JSON parsing
  Transcript.swift      timestamp interleave into the Me/Them format
  ModelDownloader.swift first-run model fetch
  JobStore.swift        the recent-meetings list, persisted
  Paths.swift           locations + config constants
```

## Known limits

- **No diarization.** "Them" is everyone on the far side as one speaker, exactly
  like Munin Phase 1. Telling apart individual remote participants needs
  diarization (sherpa-onnx or pyannote) and is not built here.
- **No echo removal.** If the meeting plays through speakers, the mic re-captures
  the far side, so some of their speech also appears as `Me:` lines. Munin's
  Windows counterpart solves this with a de-echo pass on ingest; that logic is
  not ported. Using headphones avoids the problem entirely — worth telling
  anyone who uses this.
- **English only** by default (`Config.language`).
- Transcription assumes both tracks start at the same instant. There is a small
  skew between the mic engine starting and the capture stream starting, so
  timestamps can drift by a fraction of a second between speakers.
