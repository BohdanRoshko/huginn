// Microsoft Teams in the browser, for content.js: how a Teams call page shows a call, the people in it, who is
// talking and a shared screen.
// Teams' markup is not a public API. The names below come from other recorders' public code and notes of
// 2026-10 and 2026-11, and are not yet seen on a live call here. Huginn's log shows what was read.
(() => {
  "use strict";
  const H = globalThis.HUGINN;
  // The hang-up button is on the page for the whole call, also for a guest, whose address never changes.
  const HANG_UP = '#hangup-button, button[data-tid="hangup-main-btn"], button[data-tid="hangup-leave-button"], button[data-tid="hangup-button"], button[data-tid="anon-hangup-button"]';
  // One element per stream on the stage. data-tid is the person's name as Teams shows it; data-stream-type says
  // camera or shared screen.
  const STREAM = "[data-stream-type][data-tid]";
  const SCREEN = '[data-stream-type="ScreenSharing"]'; // its data-tid is the name of the person sharing
  const MY_TILE = '[data-tid="myself-video"]'; // aria-label "Myself video, Name, …"
  // The ring around a person's tile. The element is on every other person's tile all the time; the ring itself is
  // drawn by its ::before, which is opaque while Teams hears that person. Teams moves it about once a second, so it
  // comes a little before the words and stays a little after. My own tile has no ring.
  const RING = '[data-tid="voice-level-stream-outline"]';

  // "Alex Moore (Unverified)" and "Alex Moore (Guest)" are Alex Moore.
  const plain = (name) => (name || "").replace(/\s*\((?:unverified|guest|external)\)\s*$/i, "").trim();
  const isScreen = (el) => (el.getAttribute("data-stream-type") || "").toLowerCase() === "screensharing";
  const ringShown = (ring) => parseFloat(getComputedStyle(ring, "::before").opacity) > 0.5;

  function myName() {
    const label = document.querySelector(MY_TILE)?.getAttribute("aria-label") || "";
    return plain((label.match(/^Myself video,\s*([^,]+)/i) || [])[1] || "");
  }

  H.start({
    app: "Microsoft Teams",
    meeting: () => ((location.pathname + location.hash).match(/\/meet\/(\d+)/) || [])[1] || document.title.replace(/\s*\|\s*Microsoft Teams.*$/i, "").trim().slice(0, 60),
    inCall: () => [...document.querySelectorAll(HANG_UP)].some(H.onScreen),
    selfName: myName,
    talkMark: `${RING} (its ::before)`,

    people() {
      const out = [];
      const seen = new Set();
      for (const tile of document.querySelectorAll(STREAM)) {
        if (isScreen(tile) || !H.onScreen(tile) || tile.closest(MY_TILE)) continue;
        const name = plain(tile.getAttribute("data-tid"));
        const ring = tile.querySelector(RING);
        if (!name || !ring || seen.has(name)) continue; // no ring: my own tile
        if (H.selfName() && name === H.selfName()) continue;
        seen.add(name);
        const on = ringShown(ring);
        // The class is passed on too: some notes say "vdi-frame-occlusion" marks talking, others that it does not.
        out.push({ name, talking: on, state: `${[...ring.classList].filter((c) => /vdi|speak|active/i.test(c)).sort().join(" ") || "-"} | ring ${on ? "shown" : "hidden"}` });
      }
      return out;
    },

    aboutVideo(el) {
      const screen = el.closest(SCREEN);
      if (screen) return { name: plain(screen.getAttribute("data-tid")), self: false, named: true };
      const tile = el.closest(STREAM);
      const mine = !!el.closest(MY_TILE) || (!!tile && !tile.querySelector(RING));
      return { name: tile ? plain(tile.getAttribute("data-tid")) : "", self: mine, named: false };
    },

    // Not known whether Teams draws a shared screen in a <video> or on a <canvas>; both are looked at.
    canvases: () => [...document.querySelectorAll(`${SCREEN} canvas`)],

    pageSaysPresenting: () => [...document.querySelectorAll(`${SCREEN}, [aria-label="Shared content view"]`)].some(H.onScreen),
    presenter: (name) => plain(name),
  });
})();
