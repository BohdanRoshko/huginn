// Passes the call's events and its snapshots of a shared screen from the Meet page to Huginn on this computer.
// Huginn listens on 127.0.0.1 only and accepts a request only with the X-Huginn header, which a web page cannot
// send it. When Huginn is not running the request is dropped: Huginn keeps these only while it records anyway.
const HUGINN = "http://127.0.0.1:47821/meet/";

function post(path, body) {
  fetch(HUGINN + path, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Huginn": "1" },
    body: JSON.stringify(body),
  }).catch(() => { /* Huginn is not running */ });
}

chrome.runtime.onMessage.addListener((msg) => {
  if (!msg) return;
  if (msg.type === "huginn-events" && Array.isArray(msg.events) && msg.events.length > 0)
    post("events", { meeting: msg.meeting || "", events: msg.events });
  else if (msg.type === "huginn-shot" && typeof msg.jpeg === "string" && msg.jpeg.length > 0)
    post("shot", { meeting: msg.meeting || "", t: msg.t, who: msg.who || "", jpeg: msg.jpeg });
});
