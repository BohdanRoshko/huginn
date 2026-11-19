// Huginn for Google Meet: watches a Meet call for raised hands, reactions and chat, and hands each one to the
// background worker, which passes it to Huginn. It reads the page only; it sends nothing to Google.
// Meet's markup is not a public API, so every read below says what it relies on. When one stops matching, the
// others still work, and the console shows "[huginn]" lines to tune it from.
(() => {
  "use strict";
  const ICONS = ".google-symbols, .material-icons-extended, .material-symbols-outlined";
  const SELF_NAME = ".awLEm"; // the signed-in user's name in the call (as of 2026-11)
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
    }
    if (inCall) {
      selfName = selfName || document.querySelector(SELF_NAME)?.textContent?.trim() || selfTileName() || accountName();
      scanHands();
      watchChat();
    }
    for (const [k, t] of recent) if (Date.now() - t > 60000) recent.delete(k);
    flush();
  }, 1000);

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
