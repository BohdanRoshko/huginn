// Zoom in the browser (the web client under /wc/), for content.js: how the page shows a meeting, who is talking and
// a shared screen.
// Zoom's markup is not a public API. The names below come from other recorders' public code and notes of 2026, and
// are not yet seen on a live meeting here. Zoom is the weakest of the sites this extension reads:
//   - It has no talking mark on each person. It marks one person at a time as the active speaker (the lit tile in
//     gallery view, the large tile in speaker view), a second or two late. With the Participants panel open, each
//     row does show who is talking, and that is read first.
//   - It draws the cameras and a shared screen on canvases, not in <video>. The shared screen's canvas is copied
//     into a small unseen <video> of this script's own, and the snapshots are taken from that.
// The manifest runs this in every frame: the meeting is sometimes inside an iframe (#webclient).
(() => {
  "use strict";
  const H = globalThis.HUGINN;
  const LEAVE = '#wc-footer button[aria-label="Leave" i], #wc-footer button[aria-label="End" i], button[aria-label="Leave" i]';
  const NAME_IN_TILE = '.video-avatar__avatar-footer span[role="none"], .video-avatar__avatar-footer span, .video-avatar__avatar-footer';
  // The active speaker's frame, the surest first: lit tiles (gallery view, the strip), then the large tile.
  const LIT = [".gallery-video-container__video-frame--active", ".speaker-bar-container__video-frame--active"];
  const LARGE = [".speaker-active-container__video-frame", ".single-main-container__video-frame", ".single-suspension-container__video-frame"];
  const PANEL_ROW = ".participants-li"; // aria-label "Name (Host, me),computer audio muted,video off"
  const PANEL_TALKING = ".participants-icon__voip-speaking-icon";
  const SHARE_TIP = ".sharee-sharing-indicator__tip"; // "You are viewing Name's screen"
  const SHARE_BOX = "video-player-container.sharee-container__canvas, #sharee-container video-player-container";

  const clean = (name) => (name || "").replace(/\s*\([^)]*\)\s*$/, "").trim(); // "Alex Moore (Host)" is Alex Moore
  const nameIn = (frame) => clean(frame.querySelector(NAME_IN_TILE)?.textContent || "");

  // Who is talking now, and by which sign: { names, by }; names is null when the page shows no sign at all.
  function talkingNow() {
    const rows = [...document.querySelectorAll(PANEL_ROW)].filter(H.onScreen);
    if (rows.length > 0)
      return { by: "the Participants panel", names: rows.filter((r) => r.querySelector(PANEL_TALKING)).map((r) => clean((r.getAttribute("aria-label") || "").split(",")[0])) };
    for (const sel of [...LIT, ...LARGE]) {
      const frames = [...document.querySelectorAll(sel)].filter(H.onScreen);
      if (frames.length > 0) return { by: sel, names: frames.map(nameIn).filter(Boolean) };
    }
    return { by: "", names: null };
  }

  function everyone() {
    const names = new Set();
    for (const r of document.querySelectorAll(PANEL_ROW)) {
      const label = (r.getAttribute("aria-label") || "").split(",")[0];
      if (!/\(.*\bme\b.*\)/i.test(label) && clean(label)) names.add(clean(label));
    }
    for (const el of document.querySelectorAll(".video-avatar__avatar")) { const n = nameIn(el); if (n) names.add(n); }
    return names;
  }

  // ---- the shared screen: a canvas in the open shadow root of its container, copied into a <video> of ours ----
  let copy = null; // { canvas, video }
  function shareCanvas() {
    for (const box of document.querySelectorAll(SHARE_BOX)) {
      const canvas = box.shadowRoot?.querySelector("canvas") || box.querySelector("canvas");
      if (canvas && canvas.width >= 320 && H.onScreen(box)) return canvas;
    }
    return null;
  }

  function shareVideo() {
    const canvas = shareCanvas();
    if (copy && copy.canvas !== canvas) {
      for (const t of copy.video.srcObject?.getTracks?.() || []) t.stop();
      copy.video.remove();
      copy = null;
    }
    if (!canvas) return null;
    if (!copy) {
      const video = Object.assign(document.createElement("video"), { muted: true, playsInline: true, autoplay: true });
      video.style.cssText = "position:fixed;left:0;top:0;width:2px;height:2px;opacity:0;pointer-events:none;z-index:-1";
      video.srcObject = canvas.captureStream(2);
      document.documentElement.appendChild(video);
      video.play()?.catch?.(() => {});
      copy = { canvas, video };
    }
    return copy.video;
  }

  const presenterName = () => clean(((document.querySelector(SHARE_TIP)?.textContent || "").match(/You are viewing\s+(.+?)['’]s screen/i) || [])[1] || "");

  H.start({
    app: "Zoom",
    meeting: () => (location.pathname.match(/\/wc\/(?:join\/)?(\d{9,11})/) || [])[1] || "",
    inCall: () => !!document.querySelector(LEAVE),
    selfName: () => clean(([...document.querySelectorAll(PANEL_ROW)].map((r) => (r.getAttribute("aria-label") || "").split(",")[0]).find((l) => /\(.*\bme\b.*\)/i.test(l)) || "").replace(/\s*\([^)]*\)\s*$/, "")),
    talkMark: `the active speaker's frame (${LIT[0]}, ${LARGE[0]}, …) or ${PANEL_TALKING}`,

    people() {
      const { by, names } = talkingNow();
      const talking = new Set(names || []);
      const all = everyone();
      for (const n of talking) all.add(n);
      const mine = H.selfName();
      return [...all].filter((n) => n && n !== mine).map((name) => ({ name, talking: names == null ? null : talking.has(name), state: names == null ? null : `by ${by}` }));
    },

    // Only the copy of the shared screen is ever a screen here; any other <video> on the page is left alone.
    aboutVideo: (el) => (copy && el === copy.video ? { name: presenterName(), self: false, named: true } : { name: "", self: true, named: false }),
    canvases() { shareVideo(); return []; }, // keeps the copy in step with the share; the copy is a <video> on the page
    pageSaysPresenting: () => !!document.querySelector(SHARE_TIP) || !!shareCanvas(),
    presenter: (name) => name || presenterName(),
  });
})();
