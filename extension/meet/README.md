# Huginn for Google Meet

A Chrome extension that sends who is talking, raised hands, reactions, chat and snapshots of a shared screen from a
Google Meet call to Huginn on the same computer. While Huginn records, it puts each person's name on the lines they
said, writes the rest into the meeting's transcript and keeps the snapshots next to the recording, the way it does
for Kumospace.

## Install

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Click **Load unpacked** and pick this folder (`extension/meet`).
3. Reload any open Meet tab.

After an update of these files: click the reload arrow on the extension's card in `chrome://extensions`, then reload
any open Meet tab.

## How it works

- `content.js` runs on `meet.google.com`. In a call it reads the sound mark on each person's tile five times a second
  (the three small bars: who is talking now). Only people Meet shows on screen have a mark; your own tile is skipped,
  because your voice is the microphone track. It also watches the page for a hand icon on a person's tile, Meet's
  spoken announcements, emoji reactions floating up, and the chat list. To get the chat list into the page it opens
  the chat panel once and closes it again.
- While someone shares a screen, it looks at that video every 3 seconds. When the picture is the same as 3 seconds
  before and differs from the last snapshot, it sends a new snapshot: a JPEG at most 1600 pixels across. A slide gets
  one snapshot; scrolling gets none until it stops. The snapshot is as sharp as Meet shows the screen in your window,
  so a small Meet window gives a small picture.
- A shared screen is a video like a camera. The script tells it apart by the tile's name ("… (Presentation)"), by a
  picture wider than any camera (over 1280), or, when the page says someone is presenting, by a picture that is not
  camera-shaped or is clearly the largest on the page. The last two also need the picture to hold dead still twice in
  a row, which a person on camera does not do.
- `background.js` posts each batch of events to `http://127.0.0.1:47821/meet/events` and each snapshot to
  `/meet/shot`, with the header `X-Huginn: 1`.
- Huginn keeps the events and the snapshots only while it records. When Huginn is not running, they are dropped.

Meet's markup is not a public API. The console shows `[huginn]` lines for every event it reads, so a change on
Google's side shows up there first. For the sound mark Huginn's own log helps too: `meet <code>: talk mark classes: …`
lists the states the mark was seen in, `… is talking (the first time in this recording)` names each person once, and
`talk mark not found …` means the mark's name changed. For a shared screen: `screen share: reading a 1920x1080 video
… (sign: …)` says which video it took and why, `snapshot of …'s screen saved as shot-001.jpg` is each picture kept,
and `screen share: the page says someone is presenting, but …` lists the videos it saw when it found no screen.
