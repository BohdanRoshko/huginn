// A Slack huddle in the browser, for content.js: how the huddle shows the people in it, who is talking and a
// shared screen.
// Slack draws a huddle in a window of its own, opened from the Slack tab with an empty address. The manifest runs
// this script there too ("match_about_blank"), and in the Slack tab for a huddle shown inside it.
// Slack's markup is not a public API. The names below come from other recorders' public code and notes of
// 2026-10 and 2026-11, and are not yet seen on a live huddle here. Nothing public says how Slack marks a shared
// screen, so that part goes by the picture's size and by "screenshare" in the names around it.
(() => {
  "use strict";
  const H = globalThis.HUGINN;
  const LEAVE = '[data-qa="huddle_toolbar__leave_button"]';
  const TILE = ".p-huddle_peer_tile"; // one per person; aria-label "View Name's profile"
  const TALKING = ".p-huddle_peer_tile__overlay--active_speaker"; // on the tile only while Slack hears the person
  const SCREEN_AROUND = '[class*="screenshare" i], [data-qa*="screenshare" i]';

  const nameOf = (tile) => ((tile.getAttribute("aria-label") || "").match(/^View (.+?)['’]s profile$/i) || [])[1]?.trim() || "";
  // My own tile's cell is "huddle-grid-gridcell-self_<id>".
  const isMine = (tile) => /-self_/.test(tile.id || "") || !!tile.closest('[id*="gridcell-self_"]');
  const tiles = () => [...document.querySelectorAll(TILE)];

  H.start({
    app: "Slack huddle",
    // The huddle window's title is "Huddle: #channel - workspace - Slack".
    meeting: () => (document.title.match(/^Huddle:\s*(.+?)(?:\s+-\s+.*)?$/) || [])[1]?.slice(0, 60) || "",
    // The Slack tab holds a Leave button of its own while the huddle runs in its window; only the page with the
    // people on it reads the huddle.
    inCall: () => tiles().length > 0 && !!document.querySelector(LEAVE),
    selfName: () => nameOf(tiles().find(isMine) || document.createElement("div")),
    talkMark: TALKING,

    people() {
      const out = [];
      const seen = new Set(); // one person on two devices has two tiles
      for (const tile of tiles()) {
        if (isMine(tile) || !H.onScreen(tile)) continue;
        const name = nameOf(tile);
        if (!name || seen.has(name)) continue;
        seen.add(name);
        const on = tiles().some((t) => nameOf(t) === name && !!t.querySelector(TALKING));
        out.push({ name, talking: on, state: on ? "active_speaker" : "none" });
      }
      return out;
    },

    aboutVideo(el) {
      const tile = el.closest(TILE);
      return { name: tile ? nameOf(tile) : "", self: !!tile && isMine(tile), named: !tile && !!el.closest(SCREEN_AROUND) };
    },

    pageSaysPresenting: () => [...document.querySelectorAll(SCREEN_AROUND)].some(H.onScreen) || /\bis sharing (?:their|a|the) screen\b|\bsharing (?:their|a) screen\b/i.test(H.pageText()),

    // Whose screen: the name in "… is sharing their screen" when the page says it; else unknown.
    presenter(name) {
      if (name) return name;
      const m = H.pageText().match(/^\s*(.{1,60}?) is sharing (?:their|a|the) screen\b/im);
      return m ? m[1].trim() : "";
    },
  });
})();
