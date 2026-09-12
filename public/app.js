// ============================================================
// Ferry client
// ============================================================

// ---- device identity (persisted per browser) ----
const ID_KEY = "ferry_device";
let device = JSON.parse(localStorage.getItem(ID_KEY) || localStorage.getItem("flowlite_device") || "null");
if (!device) {
  const isMobile = /Android|iPhone|iPad|Mobile/i.test(navigator.userAgent);
  device = {
    id: Math.random().toString(36).slice(2) + Date.now().toString(36),
    name: isMobile ? "Phone" : "Laptop",
  };
}
function saveDevice() { localStorage.setItem(ID_KEY, JSON.stringify(device)); }
saveDevice();
const isLocalhost = ["localhost", "127.0.0.1"].includes(location.hostname);
const AUTH_KEY = "ferry_token";
const startupUrl = new URL(location.href);
let authToken = startupUrl.searchParams.get("token") || localStorage.getItem(AUTH_KEY) || "";
if (startupUrl.searchParams.get("token")) {
  localStorage.setItem(AUTH_KEY, authToken);
  startupUrl.searchParams.delete("token");
  history.replaceState(null, "", startupUrl.pathname + startupUrl.search + startupUrl.hash);
}

const $ = (s) => document.querySelector(s);
const thread = $("#thread");
const input = $("#input");
const sendBtn = $("#sendBtn");
const fileInput = $("#fileInput");
const scrollArea = $("#scrollableArea");

let cachedMessages = [];   // for cleanup impact preview
let lastStorage = null;
let lastInfo = null;
let pinnedItems = [];
let pinsExpanded = false;
const DRAFT_KEY = "ferry_draft_v1";
const READ_KEY = "ferry_last_read_message_id_v1";
let lastReadId = Number(localStorage.getItem(READ_KEY) || 0);
let draftTimer = null;
let uploadQueue = [];
let uploadRunning = false;
let currentWs = null;
let reconnectTimer = null;
let reconnectAttempts = 0;
let hiddenSince = 0;
let filterTerm = "";
let filterDebounceTimer = null;
let savedScrollTop = 0;
let isFiltering = false;

// ---- helpers ----
function fmtBytes(n) {
  if (!n) return "0 B";
  const u = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.floor(Math.log(n) / Math.log(1024));
  return `${(n / Math.pow(1024, i)).toFixed(i ? 1 : 0)} ${u[i]}`;
}
function fmtTime(ts) { return new Date(ts).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" }); }
function dateKey(ts) { const d = new Date(ts); return `${d.getFullYear()}-${d.getMonth()}-${d.getDate()}`; }
function dayLabel(ts) {
  const d = new Date(ts), now = Date.now();
  if (dateKey(ts) === dateKey(now)) return "Today";
  if (dateKey(ts) === dateKey(now - 86400000)) return "Yesterday";
  return d.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" });
}
function escapeHtml(s) {
  return s.replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}
function linkify(s) {
  return escapeHtml(s).replace(/(https?:\/\/[^\s<]+)/g, '<a href="$1" target="_blank" rel="noopener">$1</a>');
}
function icon(id, cls = "ico") { return `<svg class="${cls}"><use href="#${id}"/></svg>`; }
function authQuery() { return authToken ? `token=${encodeURIComponent(authToken)}` : ""; }
function withAuth(url) {
  if (!authToken) return url;
  const u = new URL(url, location.href);
  u.searchParams.set("token", authToken);
  return u.pathname + u.search + u.hash;
}
async function apiFetch(url, opts = {}) {
  const headers = new Headers(opts.headers || {});
  if (authToken) headers.set("X-Ferry-Token", authToken);
  const res = await fetch(url, { ...opts, headers });
  if (res.status === 401) {
    showAuthRequired();
    throw new Error("pairing required");
  }
  return res;
}
async function apiJson(url, opts = {}) {
  return (await apiFetch(url, opts)).json();
}
function showAuthRequired() {
  const rem = $("#reminder");
  rem.innerHTML = `${icon("i-warn", "ico ico-sm")}<span>On your laptop: Connect → scan QR.</span>`;
  rem.classList.remove("hidden");
}

const THUMBABLE = ["jpg", "jpeg", "png", "gif", "webp", "avif", "bmp", "svg"];
function ext(name) { return (name.split(".").pop() || "").toLowerCase(); }
function isThumbable(name) { return THUMBABLE.includes(ext(name)); }
function fileIconId(name) {
  const e = ext(name);
  if (THUMBABLE.includes(e) || ["heic", "heif"].includes(e)) return "i-image";
  if (["mp4", "mov", "mkv", "webm", "avi", "m4v"].includes(e)) return "i-video";
  if (["mp3", "wav", "flac", "ogg", "m4a", "aac"].includes(e)) return "i-audio";
  if (["zip", "rar", "7z", "tar", "gz", "bz2"].includes(e)) return "i-archive";
  return "i-file";
}
function atBottom() { return scrollArea.scrollHeight - scrollArea.scrollTop - scrollArea.clientHeight < 90; }
function scrollDown() { scrollArea.scrollTop = scrollArea.scrollHeight; }
function latestMessageId() { return cachedMessages.length ? cachedMessages[cachedMessages.length - 1].id : 0; }
function unreadMessages() { return cachedMessages.filter((m) => m.id > lastReadId); }
function renderUnread() {
  const count = unreadMessages().length;
  const badge = $("#unreadCount");
  badge.classList.toggle("hidden", count === 0);
  badge.textContent = count ? `${count} new` : "";
}
function saveDraftSoon() {
  clearTimeout(draftTimer);
  draftTimer = setTimeout(() => {
    if (input.value) localStorage.setItem(DRAFT_KEY, input.value);
    else localStorage.removeItem(DRAFT_KEY);
  }, 300);
}
function clearDraft() { clearTimeout(draftTimer); localStorage.removeItem(DRAFT_KEY); }
function markReadAtBottom() {
  if (filterTerm) return;
  if (!atBottom()) return;
  const latest = latestMessageId();
  if (latest <= lastReadId) return;
  lastReadId = latest;
  localStorage.setItem(READ_KEY, String(lastReadId));
  thread.querySelector(".new-sep")?.remove();
  renderUnread();
}
function pinActionHtml(m) {
  const label = m.pinnedAt ? "Unpin" : "Pin";
  return `<button class="act btn-pin" data-id="${m.id}" data-pinned="${m.pinnedAt ? "true" : "false"}">${label}</button>`;
}
function copyActionHtml(m) { return `<button class="act btn-copy" data-id="${m.id}">${icon("i-copy", "ico ico-sm")} <span>Copy</span></button>`; }
function pinPreview(m) { return m.kind === "file" ? (m.deleted ? `${m.filename} · removed` : m.filename) : (m.text || "(empty message)"); }
function renderPins() {
  const strip = $("#pinnedStrip");
  strip.classList.toggle("hidden", !pinnedItems.length);
  if (!pinnedItems.length) { strip.innerHTML = ""; return; }
  const items = pinsExpanded
    ? `<div class="pinned-items">${pinnedItems.map((m) => {
        const preview = escapeHtml(pinPreview(m));
        const kindLabel = m.kind === "file" ? "File" : "Note";
        const jumpLabel = `Jump to pinned ${kindLabel.toLowerCase()}: ${preview}`;
        const unpinLabel = `Unpin ${preview}`;
        return `<div class="pinned-item ${m.deleted ? "gone" : ""}">` +
          `<button class="pinned-jump" type="button" data-id="${m.id}" aria-label="${jumpLabel}">` +
            `<span class="pinned-kind">${kindLabel}</span>` +
            `<span class="pinned-preview">${preview}</span>` +
          `</button>` +
          `<button class="act btn-pin" type="button" data-id="${m.id}" data-pinned="true" aria-label="${unpinLabel}">Unpin</button>` +
        `</div>`;
      }).join("")}</div>`
    : "";
  strip.innerHTML = `<button class="pinned-toggle" type="button" aria-expanded="${pinsExpanded}">${icon("i-link", "ico ico-sm")} Pinned · ${pinnedItems.length}</button>${items}`;
}
async function applyPins(pins) {
  pinnedItems = pins;
  const pinnedById = new Map(pins.map((m) => [m.id, m.pinnedAt]));
  cachedMessages.forEach((m) => { m.pinnedAt = pinnedById.get(m.id) || null; });
  renderPins();
  await loadHistory();
}
async function setPinned(id, pinned) {
  const result = await apiJson(`/api/messages/${id}/pin`, { method: "PUT", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ pinned }) });
  await applyPins(result.pins);
}
function dragFilename(m) {
  if (m.kind === "file") return m.filename;
  return `Ferry message ${new Date(m.createdAt).toISOString().slice(0, 16).replace(/[T:]/g, "-")}.txt`;
}
function startExportDrag(e, m) {
  if (!e.dataTransfer) return;
  const url = new URL(withAuth(m.kind === "file" ? `/api/download/${m.id}` : `/api/export-message/${m.id}`), location.href).href;
  const filename = dragFilename(m).replace(/[\r\n:]/g, "-");
  e.dataTransfer.effectAllowed = "copy";
  // Chromium on Windows recognises DownloadURL as a real file drag to Explorer/Desktop.
  e.dataTransfer.setData("DownloadURL", `application/octet-stream:${filename}:${url}`);
  e.dataTransfer.setData("text/uri-list", url);
  e.dataTransfer.setData("text/plain", m.kind === "text" ? (m.text || "") : m.filename);
}

// ---- file/message rendering ----
function actionsHtml(m) {
  if (isLocalhost) {
    return `
      <button class="act btn-open" data-id="${m.id}">${icon("i-open", "ico ico-sm")} Open</button>
      <button class="act btn-reveal" data-id="${m.id}" title="Show in folder">${icon("i-folder", "ico ico-sm")} Folder</button>
      ${pinActionHtml(m)}
      <a class="act icon-only dl" href="${withAuth(`/api/download/${m.id}`)}" download title="Download">${icon("i-download", "ico ico-sm")}</a>`;
  }
  return `
    <a class="act primary dl" href="${withAuth(`/api/download/${m.id}`)}" download>${icon("i-download", "ico ico-sm")} Download</a>
    ${pinActionHtml(m)}
    <button class="act btn-open" data-id="${m.id}" title="Open on laptop">${icon("i-open", "ico ico-sm")} Open on laptop</button>`;
}
function buildNode(m, prev) {
  const mine = m.senderId === device.id;
  const grouped = prev && prev.senderId === m.senderId &&
    Math.abs(m.createdAt - prev.createdAt) < 4 * 60000 && !prev.deleted;

  const row = document.createElement("div");
  row.className = "msg" + (mine ? " mine" : "") + (grouped ? " grouped" : "");
  row.dataset.id = m.id;
  row.dataset.sender = m.senderId;
  row.dataset.ts = m.createdAt;
  if (!m.deleted) {
    row.draggable = true;
    row.classList.add("draggable");
    row.title = "Drag to save on this computer";
    row.addEventListener("dragstart", (e) => startExportDrag(e, m));
  }

  const meta = `<div class="passage-meta"><span>${fmtTime(m.createdAt)}</span><span class="who">${mine ? "You" : escapeHtml(m.senderName)}</span></div>`;
  const body = m.kind === "file" ? (m.deleted ? goneCard(m) : fileCard(m)) : `<div class="text-card"><div class="bubble">${linkify(m.text || "")}</div><div class="message-actions">${copyActionHtml(m)}${pinActionHtml(m)}</div></div>`;

  row.innerHTML = `${meta}<div class="passage-spine" aria-hidden="true"><span></span></div><div class="bubble-col">${body}</div>`;
  return row;
}
function goneCard(m) {
  return `<div class="card gone"><div class="card-row">
    <div class="file-ic">${icon("i-file")}</div>
    <div class="file-info"><div class="file-name">${escapeHtml(m.filename)}</div><div class="tag-removed">removed</div></div>
  </div></div>`;
}
function fileCard(m) {
  if (isThumbable(m.filename)) {
    return `<div class="card has-thumb">
      <div class="thumb-wrap" data-img="${withAuth(`/api/download/${m.id}`)}" data-name="${escapeHtml(m.filename)}">
        <img loading="lazy" src="${withAuth(`/api/download/${m.id}`)}" alt="${escapeHtml(m.filename)}" />
        <div class="thumb-badge">${icon("i-image", "ico ico-sm")}<span>${escapeHtml(m.filename)} · ${fmtBytes(m.size)}</span></div>
      </div>
      <div class="card-actions">${actionsHtml(m)}</div>
    </div>`;
  }
  return `<div class="card">
    <div class="card-row">
      <div class="file-ic">${icon(fileIconId(m.filename))}</div>
      <div class="file-info"><div class="file-name">${escapeHtml(m.filename)}</div><div class="file-size">${fmtBytes(m.size)}</div></div>
    </div>
    <div class="card-actions">${actionsHtml(m)}</div>
  </div>`;
}

// ---- day separators ----
function daySep(ts) {
  const el = document.createElement("div");
  el.className = "day-sep";
  el.dataset.daykey = dateKey(ts);
  el.textContent = dayLabel(ts);
  return el;
}
function newSep() {
  const el = document.createElement("div");
  el.className = "new-sep";
  el.textContent = "New since last visit";
  return el;
}
function lastMsgInfo() {
  const rows = thread.querySelectorAll(".msg");
  const last = rows[rows.length - 1];
  return last ? { senderId: last.dataset.sender, createdAt: Number(last.dataset.ts), deleted: false } : null;
}
function lastDayKey() {
  const seps = thread.querySelectorAll(".day-sep");
  return seps.length ? seps[seps.length - 1].dataset.daykey : null;
}
function visibleMessages() {
  if (!filterTerm) return cachedMessages;
  const t = filterTerm.toLowerCase();
  return cachedMessages.filter((m) =>
    (m.text || "").toLowerCase().includes(t) ||
    (m.filename || "").toLowerCase().includes(t));
}
function addMessage(m) {
  const i = cachedMessages.findIndex((x) => x.id === m.id);
  if (i >= 0) cachedMessages[i] = m; else cachedMessages.push(m);

  if (filterTerm) {
    const t = filterTerm.toLowerCase();
    const matches = (m.text || "").toLowerCase().includes(t) || (m.filename || "").toLowerCase().includes(t);
    if (!matches) {
      renderUnread();
      return;
    }
  }

  const emptyEl = thread.querySelector(".empty");
  if (emptyEl) emptyEl.remove();

  const existing = thread.querySelector(`.msg[data-id="${m.id}"]`);
  if (existing) { existing.replaceWith(buildNode(m, null)); }
  else {
    const stick = atBottom();
    if (lastDayKey() !== dateKey(m.createdAt)) thread.appendChild(daySep(m.createdAt));
    thread.appendChild(buildNode(m, lastMsgInfo()));
    if (stick) scrollDown();
  }
  if (!filterTerm) {
    if (atBottom()) markReadAtBottom(); else renderUnread();
  } else {
    renderUnread();
  }
}
function renderEmpty(text = "Nothing here yet", subtext = "Send a message or file.") {
  thread.innerHTML = `<div class="empty">
    <div class="ring">${icon("i-logo")}</div>
    <h2>${escapeHtml(text)}</h2>
    ${subtext ? `<p>${escapeHtml(subtext)}</p>` : ""}
  </div>`;
}
function renderThread(list) {
  thread.innerHTML = "";
  if (!list.length) {
    if (filterTerm) renderEmpty("No matches.", "");
    else renderEmpty();
    return;
  }
  let prev = null, curDay = null, insertedNew = false;
  for (const m of list) {
    const dk = dateKey(m.createdAt);
    if (dk !== curDay) { thread.appendChild(daySep(m.createdAt)); curDay = dk; prev = null; }
    if (!filterTerm && !insertedNew && m.id > lastReadId) { thread.appendChild(newSep()); insertedNew = true; }
    thread.appendChild(buildNode(m, prev));
    prev = m;
  }
  renderUnread();
}
async function loadHistory() {
  const msgs = await apiJson("/api/messages");
  cachedMessages = msgs;
  const latest = latestMessageId();
  if (lastReadId > latest) { lastReadId = 0; localStorage.setItem(READ_KEY, "0"); }
  if (!lastReadId && latest) { lastReadId = latest; localStorage.setItem(READ_KEY, String(latest)); }
  renderThread(visibleMessages());
  const hasNew = !filterTerm && cachedMessages.some((m) => m.id > lastReadId);
  if (!hasNew) scrollDown();
}
async function loadPins() { pinnedItems = await apiJson("/api/pins"); renderPins(); }

// ---- sending ----
async function sendText() {
  const text = input.value.trim();
  if (!text) return;
  await apiFetch("/api/messages", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ text, senderId: device.id, senderName: device.name }),
  });
  input.value = "";
  clearDraft();
  autoGrow();
}
async function uploadFiles(files) {
  enqueueFiles(files);
}
function renderUploadQueue() {
  const box = $("#uploadQueue");
  box.classList.toggle("hidden", uploadQueue.length === 0);
  box.innerHTML = uploadQueue.map((entry) => {
    const progress = entry.status === "uploading" ? `<div class="upload-progress"><span style="--progress:${entry.progress / 100}"></span></div><span class="upload-percent">${entry.progress}%</span>` : "";
    const action = entry.status === "failed" ? `<button class="act queue-retry" data-id="${entry.id}">Retry</button>` : `<button class="act queue-remove" data-id="${entry.id}">${entry.status === "uploading" ? "Cancel" : "Remove"}</button>`;
    const status = entry.status === "failed" ? `<span class="upload-error">${escapeHtml(entry.error || "Upload failed")}</span>` : `<span class="upload-status">${entry.status === "waiting" ? "Waiting" : "Sending"}</span>`;
    const visual = entry.previewUrl
      ? `<img class="transfer-preview" src="${entry.previewUrl}" alt="" />`
      : `<span class="transfer-glyph">${icon(fileIconId(entry.file.name))}</span>`;
    return `<div class="upload-row ${entry.status === "uploading" ? "active" : ""}">
      ${visual}
      <div class="transfer-copy"><strong class="upload-name">${escapeHtml(entry.file.name)}</strong>${status}${progress}</div>
      ${action}
    </div>`;
  }).join("");
}
function enqueueFiles(files) {
  for (const file of files) uploadQueue.push({
    id: `${Date.now()}-${Math.random().toString(36).slice(2)}`,
    file,
    previewUrl: isThumbable(file.name) ? URL.createObjectURL(file) : "",
    status: "waiting", progress: 0, error: "", xhr: null,
  });
  renderUploadQueue();
  processUploadQueue();
}
function processUploadQueue() {
  if (uploadRunning) return;
  const entry = uploadQueue.find((item) => item.status === "waiting");
  if (!entry) return;
  uploadRunning = true;
  entry.status = "uploading";
  renderUploadQueue();
  const q = new URLSearchParams({ name: entry.file.name, senderId: device.id, senderName: device.name });
  if (authToken) q.set("token", authToken);
  const xhr = entry.xhr = new XMLHttpRequest();
  xhr.open("POST", `/api/upload?${q}`);
  if (authToken) xhr.setRequestHeader("X-Ferry-Token", authToken);
  xhr.upload.onprogress = (event) => {
    if (!event.lengthComputable) return;
    entry.progress = Math.round((event.loaded / event.total) * 100);
    renderUploadQueue();
  };
  const finish = (failed = "") => {
    uploadRunning = false;
    if (!uploadQueue.includes(entry)) { processUploadQueue(); return; }
    if (failed) { entry.status = "failed"; entry.error = failed; entry.xhr = null; }
    else {
      if (entry.previewUrl) URL.revokeObjectURL(entry.previewUrl);
      uploadQueue = uploadQueue.filter((item) => item !== entry);
    }
    renderUploadQueue();
    processUploadQueue();
  };
  xhr.onload = () => finish(xhr.status >= 200 && xhr.status < 300 ? "" : `Upload failed (${xhr.status})`);
  xhr.onerror = () => finish("Network error");
  xhr.onabort = () => finish("Cancelled");
  xhr.send(entry.file);
}
function removeQueuedUpload(id) {
  const entry = uploadQueue.find((item) => item.id === id);
  if (!entry) return;
  if (entry.status === "uploading") entry.xhr?.abort();
  if (entry.previewUrl) URL.revokeObjectURL(entry.previewUrl);
  uploadQueue = uploadQueue.filter((item) => item !== entry);
  renderUploadQueue();
  processUploadQueue();
}
function retryQueuedUpload(id) {
  const entry = uploadQueue.find((item) => item.id === id);
  if (!entry || entry.status !== "failed") return;
  entry.status = "waiting"; entry.progress = 0; entry.error = "";
  uploadQueue = [entry, ...uploadQueue.filter((item) => item !== entry)];
  renderUploadQueue();
  processUploadQueue();
}

// ---- storage / settings ----
async function refreshStorage(stats) {
  const s = stats || (await apiJson("/api/storage"));
  lastStorage = s;
  $("#storageUsed").textContent = fmtBytes(s.fileBytes);
  $("#storageLimit").textContent = `of ${fmtBytes(s.limitBytes)}`;
  $("#storageStats").innerHTML = `
    <div class="row"><span>Files</span><span class="v">${s.fileCount}</span></div>
    <div class="row"><span>Messages</span><span class="v">${s.messageCount}</span></div>`;
  const pct = Math.min(100, Math.round((s.fileBytes / s.limitBytes) * 100));
  const fill = $("#meterFill");
  fill.style.setProperty("--progress", String(pct / 100));
  fill.classList.toggle("warn", s.overLimit);

  const rem = $("#reminder");
  if (s.overLimit) {
    rem.innerHTML = `${icon("i-warn", "ico ico-sm")}<span>${fmtBytes(s.fileBytes)} of ${fmtBytes(s.limitBytes)} used. Clean up in Settings.</span>`;
    rem.classList.remove("hidden");
  } else rem.classList.add("hidden");
}

// cleanup smart control
let cleanupDays = null;
let cleanupArmed = false;
let cleanupArmTimer = null;
function computeImpact(days) {
  const cutoff = Date.now() - days * 86400000;
  let n = 0, bytes = 0;
  for (const m of cachedMessages) {
    if (m.kind === "file" && !m.deleted && m.createdAt < cutoff) { n++; bytes += m.size || 0; }
  }
  return { n, bytes };
}
function disarmCleanup() {
  cleanupArmed = false;
  clearTimeout(cleanupArmTimer);
  $("#cleanupRun").classList.remove("armed");
}
function updateImpact() {
  disarmCleanup();
  const run = $("#cleanupRun");
  const impact = $("#cleanupImpact");
  const label = $("#cleanupRunLabel");
  if (!cleanupDays || cleanupDays < 1) {
    impact.textContent = "Choose an age.";
    run.disabled = true; label.textContent = "Delete files";
    return;
  }
  const { n, bytes } = computeImpact(cleanupDays);
  if (n === 0) {
    impact.innerHTML = `Nothing older than <b>${cleanupDays} days</b>.`;
    run.disabled = true; label.textContent = "Delete files";
  } else {
    impact.innerHTML = `<b>${n} file${n > 1 ? "s" : ""}</b> · <b>${fmtBytes(bytes)}</b>`;
    run.disabled = false; label.textContent = `Delete ${n} file${n > 1 ? "s" : ""}`;
  }
}
function wireCleanup() {
  $("#cleanupSeg").querySelectorAll("button").forEach((b) =>
    b.addEventListener("click", () => {
      $("#cleanupSeg").querySelectorAll("button").forEach((x) => x.classList.remove("on"));
      b.classList.add("on");
      const custom = $("#cleanupCustom");
      if (b.dataset.days === "custom") {
        custom.classList.remove("hidden"); custom.focus();
        cleanupDays = Number(custom.value) || null;
      } else {
        custom.classList.add("hidden");
        cleanupDays = Number(b.dataset.days);
      }
      updateImpact();
    })
  );
  $("#cleanupCustom").addEventListener("input", (e) => { cleanupDays = Number(e.target.value) || null; updateImpact(); });

  $("#cleanupRun").addEventListener("click", async () => {
    const run = $("#cleanupRun");
    if (run.disabled || !cleanupDays) return;
    if (!cleanupArmed) {
      cleanupArmed = true;
      run.classList.add("armed");
      $("#cleanupRunLabel").textContent = "Confirm delete";
      cleanupArmTimer = setTimeout(() => { cleanupArmed = false; run.classList.remove("armed"); updateImpact(); }, 3000);
      return;
    }
    disarmCleanup();
    const r = await (await apiFetch("/api/cleanup", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ days: cleanupDays }),
    })).json();
    $("#cleanupImpact").innerHTML = `${icon("i-check", "ico ico-sm")} Deleted ${r.removed} · ${fmtBytes(r.freedBytes)} freed`;
    run.disabled = true;
    await loadHistory();
    refreshStorage();
  });
}

// drawer open/close
function openSettings() {
  $("#deviceNameInput").value = device.name;
  refreshStorage();
  refreshAuthStatus();
  // reset cleanup
  cleanupDays = null;
  $("#cleanupSeg").querySelectorAll("button").forEach((x) => x.classList.remove("on"));
  $("#cleanupCustom").classList.add("hidden"); $("#cleanupCustom").value = "";
  updateImpact();
  $("#settingsDrawer").classList.remove("hidden");
  $("#settingsBackdrop").classList.remove("hidden");
  setTimeout(() => $("#settingsDrawer").classList.add("open"), 20);
}
function closeSettings() {
  $("#settingsDrawer").classList.remove("open");
  $("#settingsBackdrop").classList.add("hidden");
  setTimeout(() => $("#settingsDrawer").classList.add("hidden"), 220);
}

// ---- connect (QR) modal ----
let connectFullUrl = "";
function renderQR(url) {
  const box = $("#qrBox");
  try {
    if (typeof window.qrcode !== "function") throw new Error("qr lib missing");
    const qr = window.qrcode(0, "M");
    qr.addData(url); qr.make();
    box.classList.remove("error");
    box.innerHTML = qr.createSvgTag({ cellSize: 6, margin: 0, scalable: true });
  } catch {
    box.classList.add("error");
    box.textContent = "Could not draw the code. Use the link below.";
  }
}
function copyText(text, btn) {
  const done = () => { if (btn) { btn.classList.add("done"); setTimeout(() => btn.classList.remove("done"), 1400); } };
  if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(done).catch(() => fallbackCopy(text, done));
  else fallbackCopy(text, done);
}
function fallbackCopy(text, done) {
  const ta = document.createElement("textarea");
  ta.value = text; ta.style.position = "fixed"; ta.style.opacity = "0";
  document.body.appendChild(ta); ta.select();
  try { document.execCommand("copy"); done(); } catch {}
  ta.remove();
}
function copyMessage(text, button) {
  const label = button.querySelector("span");
  const show = (value) => {
    label.textContent = value;
    setTimeout(() => { label.textContent = "Copy"; }, 1400);
  };
  const fallback = () => {
    const ta = document.createElement("textarea");
    ta.value = text; ta.style.position = "fixed"; ta.style.opacity = "0";
    document.body.appendChild(ta); ta.select();
    const copied = document.execCommand("copy");
    ta.remove();
    show(copied ? "Copied" : "Copy failed");
  };
  if (navigator.clipboard && window.isSecureContext) navigator.clipboard.writeText(text).then(() => show("Copied")).catch(fallback);
  else fallback();
}
async function openConnect() {
  $("#connectModal").classList.remove("hidden");
  let primary, alt = [];
  try {
    const info = await apiJson("/api/info");
    lastInfo = info;
    primary = info.primary;
    alt = (info.urls || []).filter((u) => u !== primary);
  } catch {}
  if (!primary) primary = `${location.protocol}//${location.host}`;
  connectFullUrl = primary;
  $("#connectUrl").textContent = new URL(primary, location.href).host;
  renderQR(primary);
  $("#connectAlt").innerHTML = alt.length
    ? `<b>Also:</b> ${alt.map((u) => `<code>${new URL(u, location.href).host}</code>`).join(" · ")}` : "";
}

async function refreshAuthStatus() {
  const status = $("#authStatus");
  try {
    const info = await apiJson("/api/info");
    lastInfo = info;
    status.textContent = authToken || isLocalhost ? "Paired" : "Not paired";
    status.classList.toggle("ok", !!(authToken || isLocalhost));
  } catch {
    status.textContent = "Scan the laptop QR.";
    status.classList.remove("ok");
  }
}

async function copyPairLink(btn) {
  let link = lastInfo && lastInfo.primary;
  if (!link) {
    const info = await apiJson("/api/info");
    lastInfo = info;
    link = info.primary;
  }
  if (link) copyText(link, btn);
}

let rotateArmed = false;
let rotateTimer = null;
function disarmRotate() {
  rotateArmed = false;
  clearTimeout(rotateTimer);
  $("#rotateToken").textContent = "Rotate token";
}

async function rotateSharedToken() {
  const btn = $("#rotateToken");
  if (!rotateArmed) {
    rotateArmed = true;
    btn.textContent = "Confirm rotate";
    rotateTimer = setTimeout(disarmRotate, 3500);
    return;
  }
  disarmRotate();
  const data = await apiJson("/api/auth/rotate", { method: "POST" });
  const newUrl = new URL(data.info.primary);
  authToken = newUrl.searchParams.get("token") || authToken;
  if (authToken) localStorage.setItem(AUTH_KEY, authToken);
  lastInfo = data.info;
  $("#authImpact").textContent = "Rotated. Scan the new QR.";
  refreshAuthStatus();
  connectWS();
}

// ---- theme (light / dark / system) ----
const THEME_KEY = "ferry_theme";
let themePref = localStorage.getItem(THEME_KEY) || "system";
const themeMql = window.matchMedia("(prefers-color-scheme: light)");
function resolvedTheme() { return themePref === "system" ? (themeMql.matches ? "light" : "dark") : themePref; }
function applyTheme() {
  const t = resolvedTheme();
  document.documentElement.dataset.theme = t;
  const meta = document.querySelector('meta[name="theme-color"]');
  if (meta) meta.setAttribute("content", t === "light" ? "#F4F3EF" : "#111614");
  document.querySelectorAll("#themeSeg button").forEach((b) => b.classList.toggle("on", b.dataset.theme === themePref));
}
themeMql.addEventListener("change", () => { if (themePref === "system") applyTheme(); });
document.querySelectorAll("#themeSeg button").forEach((b) =>
  b.addEventListener("click", () => {
    themePref = b.dataset.theme;
    localStorage.setItem(THEME_KEY, themePref);
    applyTheme();
  })
);

// ---- websocket ----
function setConn(on) {
  $("#connDot").classList.toggle("on", on);
  $("#deviceLabel").textContent = on
    ? (isLocalhost ? "Ready" : "Connected")
    : "Reconnecting...";
}
function renderPresence(list) {
  const others = (list || []).filter((d) => d.id !== device.id);
  const dot = $("#presenceDot");
  dot.textContent = others.length === 1 ? `● ${others[0].name}` :
    others.length > 1 ? `● ${others.length} attached` : "";
}
let wsFirstConnect = true;

function reconnectDelay() {
  const base = authToken ? 1000 : 5000;
  const cap = 30000;
  const ceiling = Math.min(cap, base * Math.pow(2, reconnectAttempts));
  return Math.random() * ceiling;   // full jitter
}

function ensureConnected() {
  if (document.visibilityState !== "visible") return;
  const awayMs = hiddenSince ? Date.now() - hiddenSince : 0;
  hiddenSince = 0;

  const state = currentWs ? currentWs.readyState : WebSocket.CLOSED;
  if (state === WebSocket.OPEN) {
    if (awayMs > 60000) {
      loadHistory().catch(() => {});
      loadPins().catch(() => {});
    }
    return;
  }
  if (state === WebSocket.CONNECTING) return;

  setConn(false);
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  connectWS();
}

function connectWS() {
  if (reconnectTimer) {
    clearTimeout(reconnectTimer);
    reconnectTimer = null;
  }
  if (currentWs && (currentWs.readyState === WebSocket.CONNECTING || currentWs.readyState === WebSocket.OPEN)) {
    return;
  }

  const proto = location.protocol === "https:" ? "wss" : "ws";
  const qs = authQuery();
  const params = new URLSearchParams(qs);
  params.set("senderId", device.id);
  params.set("senderName", device.name);
  const ws = new WebSocket(`${proto}://${location.host}/ws?${params}`);
  currentWs = ws;
  ws.onopen = () => {
    reconnectAttempts = 0;
    setConn(true);
    // A fresh socket may have missed broadcasts while we were away
    // (background tab, Doze, WiFi roam): catch up instead of staying stale.
    if (!wsFirstConnect) {
      loadHistory().catch(() => {});
      loadPins().catch(() => {});
    }
    wsFirstConnect = false;
  };
  ws.onmessage = (ev) => {
    if (typeof ev.data !== "string" || !ev.data) return;
    let data;
    try { data = JSON.parse(ev.data); } catch { return; }
    if (data.type === "message") addMessage(data.message);
    else if (data.type === "pins") applyPins(data.pins);
    else if (data.type === "storage") refreshStorage(data.storage);
    else if (data.type === "cleanup") loadHistory();
    else if (data.type === "presence") renderPresence(data.presence || []);
    else if (data.type === "auth") showAuthRequired();
  };
  ws.onclose = () => {
    if (ws !== currentWs) return;
    setConn(false);
    reconnectAttempts++;
    reconnectTimer = setTimeout(connectWS, reconnectDelay());
  };
}

// ---- input UX ----
function updateSendState() {
  const hasText = !!input.value.trim();
  sendBtn.disabled = !hasText;
  sendBtn.setAttribute("aria-disabled", String(!hasText));
}
function autoGrow() {
  input.style.height = "auto";
  input.style.height = Math.min(input.scrollHeight, 120) + "px";
  updateSendState();
}
input.addEventListener("input", () => { autoGrow(); saveDraftSoon(); updateSendState(); });
input.addEventListener("keydown", (e) => {
  if (e.key !== "Enter" || e.shiftKey || e.isComposing) return;
  // Phone keyboards can emit Enter when confirming a paste. Keep the pasted text editable;
  // the visible Send button remains the deliberate send action on touch devices.
  if (window.matchMedia("(pointer: coarse)").matches) return;
  e.preventDefault();
  sendText();
});
sendBtn.addEventListener("click", sendText);
$("#attachBtn").addEventListener("click", () => fileInput.click());
fileInput.addEventListener("change", () => { if (fileInput.files.length) uploadFiles([...fileInput.files]); fileInput.value = ""; });
scrollArea.addEventListener("scroll", markReadAtBottom);
$("#uploadQueue").addEventListener("click", (e) => {
  const retry = e.target.closest(".queue-retry");
  const remove = e.target.closest(".queue-remove");
  if (retry) retryQueuedUpload(retry.dataset.id);
  else if (remove) removeQueuedUpload(remove.dataset.id);
});

// device rename (from settings)
function commitDeviceName() {
  const v = $("#deviceNameInput").value.trim().slice(0, 40);
  if (v && v !== device.name) { device.name = v; saveDevice(); setConn($("#connDot").classList.contains("on")); }
}
$("#deviceNameInput").addEventListener("change", commitDeviceName);
$("#deviceNameInput").addEventListener("keydown", (e) => { if (e.key === "Enter") { commitDeviceName(); e.target.blur(); } });

// thread click delegation (OS actions + lightbox)
thread.addEventListener("click", (e) => {
  const open = e.target.closest(".btn-open");
  const reveal = e.target.closest(".btn-reveal");
  const thumb = e.target.closest(".thumb-wrap");
  const pin = e.target.closest(".btn-pin");
  const copy = e.target.closest(".btn-copy");
  if (open) { e.preventDefault(); apiFetch(`/api/open/${open.dataset.id}`, { method: "POST" }).catch(() => {}); }
  else if (reveal) { e.preventDefault(); apiFetch(`/api/reveal/${reveal.dataset.id}`, { method: "POST" }).catch(() => {}); }
  else if (copy) { e.preventDefault(); const message = cachedMessages.find((m) => m.id === Number(copy.dataset.id)); if (message) copyMessage(message.text || "", copy); }
  else if (pin) { e.preventDefault(); setPinned(pin.dataset.id, pin.dataset.pinned !== "true").catch(() => {}); }
  else if (thumb) { openLightbox(thumb.dataset.img, thumb.dataset.name); }
});
$("#pinnedStrip").addEventListener("click", (e) => {
  const toggle = e.target.closest(".pinned-toggle");
  const pin = e.target.closest(".btn-pin");
  const jump = e.target.closest(".pinned-jump");
  if (toggle) { pinsExpanded = !pinsExpanded; renderPins(); }
  else if (pin) {
    e.stopPropagation();
    setPinned(pin.dataset.id, false).catch(() => {});
  }
  else if (jump && jump.dataset.id) {
    const target = document.querySelector(`.msg[data-id="${jump.dataset.id}"]`);
    if (target) {
      target.scrollIntoView({ behavior: "smooth", block: "center" });
    }
  }
});
function openLightbox(src, alt) {
  const lb = document.createElement("div");
  lb.id = "lightbox";
  lb.innerHTML = `<button class="lb-close" aria-label="Close">${icon("i-close")}</button><img src="${src}" alt="${alt || ""}" />`;
  const close = () => lb.remove();
  lb.addEventListener("click", (e) => { if (e.target === lb || e.target.closest(".lb-close")) close(); });
  document.addEventListener("keydown", function esc(e) { if (e.key === "Escape") { close(); document.removeEventListener("keydown", esc); } });
  document.body.appendChild(lb);
}

// drop files from the desktop into Ferry
const dropHint = $("#dropHint");
const isFileDrag = (e) => [...(e.dataTransfer?.types || [])].includes("Files");
window.addEventListener("dragover", (e) => {
  if (!isFileDrag(e)) return;
  e.preventDefault();
  dropHint.classList.remove("hidden");
});
window.addEventListener("dragleave", (e) => { if (e.relatedTarget === null) dropHint.classList.add("hidden"); });
window.addEventListener("drop", (e) => {
  if (!isFileDrag(e)) return;
  e.preventDefault();
  dropHint.classList.add("hidden");
  if (e.dataTransfer.files.length) uploadFiles([...e.dataTransfer.files]);
});

// connect modal controls
$("#connectBtn").addEventListener("click", openConnect);
$("#closeConnect").addEventListener("click", () => $("#connectModal").classList.add("hidden"));
$("#connectModal").addEventListener("click", (e) => { if (e.target.id === "connectModal") e.currentTarget.classList.add("hidden"); });
$("#copyUrl").addEventListener("click", (e) => copyText(connectFullUrl, e.currentTarget));
$("#copyPairLink").addEventListener("click", (e) => copyPairLink(e.currentTarget).catch(() => showAuthRequired()));
$("#rotateToken").addEventListener("click", () => rotateSharedToken().catch(() => showAuthRequired()));

// filter controls
function openFilter() {
  const bar = $("#filterBar");
  const input = $("#filterInput");
  if (!bar || !input) return;
  if (bar.classList.contains("hidden")) {
    savedScrollTop = scrollArea.scrollTop;
    bar.classList.remove("hidden");
  }
  input.focus();
}

function closeFilter() {
  const bar = $("#filterBar");
  const input = $("#filterInput");
  if (!bar || !input) return;
  bar.classList.add("hidden");
  if (filterTerm || input.value) {
    input.value = "";
    filterTerm = "";
    isFiltering = false;
    renderThread(visibleMessages());
    scrollArea.scrollTop = savedScrollTop;
  }
}

function onFilterInput() {
  clearTimeout(filterDebounceTimer);
  filterDebounceTimer = setTimeout(() => {
    const input = $("#filterInput");
    const term = input ? input.value.trim() : "";
    if (!isFiltering && term) {
      savedScrollTop = scrollArea.scrollTop;
      isFiltering = true;
    }
    filterTerm = term;
    renderThread(visibleMessages());
    if (!filterTerm && isFiltering) {
      isFiltering = false;
      scrollArea.scrollTop = savedScrollTop;
    }
  }, 120);
}

$("#filterBtn")?.addEventListener("click", openFilter);
$("#closeFilter")?.addEventListener("click", closeFilter);
$("#filterInput")?.addEventListener("input", onFilterInput);
$("#filterInput")?.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    e.preventDefault();
    closeFilter();
  }
});

// settings controls
$("#settingsBtn").addEventListener("click", openSettings);
$("#closeSettings").addEventListener("click", closeSettings);
$("#settingsBackdrop").addEventListener("click", closeSettings);
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") {
    $("#connectModal").classList.add("hidden");
    if ($("#settingsDrawer").classList.contains("open")) closeSettings();
    if (!$("#filterBar").classList.contains("hidden")) closeFilter();
  } else if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "f") {
    e.preventDefault();
    openFilter();
  }
});
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "hidden") {
    hiddenSince = Date.now();
    return;
  }
  ensureConnected();
});
window.addEventListener("pageshow", ensureConnected);
wireCleanup();

// ---- boot ----
applyTheme();
$("#connectBtn").classList.toggle("hidden", !isLocalhost);
setConn(false);
const savedDraft = localStorage.getItem(DRAFT_KEY);
if (!input.value && savedDraft) { input.value = savedDraft; autoGrow(); }
updateSendState();
Promise.all([loadHistory(), loadPins()]).catch(() => {});
refreshStorage().catch(() => {});
connectWS();
