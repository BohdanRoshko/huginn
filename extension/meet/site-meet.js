// Google Meet, for content.js: how a Meet page shows a call, the people in it, who is talking, a shared screen,
// raised hands, reactions and chat.
// Meet's markup is not a public API, so every read below says what it relies on. When one stops matching, the
// others still work, and the console shows "[huginn]" lines to tune it from.
(() => {
  "use strict";
  const H = globalThis.HUGINN;
  const ICONS = ".google-symbols, .material-icons-extended, .material-symbols-outlined";
  const SELF_NAME = ".awLEm"; // the signed-in user's name in the call (as of 2026-11)
  // The sound mark on a person's tile: the three small bars. It carries TALK_QUIET while the person is silent and
  // loses it while Meet hears them (as of 2026-11; both are Meet's own generated names, so the first thing to check
  // when names stop coming is the "[huginn] talk mark" lines in the console and Huginn's log).
  const TALK_MARK = '[jsname="QgSmzd"]';
  const TALK_QUIET = "gjg47c";
  const CHAT_REGION = 'div[aria-live="polite"].Ge9Kpc'; // the chat message list (as of 2026-11)
  // A shared screen has a tile of its own, named after the person (not yet seen on a live call, 2026-11).
  const NAME_SAYS_SCREEN = /\s*\((?:presentation|presenting)\)\s*$|['’]s (?:presentation|screen)\s*$|\s+[-–—·]\s+presentation\s*$/i;
  const PAGE_SAYS_PRESENTING = /\(presentation\)|['’]s presentation|\bis presenting\b|\byou(?:['’]re| are) presenting\b|\bstop presenting\b/i;
  const PRESENTING_LABEL = '[aria-label*="is presenting" i], [aria-label*="stop presenting" i], [aria-label*="(presentation)" i]';
  let chatWatch = null, chatNode = null, chatOpens = 0;
  const chatSeen = new Set(); // data-message-id of every message already passed on
  const hands = new Map(); // name -> raised

  const icons = (name) => [...document.querySelectorAll(ICONS)].filter((e) => e.textContent.trim() === name);

  // The signed-in Google account's name, from the account button ("Google Account: Name (email)").
  function accountName() {
    const label = document.querySelector('[aria-label^="Google Account:"]')?.getAttribute("aria-label") || "";
    return (label.match(/^Google Account:\s*(.+?)\s*(?:\(|$)/) || [])[1] || "";
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

  // ---- raised hands: mine from the hand button ("Lower hand" while it is up), others' from the "front_hand" icon on
  //      their tile (both seen in a call, 2026-11-20), plus Meet's spoken announcements ----
  function setHand(name, up) {
    name = H.me(name);
    if (!name || (hands.get(name) || false) === up) return;
    hands.set(name, up);
    H.push(up ? "hand-up" : "hand-down", name, "");
  }

  function scanHands() {
    const mine = document.querySelector('[aria-label^="Lower hand"]') ? true
      : document.querySelector('[aria-label^="Raise hand"]') ? false : null;
    if (mine !== null) setHand(H.selfName() || "Me", mine);
    const up = new Set();
    const seen = new Set();
    for (const tile of document.querySelectorAll("[data-participant-id]")) {
      if (isSelfTile(tile)) continue; // my own tile: the hand button above says it better
      const name = H.me(tileName(tile));
      if (!name || name === H.me("")) continue;
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
    else if ((m = text.match(/^(.+?) reacted with (.+)$/i))) H.push("react", m[1], m[2].trim(), 4000);
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
    if (who) H.push("react", who, emoji, 4000);
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
      H.log("chat: watching");
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
      H.push("chat", chatSender(m), text, 15000);
    }
  }

  function chatSender(m) {
    const head = m.parentElement?.previousElementSibling;
    if (!head) return H.selfName() || "Me";
    const time = head.querySelector('[jsname="biJjHb"]')?.textContent || "";
    const name = (head.textContent || "").replace(time, "").trim();
    return name || H.selfName() || "Me";
  }

  // ---- one observer for the whole page: announcements and floating reactions ----
  new MutationObserver((mutations) => {
    if (!H.inCall()) return;
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

  H.start({
    app: "Google Meet",
    meeting: () => (location.pathname.match(/^\/([a-z]+-[a-z]+-[a-z]+)/) || [])[1] || location.pathname.slice(1),
    inCall: () => icons("call_end").length > 0, // the red hang-up button is on screen
    selfName: () => document.querySelector(SELF_NAME)?.textContent?.trim() || selfTileName() || accountName(),
    talkMark: TALK_MARK,

    people() {
      const out = [];
      for (const tile of document.querySelectorAll("[data-participant-id]")) {
        if (isSelfTile(tile) || !H.onScreen(tile)) continue;
        const name = tileName(tile);
        if (!name || /^you$/i.test(name) || (H.selfName() && name === H.selfName())) continue;
        if (NAME_SAYS_SCREEN.test(name)) continue; // a shared screen's own tile: its sound is a video playing, not a person
        const mark = tile.querySelector(TALK_MARK);
        const shown = !!mark && H.onScreen(mark); // muted: Meet shows a crossed microphone in place of the bars
        out.push({ name, talking: shown ? !mark.classList.contains(TALK_QUIET) : null, state: shown ? [...mark.classList].sort().join(" ") : null });
      }
      return out;
    },

    aboutVideo(video) {
      const tile = video.closest("[data-participant-id]");
      const name = tile ? tileName(tile) : "";
      return { name, self: !!tile && isSelfTile(tile),
        named: NAME_SAYS_SCREEN.test(name) || /\bpresentation\b|\bpresenting\b/i.test(tile?.getAttribute("aria-label") || "") };
    },

    pageSaysPresenting: () => !!document.querySelector(PRESENTING_LABEL) || PAGE_SAYS_PRESENTING.test(H.pageText()),

    // Whose screen: the tile's name without "(Presentation)"; else the name in "… is presenting"; else nobody's.
    presenter(name) {
      const plain = name.replace(NAME_SAYS_SCREEN, "").trim();
      if (plain) return H.me(plain);
      const text = H.pageText();
      if (/\byou(?:['’]re| are) presenting\b/i.test(text)) return H.selfName() || "Me";
      const m = text.match(/^\s*(.{1,60}?) is presenting\b/m);
      return m ? m[1].trim() : "";
    },

    everySecond() { scanHands(); watchChat(); },
    onCallChange(inCall) { if (!inCall) { hands.clear(); stopChat(); chatOpens = 0; } },
  });
})();
