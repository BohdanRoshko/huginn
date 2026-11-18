// Munin's read of the Kumospace desktop page, run over the DevTools port once a second (KumoWatcher.cs).
// It reads the app's Redux store and the Daily call object; it sends nothing to Kumospace's servers.
// The only thing it starts is Daily's local audio-level observers, which measure the tracks this client
// already receives. Samples are buffered here between reads and handed over (drained) on each read.
(() => {
  const M = window.__munin || (window.__munin = { v: 1, buf: [], lbuf: [], call: null, store: null });
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
  } else {
    M.call = null;
  }
  return out;
})()
