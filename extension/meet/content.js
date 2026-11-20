// Huginn in the browser, the part every meeting site shares. A site file (site-meet.js, …) says how its page shows
// a call, the people in it, who is talking and a shared screen. This file reads that five times a second, takes the
// snapshots of a shared screen, and hands everything to the background worker, which passes it to Huginn on this
// computer. It reads the page only; it sends nothing to the meeting site.
//
// A site file calls HUGINN.start({ … }) with:
//   app            the meeting app's name, as Huginn writes it in a transcript ("Google Meet")
//   meeting()      a short name of this call, for the log
//   inCall()       is a call going on in this page
//   selfName()     the signed-in person's name, or ""
//   talkMark       what the "who is talking" mark is, in words, for the log
//   people()       the other people on screen: [{ name, talking, state }]. talking is true, false, or null when the
//                  page shows no mark for that person (muted). state is the mark's look as text, for the log.
//   aboutVideo(el) for a video or canvas: { name, self, named }: the name on its tile, is it my own camera, and
//                  does the page itself call it a shared screen
//   canvases()     optional: canvases that may hold a shared screen (a site that does not use <video> for it)
//   pageSaysPresenting()   does the page say someone is sharing a screen
//   presenter(name)        whose screen it is, from the tile's name or the page; "" when unknown
//   everySecond()  optional: the site's own reads (hands, chat)
//   onCallChange(inCall)   optional
(() => {
  "use strict";
  const TALK_EVERY_MS = 200;
  const queue = [];
  const recent = new Map(); // dedupe key -> time
  let site = null;
  let inCall = false;
  let selfName = "";

  const log = (...a) => console.log("[huginn]", ...a);
  const onScreen = (el) => el.getClientRects().length > 0;
  const me = (name) => (!name || /^you$/i.test(name.trim()) ? selfName || "Me" : name.trim());
  // The text a person can see on the page.
  const pageText = () => document.body.innerText ?? document.body.textContent ?? "";

  function push(kind, who, text, dedupeMs) {
    who = me(who);
    if (kind === "react") text = (text || "").replace(/[\s.!]+$/u, ""); // "reacted with 👍." from an announcement
    const key = `${kind}|${who}|${text || ""}`;
    const now = Date.now();
    if (dedupeMs && now - (recent.get(key) || 0) < dedupeMs) return;
    recent.set(key, now);
    queue.push({ t: now, kind, who, text: text || "" });
    log(kind, who, text || "");
  }

  // A remark about the page itself, for Huginn's log.
  function note(text) {
    queue.push({ t: Date.now(), kind: "note", who: "", text });
    log("note", text);
  }

  function flush() {
    if (queue.length === 0) return;
    const events = queue.splice(0);
    try { chrome.runtime.sendMessage({ type: "huginn-events", app: site.app, meeting: site.meeting(), events }); }
    catch { /* the extension was reloaded; this page keeps the old script until a reload */ }
  }

  function everySecond() {
    const now = !!site.inCall();
    if (now !== inCall) {
      inCall = now;
      log(inCall ? "joined" : "left", site.app, site.meeting());
      talkers.clear(); markStates.clear(); markSeenAt = 0; markMissingSince = 0; markMissingSaid = false;
      if (!inCall) endShare(Date.now());
      shareSaid.clear(); maybe = null; shareMissSince = 0; shareMissSaid = false; shotFailSaid = false;
      site.onCallChange?.(inCall);
    }
    if (inCall) {
      selfName = selfName || site.selfName() || "";
      site.everySecond?.();
    }
    for (const [k, t] of recent) if (Date.now() - t > 60000) recent.delete(k);
    flush();
  }

  // ---- who is talking: the mark the site shows on each person, read five times a second. Huginn puts the name of
  //      whoever talked on each line of the system audio. My own tile is skipped: my voice is the microphone track.
  //      Only people on screen are known; a person the page does not show has no mark to read. ----
  const talkers = new Set(); // names already seen talking in this call, for one console line each
  const markStates = new Set(); // looks of the mark, passed on once each so a change by the site can be read from the log
  let markSeenAt = 0, markMissingSince = 0, markMissingSaid = false;

  function scanTalk() {
    if (!inCall) return;
    const now = Date.now();
    const talking = new Set();
    let others = 0, marks = 0;
    for (const p of site.people()) {
      others++;
      if (p.talking == null) continue; // muted: the page shows no mark
      marks++;
      if (p.state != null && !markStates.has(p.state) && markStates.size < 12) {
        markStates.add(p.state);
        note(`talk mark classes: "${p.state}"${p.talking ? " (talking)" : " (quiet)"}`);
      }
      if (p.talking) talking.add(p.name);
    }
    for (const name of talking) {
      queue.push({ t: now, kind: "talk", who: name, text: "" });
      if (!talkers.has(name)) { talkers.add(name); log("talk", name); }
    }
    // Nobody unmuted is not a fault; other people on screen for a minute with no mark on any tile is.
    if (marks > 0) { markSeenAt = now; markMissingSince = 0; }
    else if (others > 0 && !markSeenAt) {
      markMissingSince = markMissingSince || now;
      if (!markMissingSaid && now - markMissingSince > 60000) {
        markMissingSaid = true;
        note(`talk mark not found on ${others} tile(s) for a minute: either everyone is muted or ${site.app} changed ${site.talkMark}`);
      }
    }
  }

  // ---- shared screens: who starts and stops, and a snapshot each time the shared screen settles on something new.
  //      A shared screen is a video like a camera, so four signs tell it apart, the surest first:
  //        1. the site itself calls it a shared screen (its tile's name, or its own place on the page);
  //        2. the picture is wider than any camera a meeting sends (over 1280);
  //        3. the page says someone is presenting, and the picture is not camera-shaped (16:9 or 4:3);
  //        4. the page says someone is presenting, and this is clearly the largest video that is not my own camera.
  //      Signs 3 and 4 can also fit a camera, so they count only once the picture has held dead still for two looks
  //      in a row: a screen does that, a person on camera does not. The "screen share" lines in Huginn's log say
  //      which sign matched, or which videos were on the page when none did. The snapshot is as sharp as the site
  //      shows the screen here. ----
  const SHOT_EVERY_MS = 3000;
  const SHOT_MAX_WIDTH = 1600;
  let share = null; // the screen being shared now: { who, unsure, el, lastGrid, keptGrid, seenAt }
  let maybe = null; // a video found by sign 3 or 4, until it has held still: { video, grid, still }
  const shareSaid = new Set(); // remarks already passed on in this call
  let shareMissSince = 0, shareMissSaid = false, shotFailSaid = false, sending = false;

  // A picture's own size: a video's frame, a canvas's drawing size.
  const widthOf = (el) => el.videoWidth || (el.tagName === "CANVAS" ? el.width : 0) || 0;
  const heightOf = (el) => el.videoHeight || (el.tagName === "CANVAS" ? el.height : 0) || 0;
  const sizeOf = (el) => `${widthOf(el)}x${heightOf(el)}`;
  const cameraShaped = (el) => {
    const r = widthOf(el) / heightOf(el);
    return [16 / 9, 4 / 3, 9 / 16, 3 / 4].some((c) => Math.abs(r - c) < 0.03);
  };

  // The shared screen's picture, whose it is, and which sign found it; or the pictures that were looked at.
  function findScreen() {
    const seen = [];
    for (const v of [...document.querySelectorAll("video"), ...(site.canvases?.() || [])]) {
      if (widthOf(v) < 320 || !heightOf(v) || !onScreen(v)) continue;
      const about = site.aboutVideo(v);
      const box = v.getBoundingClientRect();
      seen.push({ v, name: about.name || "", self: !!about.self, named: !!about.named, area: box.width * box.height });
    }
    const largest = (list) => list.slice().sort((a, b) => b.area - a.area)[0] || null;
    let hit = largest(seen.filter((s) => s.named)), sign = `${site.app} calls it a shared screen`, unsure = false;
    if (!hit) { hit = largest(seen.filter((s) => !s.self && widthOf(s.v) > 1280)); sign = "a picture wider than a camera's"; }
    const says = hit ? true : !!site.pageSaysPresenting();
    if (!hit && says) {
      unsure = true;
      hit = largest(seen.filter((s) => !s.self && !cameraShaped(s.v)));
      sign = "the page says presenting, the picture is not camera-shaped and it holds still";
    }
    if (!hit && says) {
      const others = seen.filter((s) => !s.self).sort((a, b) => b.area - a.area);
      if (others.length > 0 && widthOf(others[0].v) >= 640 && (others.length === 1 || others[0].area >= 1.5 * others[1].area)) hit = others[0];
      sign = "the page says presenting, this is the largest video and it holds still";
    }
    return { hit, sign, unsure, says, seen };
  }

  // The picture as a small grid of grey levels, to compare two looks; null while there is no picture yet.
  const GW = 96, GH = 54;
  function look(video) {
    const small = new OffscreenCanvas(GW, GH).getContext("2d", { willReadFrequently: true });
    small.drawImage(video, 0, 0, GW, GH);
    const px = small.getImageData(0, 0, GW, GH).data;
    const grid = new Uint8Array(GW * GH);
    let low = 255, high = 0;
    for (let i = 0; i < grid.length; i++) {
      const g = grid[i] = (px[i * 4] * 3 + px[i * 4 + 1] * 6 + px[i * 4 + 2]) / 10;
      if (g < low) low = g;
      if (g > high) high = g;
    }
    return high - low < 8 ? null : grid; // one flat colour is no picture
  }
  // The share of the grid that differs by more than `by` grey levels.
  const moved = (a, b, by) => { if (!b) return 1; let n = 0; for (let i = 0; i < a.length; i++) if (Math.abs(a[i] - b[i]) > by) n++; return n / a.length; };
  const deadStill = (a, b) => moved(a, b, 12) <= 0.001; // a blinking cursor passes; a person who breathes does not

  function endShare(now) {
    if (!share) return;
    queue.push({ t: now, kind: "share-stop", who: share.who, text: "" });
    log("share-stop", share.who);
    share = null;
  }

  function watchShare() {
    if (!inCall) return;
    const now = Date.now();
    const { hit, sign, unsure, says, seen } = findScreen();
    const failed = (ex) => { if (!shotFailSaid) { shotFailSaid = true; note(`screen share: the picture could not be read (${ex && ex.message ? ex.message : ex})`); } };
    let grid = null;
    if (hit) { try { grid = look(hit.v); } catch (ex) { failed(ex); } }

    // A video found by sign 3 or 4 is a shared screen only once it has held dead still for two looks in a row.
    // The picture already being read needs no second proof; a different one found by those signs does, also while
    // a share is still on (the real screen just went, and a camera is now the largest video).
    let held = null;
    const same = !!hit && !!share && share.el === hit.v;
    if (hit && grid && unsure && !same) {
      const still = maybe && maybe.video === hit.v && deadStill(grid, maybe.grid);
      held = still ? maybe.grid : null;
      maybe = { video: hit.v, grid, still: still ? maybe.still + 1 : 0 };
    }
    const reading = !!hit && !!grid && (!unsure || same || maybe.still >= 2);

    if (!reading) {
      if (!hit) maybe = null;
      else if (same) share.seenAt = now; // still there, with no picture for a moment
      if (share && now - share.seenAt > 2 * SHOT_EVERY_MS) endShare(now); // a site rebuilds the tile when the layout changes
      if (!says || share) { shareMissSince = 0; return; }
      shareMissSince = shareMissSince || now;
      if (!shareMissSaid && now - shareMissSince > 30000) {
        shareMissSaid = true;
        const videos = seen.map((s) => `${sizeOf(s.v)} "${s.name}"`).join(", ") || "none";
        const why = hit && grid ? `the likeliest one, ${sizeOf(hit.v)} "${hit.name}", keeps moving` : "no video looks like a shared screen";
        note(`screen share: the page says someone is presenting, but ${why} (videos: ${videos})`);
      }
      return;
    }
    shareMissSince = 0;
    const who = site.presenter(hit.name) || (share ? share.who : ""); // a name once read stays while the share goes on
    if (share && share.who !== who) endShare(now);
    if (!share) {
      share = { who, unsure, el: hit.v, lastGrid: held, keptGrid: null, seenAt: now };
      maybe = null;
      queue.push({ t: now, kind: "share-start", who, text: "" });
      log("share-start", who);
    }
    if (share.el !== hit.v) { share.el = hit.v; share.unsure = unsure; } // the site rebuilt the tile, or a surer one took over
    share.seenAt = now;
    const said = `screen share: reading a ${sizeOf(hit.v)} ${hit.v.tagName === "CANVAS" ? "canvas" : "video"} on the tile "${hit.name}" (sign: ${sign})`;
    if (!shareSaid.has(said) && shareSaid.size < 8) { shareSaid.add(said); note(said); }

    // A snapshot is kept when the picture is the same as 3 s ago (not mid-scroll or mid-animation) and differs from
    // the last one kept.
    const settled = share.unsure ? deadStill(grid, share.lastGrid) : moved(grid, share.lastGrid, 20) < 0.005;
    share.lastGrid = grid;
    if (!settled || moved(grid, share.keptGrid, 20) < 0.01 || sending) return;
    share.keptGrid = grid;
    sending = true;
    send(share.who, hit.v).catch(failed).finally(() => { sending = false; });
  }

  // The picture as a JPEG, at most SHOT_MAX_WIDTH across, handed to the background worker for Huginn.
  async function send(who, video) {
    const scale = Math.min(1, SHOT_MAX_WIDTH / widthOf(video));
    const w = Math.round(widthOf(video) * scale), h = Math.round(heightOf(video) * scale);
    const big = new OffscreenCanvas(w, h);
    big.getContext("2d").drawImage(video, 0, 0, w, h);
    const blob = await big.convertToBlob({ type: "image/jpeg", quality: 0.82 });
    const jpeg = await new Promise((res) => { const r = new FileReader(); r.onload = () => res(String(r.result).split(",")[1] || ""); r.onerror = () => res(""); r.readAsDataURL(blob); });
    if (!jpeg) return;
    log("shot", who, `${w}x${h}`);
    try { chrome.runtime.sendMessage({ type: "huginn-shot", app: site.app, meeting: site.meeting(), t: Date.now(), who, jpeg }); }
    catch { /* the extension was reloaded; this page keeps the old script until a reload */ }
  }

  // What a site file works with.
  globalThis.HUGINN = {
    log, note, push, me, onScreen, pageText,
    queue: (event) => queue.push(event),
    inCall: () => inCall,
    selfName: () => selfName,
    start(theSite) {
      if (site) return;
      site = theSite;
      setInterval(everySecond, 1000);
      setInterval(scanTalk, TALK_EVERY_MS);
      setInterval(watchShare, SHOT_EVERY_MS);
    },
  };
})();
