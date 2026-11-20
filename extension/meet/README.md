# Huginn for Google Meet

A Chrome extension that sends who is talking, raised hands, reactions and chat from a Google Meet call to Huginn on
the same computer. While Huginn records, it puts each person's name on the lines they said and writes the rest into
the meeting's transcript, the way it does for Kumospace.

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
- `background.js` posts each batch to `http://127.0.0.1:47821/meet/events` with the header `X-Huginn: 1`.
- Huginn keeps the events only while it records. When Huginn is not running, they are dropped.

Meet's markup is not a public API. The console shows `[huginn]` lines for every event it reads, so a change on
Google's side shows up there first. For the sound mark Huginn's own log helps too: `meet <code>: talk mark classes: …`
lists the states the mark was seen in, `… is talking (the first time in this recording)` names each person once, and
`talk mark not found …` means the mark's name changed.
