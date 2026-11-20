// Huginn for Google Meet: watches a Meet call for who is talking, raised hands, reactions, chat and shared screens,
// and hands each one to the background worker, which passes it to Huginn. It reads the page only; it sends nothing
// to Google.
// Meet's markup is not a public API, so every read below says what it relies on. When one stops matching, the
// others still work, and the console shows "[huginn]" lines to tune it from.
(() => {
  "use strict";
  const ICONS = ".google-symbols, .material-icons-extended, .material-symbols-outlined";
  const SELF_NAME = ".awLEm"; // the signed-in user's name in the call (as of 2026-11)
  // The sound mark on a person's tile: the three small bars. It carries TALK_QUIET while the person is silent and
  // loses it while Meet hears them (as of 2026-11; both are Meet's own generated names, so the first thing to check
  // when names stop coming is the "[huginn] talk mark" lines in the console and Huginn's log).
  const TALK_MARK = '[jsname="QgSmzd"]';
  const TALK_QUIET = "gjg47c";
  const TALK_EVERY_MS = 200;
  const CHAT_REGION = 'div[aria-live="polite"].Ge9Kpc'; // the chat message list (as of 2026-11)
  const queue = [];
  let inCall = false;
  let selfName = "";
  let chatWatch = null, chatNode = null, chatOpens = 0;
  const chatSeen = new Set(); // data-message-id of every message already passed on
  const hands = new Map(); // name -> raised
  const recent = new Map(); // dedupe key -> time

  const log = (...a) => console.log("[huginn]", ...a);
  const meeting = () => (location.pathname.match(/^\/([a-z]+-[a-z]+-[a-z]+)/) || [])[1] || location.pathname.slice(1);
  const icons = (name) => [...document.querySelectorAll(ICONS)].filter((e) => e.textContent.trim() === name);
  const me = (name) => (!name || /^you$/i.test(name.trim()) ? selfName || "Me" : name.trim());

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

  function flush() {
    if (queue.length === 0) return;
    const events = queue.splice(0);
    try { chrome.runtime.sendMessage({ type: "huginn-events", meeting: meeting(), events }); }
    catch { /* the extension was reloaded; this page keeps the old script until a reload */ }
  }

  // The signed-in Google account's name, from the account button ("Google Account: Name (email)").
  function accountName() {
    const label = document.querySelector('[aria-label^="Google Account:"]')?.getAttribute("aria-label") || "";
    return (label.match(/^Google Account:\s*(.+?)\s*(?:\(|$)/) || [])[1] || "";
  }

  // ---- in a call: the red hang-up button is on screen ----
  setInterval(() => {
    const now = icons("call_end").length > 0;
    if (now !== inCall) {
      inCall = now;
      log(inCall ? "joined" : "left", meeting());
      if (!inCall) { hands.clear(); stopChat(); chatOpens = 0; }
      talkers.clear(); markStates.clear(); markSeenAt = 0; markMissingSince = 0; markMissingSaid = false;
      if (!inCall) endShare(Date.now());
      shareSaid.clear(); maybe = null; shareMissSince = 0; shareMissSaid = false; shotFailSaid = false;
    }
    if (inCall) {
      selfName = selfName || document.querySelector(SELF_NAME)?.textContent?.trim() || selfTileName() || accountName();
      scanHands();
      watchChat();
    }
    for (const [k, t] of recent) if (Date.now() - t > 60000) recent.delete(k);
    flush();
  }, 1000);

  // ---- who is talking: the sound mark on each person's tile, read five times a second. Huginn puts the name of
  //      whoever talked on each line of the system audio. My own tile is skipped: my voice is the microphone track.
  //      Only tiles on screen are known; a person Meet does not show has no mark to read. ----
  const talkers = new Set(); // names already seen talking in this call, for one console line each
  const markStates = new Set(); // class lists seen on the mark, passed on once each so a change by Google can be read from the log
  let markSeenAt = 0, markMissingSince = 0, markMissingSaid = false;

  const onScreen = (el) => el.getClientRects().length > 0;

  function scanTalk() {
    if (!inCall) return;
    const now = Date.now();
    const talking = new Set();
    let others = 0, marks = 0;
    for (const tile of document.querySelectorAll("[data-participant-id]")) {
      if (isSelfTile(tile) || !onScreen(tile)) continue;
      const name = tileName(tile);
      if (!name || /^you$/i.test(name) || (selfName && name === selfName)) continue;
      if (NAME_SAYS_SCREEN.test(name)) continue; // a shared screen's own tile: its sound is a video playing, not a person
      others++;
      const mark = tile.querySelector(TALK_MARK);
      if (!mark || !onScreen(mark)) continue; // muted: Meet shows a crossed microphone in place of the bars
      marks++;
      const state = [...mark.classList].sort().join(" ");
      if (!markStates.has(state) && markStates.size < 12) {
        markStates.add(state);
        note(`talk mark classes: "${state}"${mark.classList.contains(TALK_QUIET) ? " (quiet)" : " (talking)"}`);
      }
      if (!mark.classList.contains(TALK_QUIET)) talking.add(name);
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
        note(`talk mark not found on ${others} tile(s) for a minute: either everyone is muted or Meet changed ${TALK_MARK}`);
      }
    }
  }
  setInterval(scanTalk, TALK_EVERY_MS);

  // A remark about the page itself, for Huginn's log.
  function note(text) {
    queue.push({ t: Date.now(), kind: "note", who: "", text });
    log("note", text);
  }

  // ---- shared screens: who starts and stops, and a snapshot each time the shared screen settles on something new.
  //      A shared screen is a video like a camera, so four signs tell it apart, the surest first:
  //        1. its tile's name says "presentation";
  //        2. the picture is wider than any camera Meet sends (over 1280);
  //        3. the page says someone is presenting, and the picture is not camera-shaped (16:9 or 4:3);
  //        4. the page says someone is presenting, and this is clearly the largest video that is not my own camera.
  //      Signs 3 and 4 can also fit a camera, so they count only once the picture has held dead still for two looks
  //      in a row: a screen does that, a person on camera does not. Not yet seen on a live call (2026-11): the
  //      "screen share" lines in Huginn's log say which sign matched, or which videos were on the page when none
  //      did. The snapshot is as sharp as Meet shows the screen here. ----
  const SHOT_EVERY_MS = 3000;
  const SHOT_MAX_WIDTH = 1600;
  const NAME_SAYS_SCREEN = /\s*\((?:presentation|presenting)\)\s*$|['’]s (?:presentation|screen)\s*$|\s+[-–—·]\s+presentation\s*$/i;
  const PAGE_SAYS_PRESENTING = /\(presentation\)|['’]s presentation|\bis presenting\b|\byou(?:['’]re| are) presenting\b|\bstop presenting\b/i;
  const PRESENTING_LABEL = '[aria-label*="is presenting" i], [aria-label*="stop presenting" i], [aria-label*="(presentation)" i]';
  let share = null; // the screen being shared now: { who, unsure, lastGrid, keptGrid, seenAt }
  let maybe = null; // a video found by sign 3 or 4, until it has held still: { video, grid, still }
  const shareSaid = new Set(); // remarks already passed on in this call
  let shareMissSince = 0, shareMissSaid = false, shotFailSaid = false, sending = false;

  const cameraShaped = (v) => {
    const r = v.videoWidth / v.videoHeight;
    return [16 / 9, 4 / 3, 9 / 16, 3 / 4].some((c) => Math.abs(r - c) < 0.03);
  };

  function pageSaysPresenting() {
    if (document.querySelector(PRESENTING_LABEL)) return true;
    const text = document.body.innerText ?? document.body.textContent ?? "";
    return PAGE_SAYS_PRESENTING.test(text);
  }

  // The shared screen's video, whose it is, and which sign found it; or the videos that were looked at.
  function findScreen() {
    const seen = [];
    for (const v of document.querySelectorAll("video")) {
      if (!v.videoWidth || v.videoWidth < 320 || !onScreen(v)) continue;
      const tile = v.closest("[data-participant-id]");
      const name = tile ? tileName(tile) : "";
      const box = v.getBoundingClientRect();
      seen.push({ v, name, self: !!tile && isSelfTile(tile), area: box.width * box.height,
        named: NAME_SAYS_SCREEN.test(name) || /\bpresentation\b|\bpresenting\b/i.test(tile?.getAttribute("aria-label") || "") });
    }
    const largest = (list) => list.slice().sort((a, b) => b.area - a.area)[0] || null;
    let hit = largest(seen.filter((s) => s.named)), sign = "the tile's name", unsure = false;
    if (!hit) { hit = largest(seen.filter((s) => !s.self && s.v.videoWidth > 1280)); sign = "a picture wider than a camera's"; }
    const says = hit ? true : pageSaysPresenting();
    if (!hit && says) {
      unsure = true;
      hit = largest(seen.filter((s) => !s.self && !cameraShaped(s.v)));
      sign = "the page says presenting, the picture is not camera-shaped and it holds still";
    }
    if (!hit && says) {
      const others = seen.filter((s) => !s.self).sort((a, b) => b.area - a.area);
      if (others.length > 0 && others[0].v.videoWidth >= 640 && (others.length === 1 || others[0].area >= 1.5 * others[1].area)) hit = others[0];
      sign = "the page says presenting, this is the largest video and it holds still";
    }
    return { hit, sign, unsure, says, seen };
  }

  // The picture as a small grid of grey levels, to compare two looks; null while the video has no picture yet.
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

  // Whose screen: the tile's name without "(Presentation)"; else the name in "… is presenting"; else nobody's.
  function presenter(tileNameText) {
    const plain = tileNameText.replace(NAME_SAYS_SCREEN, "").trim();
    if (plain) return me(plain);
    const text = document.body.innerText ?? "";
    if (/\byou(?:['’]re| are) presenting\b/i.test(text)) return selfName || "Me";
    const m = text.match(/^\s*(.{1,60}?) is presenting\b/m);
    return m ? m[1].trim() : "";
  }

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
    let held = null;
    if (hit && grid && unsure && !share) {
      const still = maybe && maybe.video === hit.v && deadStill(grid, maybe.grid);
      held = still ? maybe.grid : null;
      maybe = { video: hit.v, grid, still: still ? maybe.still + 1 : 0 };
    }
    const reading = !!hit && !!grid && (!!share || !unsure || maybe.still >= 2);

    if (!reading) {
      if (!hit) maybe = null;
      else if (share) share.seenAt = now; // still there, with no picture for a moment
      if (share && now - share.seenAt > 2 * SHOT_EVERY_MS) endShare(now); // Meet rebuilds the tile when the layout changes
      if (!says || share) { shareMissSince = 0; return; }
      shareMissSince = shareMissSince || now;
      if (!shareMissSaid && now - shareMissSince > 30000) {
        shareMissSaid = true;
        const videos = seen.map((s) => `${s.v.videoWidth}x${s.v.videoHeight} "${s.name}"`).join(", ") || "none";
        const why = hit && grid ? `the likeliest one, ${hit.v.videoWidth}x${hit.v.videoHeight} "${hit.name}", keeps moving` : "no video looks like a shared screen";
        note(`screen share: the page says someone is presenting, but ${why} (videos: ${videos})`);
      }
      return;
    }
    shareMissSince = 0;
    const who = presenter(hit.name) || (share ? share.who : ""); // a name once read stays while the share goes on
    if (share && share.who !== who) endShare(now);
    if (!share) {
      share = { who, unsure, lastGrid: held, keptGrid: null, seenAt: now };
      maybe = null;
      queue.push({ t: now, kind: "share-start", who, text: "" });
      log("share-start", who);
    }
    share.seenAt = now;
    const said = `screen share: reading a ${hit.v.videoWidth}x${hit.v.videoHeight} video on the tile "${hit.name}" (sign: ${sign})`;
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
  setInterval(watchShare, SHOT_EVERY_MS);

  // The picture as a JPEG, at most SHOT_MAX_WIDTH across, handed to the background worker for Huginn.
  async function send(who, video) {
    const scale = Math.min(1, SHOT_MAX_WIDTH / video.videoWidth);
    const w = Math.round(video.videoWidth * scale), h = Math.round(video.videoHeight * scale);
    const big = new OffscreenCanvas(w, h);
    big.getContext("2d").drawImage(video, 0, 0, w, h);
    const blob = await big.convertToBlob({ type: "image/jpeg", quality: 0.82 });
    const jpeg = await new Promise((res) => { const r = new FileReader(); r.onload = () => res(String(r.result).split(",")[1] || ""); r.onerror = () => res(""); r.readAsDataURL(blob); });
    if (!jpeg) return;
    log("shot", who, `${w}x${h}`);
    try { chrome.runtime.sendMessage({ type: "huginn-shot", meeting: meeting(), t: Date.now(), who, jpeg }); }
    catch { /* the extension was reloaded; this page keeps the old script until a reload */ }
  }

  // ---- raised hands: mine from the hand button ("Lower hand" while it is up), others' from the "front_hand" icon on
  //      their tile (both seen in a call, 2026-11-20), plus Meet's spoken announcements ----
  function setHand(name, up) {
    name = me(name);
    if (!name || (hands.get(name) || false) === up) return;
    hands.set(name, up);
    push(up ? "hand-up" : "hand-down", name, "");
  }

  // My own tile is the one with the camera-effects buttons on it (seen 2026-11-20); its name is my name.
  const isSelfTile = (tile) => !!tile.querySelector('[aria-label*="visual effects" i], [aria-label*="backgrounds and effects" i], [aria-label*="framing" i]');
  function selfTileName() {
    const tile = [...document.querySelectorAll("[data-participant-id]")].find(isSelfTile);
    return tile ? tileName(tile) : "";
  }

  // A tile's person: its first "notranslate" text that is not an icon (icons are notranslate too).
  function tileName(tile) {
    const own = tile.querySelector("[data-self-name]")?.getAttribute("data-self-name");
    if (own) return own.trim();
    for (const el of tile.querySelectorAll(".notranslate")) {
      if (el.matches(`i, ${ICONS}, [class*="material"]`) || el.querySelector(ICONS)) continue;
      const text = (el.textContent || "").trim().split("\n")[0];
      if (text && text.length <= 60 && !/^[a-z_]+$/.test(text)) return text;
    }
    return "";
  }

  function scanHands() {
    const mine = document.querySelector('[aria-label^="Lower hand"]') ? true
      : document.querySelector('[aria-label^="Raise hand"]') ? false : null;
    if (mine !== null) setHand(selfName || "Me", mine);
    const up = new Set();
    const seen = new Set();
    for (const tile of document.querySelectorAll("[data-participant-id]")) {
      if (isSelfTile(tile)) continue; // my own tile: the hand button above says it better
      const name = me(tileName(tile));
      if (!name || name === me("")) continue;
      seen.add(name);
      if ([...tile.querySelectorAll(ICONS)].some((e) => e.textContent.trim() === "front_hand")) up.add(name);
    }
    for (const name of up) setHand(name, true);
    // Only tiles on screen are known, so a hand goes down when its tile shows no hand, not when the tile is gone.
    for (const name of seen) if (hands.get(name) && !up.has(name)) setHand(name, false);
  }

  function onAnnouncement(text) {
    let m;
    if ((m = text.match(/^(.+?) (?:has )?raised (?:a|their|his|her|your) hand/i))) setHand(m[1], true);
    else if ((m = text.match(/^(.+?) (?:has )?lowered (?:a|their|his|her|your) hand/i))) setHand(m[1], false);
    else if ((m = text.match(/^(.+?) reacted with (.+)$/i))) push("react", m[1], m[2].trim(), 4000);
  }

  // ---- reactions: an emoji image that floats up with the sender's name ----
  function emojiOf(img) {
    const alt = (img.getAttribute("alt") || "").trim();
    if (alt && /\p{Extended_Pictographic}/u.test(alt)) return alt;
    const m = (img.getAttribute("src") || "").match(/\/(?:u|emoji_u)?([0-9a-f]{4,6}(?:[_-][0-9a-f]{4,6})*)(?:\/|\.|$)/i);
    if (!m) return "";
    try { return String.fromCodePoint(...m[1].split(/[_-]/).map((h) => parseInt(h, 16))); } catch { return ""; }
  }

  function reactionFrom(img) {
    if (img.closest("button, [role='button'], [role='menu'], [role='menuitem'], [role='dialog']")) return; // the picker
    const emoji = emojiOf(img);
    if (!emoji) return;
    let who = "";
    for (let el = img.parentElement, i = 0; el && i < 4 && !who; el = el.parentElement, i++) {
      const t = (el.innerText || "").trim();
      if (t && t.length <= 60 && !/\p{Extended_Pictographic}/u.test(t)) who = t.split("\n")[0];
    }
    if (who) push("react", who, emoji, 4000);
  }

  // ---- chat: the message list. Meet adds it to the page the first time the chat panel opens and keeps it after the
  //      panel closes, so the panel is opened (and closed again) once, unless the user already opened it ----
  function watchChat() {
    const region = document.querySelector(CHAT_REGION);
    if (!region) {
      if (chatOpens >= 3) return;
      const button = [...document.querySelectorAll("button")].find((b) => /^chat with everyone|^chat\b/i.test(b.getAttribute("aria-label") || ""))
        || (icons("chat")[0] || icons("chat_bubble")[0])?.closest("button");
      if (!button) return;
      chatOpens++;
      button.click(); // open the chat panel…
      setTimeout(() => { if (document.querySelector(CHAT_REGION)) button.click(); }, 500); // …and close it again
      return;
    }
    if (region !== chatNode) {
      chatWatch?.disconnect();
      chatNode = region;
      chatWatch = new MutationObserver(readChat);
      chatWatch.observe(region, { childList: true, subtree: true, characterData: true });
      log("chat: watching");
    }
    readChat();
  }

  function stopChat() { chatWatch?.disconnect(); chatWatch = null; chatNode = null; }

  // Each message has its own data-message-id, so it is passed on once. Its text is in jsname="dTKtvb". The group
  // header above holds the sender's name and the time; my own messages have only the time (seen 2026-11-20).
  function readChat() {
    const region = chatNode;
    if (!region) return;
    for (const m of region.querySelectorAll("div[data-message-id]")) {
      const id = m.getAttribute("data-message-id");
      if (chatSeen.has(id)) continue;
      const text = (m.querySelector('[jsname="dTKtvb"]')?.textContent || "").trim();
      if (!text) continue; // still being drawn; the next change reads it
      chatSeen.add(id);
      // A message I send gets a second id once the server has it, so the same words twice in 15 s count once.
      push("chat", chatSender(m), text, 15000);
    }
  }

  function chatSender(m) {
    const head = m.parentElement?.previousElementSibling;
    if (!head) return selfName || "Me";
    const time = head.querySelector('[jsname="biJjHb"]')?.textContent || "";
    const name = (head.textContent || "").replace(time, "").trim();
    return name || selfName || "Me";
  }

  // ---- one observer for the whole page: announcements and floating reactions ----
  new MutationObserver((mutations) => {
    if (!inCall) return;
    for (const mu of mutations) {
      const target = mu.target.nodeType === 1 ? mu.target : mu.target.parentElement;
      const live = target?.closest?.("[aria-live]");
      if (live && !live.matches(CHAT_REGION) && !live.closest(CHAT_REGION)) {
        const text = (live.textContent || "").trim();
        if (text) onAnnouncement(text);
      }
      for (const node of mu.addedNodes) {
        if (node.nodeType !== 1) continue;
        if (node.tagName === "IMG") reactionFrom(node);
        else if (node.querySelector) for (const img of node.querySelectorAll("img[src*='emoji']")) reactionFrom(img);
      }
    }
  }).observe(document.body, { childList: true, subtree: true, characterData: true });
})();
