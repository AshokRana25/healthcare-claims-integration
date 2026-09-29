"use strict";
const $ = id => document.getElementById(id);
let selectedId = null;
let lastSubmission = null;
let refreshBusy = false;
let requestBusy = false;
const money = n => new Intl.NumberFormat("en-US", {style: "currency", currency: "USD"}).format(n);
const time = n => new Date(n).toLocaleTimeString([], {hour: "2-digit", minute: "2-digit", second: "2-digit"});

function newId() { $("external").value = "DEMO-" + crypto.randomUUID().slice(0, 8).toUpperCase(); }
function notice(message, error = false) {
  $("notice").textContent = message;
  $("notice").className = error ? "error" : "";
}
async function api(path, options) {
  const response = await fetch(path, options);
  const data = await response.json().catch(() => ({}));
  if (!response.ok) {
    const errors = data.errors ? Object.values(data.errors).flat().join(" ") : null;
    throw new Error(errors || data.error || data.title || "Request failed. Please retry.");
  }
  return data;
}
async function submit(body, key) {
  if (requestBusy) return;
  requestBusy = true;
  $("submit").disabled = true;
  $("duplicate").disabled = true;
  try {
    // Keep the key even after a network failure so an uncertain submission can be retried safely.
    lastSubmission = {body, key};
    const result = await api("/api/claims", {
      method: "POST", headers: {"Content-Type": "application/json", "Idempotency-Key": key},
      body: JSON.stringify(body)
    });
    selectedId = result.claim.id;
    notice(result.duplicate ? "Duplicate detected. Existing claim returned; no new event created." : "Claim accepted. Processing will begin automatically.");
    await refresh();
  } catch (e) { notice(e.message, true); }
  finally { requestBusy = false; $("submit").disabled = false; $("duplicate").disabled = false; }
}
$("claim-form").onsubmit = e => {
  e.preventDefault();
  const body = {source: "demo-console", externalClaimId: $("external").value.trim(),
    memberId: $("member").value.trim(), serviceDate: $("date").value,
    amount: Number($("amount").value), currency: "USD", scenario: $("scenario").value};
  submit(body, crypto.randomUUID());
};
$("new-claim").onclick = newId;
$("duplicate").onclick = () => lastSubmission && submit(lastSubmission.body, lastSubmission.key);

function renderClaims(claims) {
  $("total").textContent = claims.length;
  $("integrated").textContent = claims.filter(c => c.status === "Integrated").length;
  $("pending").textContent = claims.filter(c => ["Queued", "RetryScheduled"].includes(c.status)).length;
  $("attention").textContent = claims.filter(c => ["DeadLetter", "ManualReview"].includes(c.status)).length;
  $("empty").hidden = claims.length > 0;
  $("claims").replaceChildren();
  for (const claim of claims) {
    const row = document.createElement("tr");
    if (claim.id === selectedId) row.className = "selected";
    const name = document.createElement("td");
    const button = document.createElement("button");
    button.className = "claim-link";
    button.textContent = claim.externalClaimId;
    button.onclick = async () => {
      selectedId = claim.id;
      renderClaims(claims);
      try { await detail(); } catch (e) { notice(e.message, true); }
    };
    const member = document.createElement("small"); member.textContent = claim.memberId;
    name.append(button, member);
    const amount = document.createElement("td"); amount.textContent = money(claim.amount);
    const status = document.createElement("td");
    const badge = document.createElement("span"); badge.className = "badge " + claim.status;
    badge.textContent = claim.status.replace(/([a-z])([A-Z])/g, "$1 $2"); status.append(badge);
    const attempts = document.createElement("td"); attempts.textContent = claim.attempts + " / 3";
    row.append(name, amount, status, attempts); $("claims").append(row);
  }
}
async function detail() {
  if (!selectedId) return;
  const id = selectedId;
  const data = await api("/api/claims/" + encodeURIComponent(id));
  if (id !== selectedId) return;
  $("detail-title").textContent = data.claim.externalClaimId + " · Audit trail";
  $("event-state").textContent = "Event: " + data.event.state +
    (data.event.state === "RetryScheduled" ? " · Next attempt " + time(data.event.nextAttemptAt) : "");
  $("audit").replaceChildren();
  for (const entry of data.audit) {
    const item = document.createElement("div"); item.className = "audit-item";
    const when = document.createElement("time"); when.textContent = time(entry.occurredAt);
    when.dateTime = new Date(entry.occurredAt).toISOString();
    const info = document.createElement("div");
    const action = document.createElement("strong"); action.textContent = entry.action;
    const text = document.createElement("p"); text.textContent = entry.detail;
    info.append(action, text); item.append(when, info); $("audit").append(item);
  }
  $("replay-form").hidden = data.claim.status !== "DeadLetter";
}
$("replay-form").onsubmit = async e => {
  e.preventDefault();
  const id = selectedId;
  $("replay").disabled = true;
  try {
    await api("/api/claims/" + encodeURIComponent(id) + "/replay", {
      method: "POST", headers: {"Content-Type": "application/json"},
      body: JSON.stringify({reason: $("reason").value})
    });
    notice("Replay queued. Audit history preserved.");
    await refresh();
  } catch (e) { notice(e.message, true); }
  finally { $("replay").disabled = false; }
};
async function refresh() {
  if (refreshBusy) return;
  refreshBusy = true;
  try {
    renderClaims(await api("/api/claims"));
    await detail();
    $("connection").textContent = "Last refreshed " + time(Date.now());
    $("connection").className = "muted";
  } catch (e) {
    $("connection").textContent = "Connection unavailable. Retrying automatically.";
    $("connection").className = "error";
  } finally { refreshBusy = false; }
}
newId(); refresh(); setInterval(refresh, 2000);
