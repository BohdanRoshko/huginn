// Passes the call's events from the Meet page to Huginn on this computer. Huginn listens on 127.0.0.1 only and
// accepts a batch only with the X-Huginn header, which a web page cannot send it. When Huginn is not running the
// batch is dropped: Huginn keeps events only while it records anyway.
const HUGINN = "http://127.0.0.1:47821/meet/events";

chrome.runtime.onMessage.addListener((msg) => {
  if (!msg || msg.type !== "huginn-events" || !Array.isArray(msg.events) || msg.events.length === 0) return;
  fetch(HUGINN, {
    method: "POST",
    headers: { "Content-Type": "application/json", "X-Huginn": "1" },
    body: JSON.stringify({ meeting: msg.meeting || "", events: msg.events }),
  }).catch(() => { /* Huginn is not running */ });
});
