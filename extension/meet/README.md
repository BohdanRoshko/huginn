# Huginn for Google Meet

A Chrome extension that sends raised hands, reactions and chat from a Google Meet call to Huginn on the same
computer. Huginn writes them into the meeting's transcript while it records, the way it does for Kumospace.

## Install

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Click **Load unpacked** and pick this folder (`extension/meet`).
3. Reload any open Meet tab.

## How it works

- `content.js` runs on `meet.google.com`. In a call it watches the page for a hand icon on a person's tile, Meet's
  spoken announcements, emoji reactions floating up, and the chat list. To get the chat list into the page it opens
  the chat panel once and closes it again.
- `background.js` posts each batch to `http://127.0.0.1:47821/meet/events` with the header `X-Huginn: 1`.
- Huginn keeps the events only while it records. When Huginn is not running, they are dropped.

Meet's markup is not a public API. The console shows `[huginn]` lines for every event it reads, so a change on
Google's side shows up there first.
