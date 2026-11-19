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
  let chatWatch = null;
  const hands = new Map(); // name -> raised
  const recent = new Map(); // dedupe key -> time

  const log = (...a) => console.debug("[huginn]", ...a);
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
      if (inCall) startChat(); else { hands.clear(); stopChat(); }
    }
    if (inCall) {
      selfName = selfName || document.querySelector(SELF_NAME)?.textContent?.trim() || accountName();
      scanHands();
    }
    for (const [k, t] of recent) if (Date.now() - t > 60000) recent.delete(k);
    flush();
  }, 1000);

  // ---- raised hands: a "back_hand" icon on a person's tile, plus Meet's spoken announcements ----
  function setHand(name, up) {
    name = me(name);
    if (!name || (hands.get(name) || false) === up) return;
    hands.set(name, up);
    push(up ? "hand-up" : "hand-down", name, "");
  }

  function tileName(tile) {
    const label = tile.querySelector(".notranslate, [data-self-name]");
    const text = label?.getAttribute("data-self-name") || label?.textContent || "";
    return text.trim().split("\n")[0];
  }

  function scanHands() {
    const up = new Set();
    for (const tile of document.querySelectorAll("[data-participant-id]")) {
      const raised = [...tile.querySelectorAll(ICONS)].some((e) => e.textContent.trim() === "back_hand");
      const name = tileName(tile);
      if (name && raised) up.add(me(name));
    }
    for (const name of up) setHand(name, true);
    // Only tiles on screen are known, so a hand goes down when its tile shows no hand, not when the tile is gone.
    for (const tile of document.querySelectorAll("[data-participant-id]")) {
      const name = me(tileName(tile));
      if (name && hands.get(name) && !up.has(name)) setHand(name, false);
    }
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

  // ---- chat: the message list, opened once so Meet puts it in the page (it stays after the panel closes) ----
  function startChat() {
    if (chatWatch) return;
    let tries = 0;
    const timer = setInterval(() => {
      if (!inCall || ++tries > 30) { clearInterval(timer); return; }
      let region = document.querySelector(CHAT_REGION);
      if (!region) {
        const button = icons("chat")[0]?.closest("button");
        if (!button || tries % 5 !== 1) return;
        button.click(); // open the chat panel…
        setTimeout(() => { if (document.querySelector(CHAT_REGION)) button.click(); }, 400); // …and close it again
        return;
      }
      clearInterval(timer);
      lastChat = readLast(region);
      chatWatch = new MutationObserver(() => readChat(region));
      chatWatch.observe(region, { childList: true, subtree: true, characterData: true });
      log("chat: watching");
    }, 1000);
  }

  function stopChat() { chatWatch?.disconnect(); chatWatch = null; }

  // The newest message: its header holds the name and the time (only the time for my own messages). The path is the
  // one the open-source TranscripTonic extension keeps current for Meet.
  let lastChat = "";
  function readLast(region) {
    const msg = region.lastChild?.firstChild?.firstChild?.lastChild;
    const head = msg?.firstChild;
    const who = head?.childNodes.length === 1 ? selfName || "Me" : head?.firstChild?.textContent || "";
    const text = msg?.lastChild?.lastChild?.firstChild?.firstChild?.firstChild?.textContent || "";
    return who && text ? `${who}\u0000${text}` : "";
  }

  // Meet fires many changes per message; a message counts once, when the newest one changes.
  function readChat(region) {
    const last = readLast(region);
    if (!last || last === lastChat) return;
    lastChat = last;
    const [who, text] = last.split("\u0000");
    push("chat", who, text.trim());
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
