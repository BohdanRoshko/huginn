# Huginn for meetings in the browser

A Chrome extension that sends who is talking and snapshots of a shared screen from a call in the browser to Huginn
on the same computer. While Huginn records, it puts each person's name on the lines they said and keeps the snapshots
next to the recording, the way it does for Kumospace. On Google Meet it also sends raised hands, reactions and chat.

| Site | Who is talking | Shared screen | Seen on a live call |
|---|---|---|---|
| Google Meet (`meet.google.com`) | the sound mark on each person's tile | the screen's video | hands, reactions and chat yes; talking and screens not yet |
| Microsoft Teams (`teams.microsoft.com`, `teams.cloud.microsoft`, `teams.live.com`) | the ring around each person's tile | the screen's stream on the stage | not yet |
| Slack huddle (`app.slack.com`) | the "active speaker" mark on a person's tile | a wide video, or one Slack's page names a screen share | not yet |
| Zoom web client (`*.zoom.us/wc/…`) | the active speaker's frame, one person at a time | the screen's canvas | not yet |

Only the browser is read. A call in the Teams, Slack or Zoom desktop app is recorded with sound only.

The folder is still called `extension/meet`, so an installed copy keeps working.

## Install

1. Open `chrome://extensions` and turn on **Developer mode**.
2. Click **Load unpacked** and pick this folder (`extension/meet`).
3. Reload any open call tab.

After an update of these files: click the reload arrow on the extension's card in `chrome://extensions`, then reload
any open call tab.

## How it works

- `content.js` is the part every site shares. Five times a second it asks the site's reader who is talking and
  queues a sample for each person. Only people the page shows have a mark; your own tile is skipped, because your
  voice is the microphone track.
- While someone shares a screen, it looks at that picture every 3 seconds. When the picture is the same as 3 seconds
  before and differs from the last snapshot, it sends a new snapshot: a JPEG at most 1600 pixels across. A slide gets
  one snapshot; scrolling gets none until it stops. The snapshot is as sharp as the site shows the screen in your
  window, so a small window gives a small picture.
- A shared screen is a video like a camera. It is taken for a screen when the site itself marks it as one, or when
  the picture is wider than any camera (over 1280). When the page only says that someone is presenting, a picture
  that is not camera-shaped, or is clearly the largest on the page, counts once it has held dead still twice in a
  row, which a person on camera does not do.
- `site-meet.js`, `site-teams.js`, `site-slack.js` and `site-zoom.js` each say how one site's page shows a call, the
  people, the talking mark and a shared screen. The manifest loads one of them after `content.js`. The Meet reader
  also watches for a hand icon on a person's tile, Meet's spoken announcements, emoji reactions floating up, and the
  chat list. To get the chat list into the page it opens the chat panel once and closes it again.
- `background.js` posts each batch of events to `http://127.0.0.1:47821/meet/events` and each snapshot to
  `/meet/shot`, with the header `X-Huginn: 1` and the name of the site's app.
- Huginn keeps the events and the snapshots only while it records. When Huginn is not running, they are dropped.

## What each site's reader relies on

No site's markup is a public API. The Teams, Slack and Zoom names come from other recorders' public code and notes
of 2026, not from a call seen here.

- **Teams.** A person is `[data-stream-type][data-tid]`; `data-tid` is the name. The ring is
  `[data-tid="voice-level-stream-outline"]`, and it counts as shown while its `::before` is opaque. Teams moves the
  ring about once a second, so a name can come a little early and stay a little late. A shared screen is
  `[data-stream-type="ScreenSharing"]`, whose `data-tid` is the presenter.
- **Slack.** A huddle is drawn in a window of its own with an empty address, opened from the Slack tab; the manifest
  runs the script there with `match_about_blank`. A person is `.p-huddle_peer_tile` with the label
  "View Name's profile"; the tile holds `.p-huddle_peer_tile__overlay--active_speaker` only while the person talks.
  Nothing public says how Slack marks a shared screen, so it is found by its size and by "screenshare" in the names
  around it.
- **Zoom.** Zoom has no talking mark on each person. It lights one tile in gallery view
  (`.gallery-video-container__video-frame--active`) and shows the active speaker large in speaker view, a second or
  two late, and the large tile stays on the last speaker during a silence. With the Participants panel open, each
  row shows who is talking, and that is read first. Zoom draws a shared screen on a canvas in an open shadow root;
  the script copies it into a small unseen `<video>` of its own and takes the snapshots from that. The presenter is
  read from "You are viewing Name's screen". The meeting is sometimes inside an iframe, so the script runs in every
  frame of `/wc/` pages.

## Reading the log

The console shows `[huginn]` lines for every event it reads, so a change on a site's side shows up there first.
Huginn's own log has a line per call, starting with `meet <code>:` for Google Meet and with the app's name for the
others (`Microsoft Teams <code>:`):

- `talk mark classes: …` lists the looks the talking mark was seen in, each once.
- `… is talking (the first time in this recording)` names each person once.
- `talk mark not found …` means the mark's name changed, or everyone was muted for a minute.
- `screen share: reading a 1920x1080 video … (sign: …)` says which picture it took and why.
- `snapshot of …'s screen saved as shot-001.jpg` is each picture kept.
- `screen share: the page says someone is presenting, but …` lists the videos it saw when it found no screen.
