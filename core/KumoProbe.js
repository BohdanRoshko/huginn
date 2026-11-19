// Huginn's read of the Kumospace desktop page, run over the DevTools port once a second (KumoWatcher.cs).
// It reads the app's Redux store and the Daily call object; it sends nothing to Kumospace's servers.
// The only thing it starts is Daily's local audio-level observers, which measure the tracks this client
// already receives. Samples are buffered here between reads and handed over (drained) on each read.
(() => {
  const M = window.__huginn || (window.__huginn = { v: 1, buf: [], lbuf: [], call: null, store: null });
  const findStore = () => {
    const root = document.getElementById("root") || document.body.firstElementChild;
    const key = root && Object.keys(root).find((k) => k.startsWith("__reactContainer$"));
    if (!key) return null;
    const stack = [root[key]];
    while (stack.length) {
      const n = stack.pop();
      if (!n) continue;
      const p = n.memoizedProps;
      if (p && p.store && typeof p.store.getState === "function") return p.store;
      if (n.child) stack.push(n.child);
      if (n.sibling) stack.push(n.sibling);
    }
    return null;
  };
  if (!M.store || typeof M.store.getState !== "function") M.store = findStore();
  if (!M.store) return { ok: false, why: "no store" };

  const s = M.store.getState();
  const call = s.daily && s.daily.call;
  const self = s.users && s.users.userSelf;
  const out = { ok: true, t: Date.now(), callState: call && call.meetingState ? call.meetingState() : "none" };

  // My status (available / away / focusing) for the work timer; the lobby (/profile) is not the space.
  out.inSpace = !!self && !/^\/profile/.test(location.pathname);
  out.availability = (self && self.userStatus && self.userStatus.availability) || "";

  // Where I am: the room (zone) and the people the space puts in the same room.
  if (self) {
    const zones = (s.space && s.space.zones && (s.space.zones.entities || s.space.zones)) || {};
    const z = self.zoneId ? zones[self.zoneId] : null;
    out.zone = z ? z.displayName || "" : "";
    out.zoneType = z ? z.type || "" : "";
    const census = Object.values((s.users.censusMap && (s.users.censusMap.entities || s.users.censusMap)) || {});
    out.roomMates = self.zoneId
      ? census.filter((c) => c && c.id !== self.id && c.zoneId === self.zoneId && c.roomName === self.roomName).map((c) => ({ id: c.id, name: c.name }))
      : [];
  }

  if (call && out.callState === "joined-meeting") {
    if (M.call !== call) {
      M.call = call;
      M.buf = [];
      M.lbuf = [];
      const round = (x) => Math.round(x * 1000) / 1000;
      call.on("remote-participants-audio-level", (e) => {
        const lv = (e && e.participantsAudioLevel) || {};
        const row = {};
        let any = false;
        for (const k in lv) if (lv[k] > 0.005) { row[k] = round(lv[k]); any = true; }
        if (any) M.buf.push([Date.now(), row]);
        if (M.buf.length > 6000) M.buf.splice(0, M.buf.length - 6000);
      });
      call.on("local-audio-level", (e) => {
        const lv = e && e.audioLevel;
        if (lv > 0.005) M.lbuf.push([Date.now(), round(lv)]);
        if (M.lbuf.length > 6000) M.lbuf.splice(0, M.lbuf.length - 6000);
      });
    }
    try { if (!call.isRemoteParticipantsAudioLevelObserverRunning()) call.startRemoteParticipantsAudioLevelObserver(200).catch(() => {}); } catch (e) { out.observerError = String(e); }
    try { if (!call.isLocalAudioLevelObserverRunning()) call.startLocalAudioLevelObserver(200).catch(() => {}); } catch (e) { out.observerError = String(e); }

    // The people I hear: Kumospace subscribes this client to the audio of everyone near me or in my room.
    const parts = call.participants();
    out.me = parts.local ? { sid: parts.local.session_id, id: parts.local.user_id, name: parts.local.user_name } : null;
    out.heard = Object.values(parts)
      .filter((p) => p && !p.local && p.tracks && p.tracks.audio && p.tracks.audio.subscribed === true)
      .map((p) => ({ sid: p.session_id, id: p.user_id, name: p.user_name }));
    out.names = Object.fromEntries(Object.values(parts).filter((p) => p && !p.local).map((p) => [p.session_id, p.user_name]));
    out.samples = M.buf.splice(0);
    out.local = M.lbuf.splice(0);

    // Who I am with, by census id (Daily's user_id): the people I hear and my room mates. The Daily call holds the
    // whole floor, so what someone far away does is not part of my meeting.
    M.callIds = new Set([...out.heard.map((p) => p.id), ...(out.roomMates || []).map((p) => p.id)]);
    if (self) M.callIds.add(self.id);
    if (out.me) M.callIds.add(out.me.id);
    watchState(s);
    watchShares(parts);
    out.events = M.ev.splice(0);
    out.shots = M.shots.splice(0);
  } else {
    M.call = null;
    M.ev = [];
    M.shots = [];
  }
  return out;

  // ---- reactions, hands and meeting chat: every store change, so a reaction that shows for a moment is not missed ----
  function watchState(state) {
    if (!M.ev) M.ev = [];
    const VERSION = 1; // bump when this watcher changes, so a running page drops the old one
    if (M.subStore === M.store && M.subVersion === VERSION) return;
    if (M.unsub) try { M.unsub(); } catch { /* the old store is gone */ }
    M.subStore = M.store;
    M.subVersion = VERSION;
    M.emoji = {}; M.hand = {}; M.chatSeen = null; M.lastUsers = null; M.lastMsgs = null;
    const onChange = () => {
      try {
        const st = M.store.getState();
        const users = st.users && st.users.users;
        if (users && users !== M.lastUsers) { M.lastUsers = users; scanUsers(users); }
        const msgs = st.chat && st.chat.session && st.chat.session.messages;
        if (msgs && msgs !== M.lastMsgs) { M.lastMsgs = msgs; scanChat(st, msgs); }
      } catch { /* the page is changing; the next change reads again */ }
    };
    M.unsub = M.store.subscribe(onChange);
    onChange();
  }

  function scanUsers(users) {
    const now = Date.now();
    for (const u of users) {
      if (!u || u.type !== "user" || !M.callIds || !M.callIds.has(u.id)) continue;
      const emoji = u.emoji || "";
      const hand = !!u.handRaisedTimestamp;
      if (u.id in M.emoji) {
        if (emoji && emoji !== M.emoji[u.id]) push([now, "react", u.id, u.name, emoji]);
        if (hand !== M.hand[u.id]) push([now, hand ? "hand-up" : "hand-down", u.id, u.name, ""]);
      }
      M.emoji[u.id] = emoji; // the first sight of a person only records the state; it is not an event
      M.hand[u.id] = hand;
    }
  }

  // Nearby and room chat is the meeting's chat; the space-wide public chat and private messages are not.
  function scanChat(st, msgs) {
    const first = M.chatSeen === null;
    if (first) M.chatSeen = new Set();
    const census = (st.users.censusMap && (st.users.censusMap.entities || st.users.censusMap)) || {};
    const selfId = st.users.userSelf && st.users.userSelf.id;
    for (const m of msgs) {
      const id = m && (m.id || `${m.senderId}|${m.timestamp}|${m.message}`);
      if (!id || M.chatSeen.has(id)) continue;
      M.chatSeen.add(id);
      if (first || (m.range !== "nearby" && m.range !== "room") || typeof m.message !== "string") continue;
      const who = m.senderId === selfId ? st.users.userSelf.name : (census[m.senderId] && census[m.senderId].name) || "Someone";
      push([Number(m.timestamp) || Date.now(), "chat", m.senderId || "", who, m.message]);
    }
  }

  function push(e) {
    M.ev.push(e);
    if (M.ev.length > 2000) M.ev.splice(0, M.ev.length - 2000);
  }

  // ---- screen shares: who starts and stops, and a snapshot each time the shared screen settles on something new ----
  function watchShares(parts) {
    if (!M.share) { M.share = {}; M.shots = []; }
    const now = Date.now();
    let active = null;
    for (const p of Object.values(parts)) {
      if (!p || !p.user_id || !M.callIds.has(p.user_id)) continue;
      const sv = p.tracks && p.tracks.screenVideo;
      const on = !!sv && (sv.state !== "off" || !!(sv.off && sv.off.byRemoteRequest));
      const name = p.user_name || "";
      if (on !== !!M.share[p.user_id]) push([now, on ? "share-start" : "share-stop", p.user_id, name, ""]);
      M.share[p.user_id] = on;
      if (on && sv.state === "playable" && sv.persistentTrack) active = { id: p.user_id, name, track: sv.persistentTrack };
    }
    if (!active) { M.shotFor = null; return; }
    if (M.shotFor !== active.id) { M.shotFor = active.id; M.lastGrid = null; M.keptGrid = null; }
    if (M.grabbing || now - (M.lastGrab || 0) < 3000) return;
    M.grabbing = true;
    M.lastGrab = now;
    grab(active).catch(() => {}).finally(() => { M.grabbing = false; });
  }

  async function grab(a) {
    const bmp = await frameOf(a.track);
    if (!bmp || !bmp.width) return;
    const GW = 96, GH = 54;
    const small = new OffscreenCanvas(GW, GH).getContext("2d", { willReadFrequently: true });
    small.drawImage(bmp, 0, 0, GW, GH);
    const px = small.getImageData(0, 0, GW, GH).data;
    const grid = new Uint8Array(GW * GH);
    for (let i = 0; i < grid.length; i++) grid[i] = (px[i * 4] * 3 + px[i * 4 + 1] * 6 + px[i * 4 + 2]) / 10;
    const changed = (a1, b1) => { if (!b1) return 1; let n = 0; for (let i = 0; i < a1.length; i++) if (Math.abs(a1[i] - b1[i]) > 20) n++; return n / a1.length; };
    const settled = changed(grid, M.lastGrid) < 0.005; // the same as 3 s ago: not mid-scroll or mid-animation
    M.lastGrid = grid;
    if (!settled || changed(grid, M.keptGrid) < 0.01) return;
    M.keptGrid = grid;
    const scale = Math.min(1, 1600 / bmp.width);
    const w = Math.round(bmp.width * scale), h = Math.round(bmp.height * scale);
    const big = new OffscreenCanvas(w, h);
    big.getContext("2d").drawImage(bmp, 0, 0, w, h);
    const blob = await big.convertToBlob({ type: "image/jpeg", quality: 0.82 });
    const b64 = await new Promise((res) => { const r = new FileReader(); r.onload = () => res(String(r.result).split(",")[1] || ""); r.readAsDataURL(blob); });
    if (b64) { M.shots.push([Date.now(), a.id, a.name, b64, w, h]); if (M.shots.length > 20) M.shots.splice(0, M.shots.length - 20); }
  }

  // One frame of a video track: ImageCapture when the page has it, else a muted video element.
  async function frameOf(track) {
    if (typeof ImageCapture === "function") {
      try { return await new ImageCapture(track).grabFrame(); } catch { /* fall through */ }
    }
    const v = M.shotVideo || (M.shotVideo = Object.assign(document.createElement("video"), { muted: true, playsInline: true }));
    if (!v.srcObject || v.srcObject.getVideoTracks()[0] !== track) v.srcObject = new MediaStream([track]);
    await v.play();
    if (!v.videoWidth) await new Promise((r) => setTimeout(r, 300));
    return v.videoWidth ? await createImageBitmap(v) : null;
  }
})()
