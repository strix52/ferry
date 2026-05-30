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
  rem.innerHTML = `${icon("i-warn", "ico ico-sm")}<span>Pair this device from Ferry on your laptop. Open Ferry there, press Connect, and scan the QR code.</span>`;
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
function avatarFor(name, mine) {
  const n = (name || "").toLowerCase();
  let inner;
  if (n.includes("phone") || n.includes("mobile")) inner = icon("i-phone", "ico ico-sm");
  else if (n.includes("laptop") || n.includes("pc") || n.includes("desktop") || n.includes("mac")) inner = icon("i-laptop", "ico ico-sm");
  else inner = escapeHtml((name || "?").trim()[0] || "?").toUpperCase();
  return `<div class="avatar ${mine ? "you" : ""}">${inner}</div>`;
}
function atBottom() { return scrollArea.scrollHeight - scrollArea.scrollTop - scrollArea.clientHeight < 90; }
function scrollDown() { scrollArea.scrollTop = scrollArea.scrollHeight; }

// ---- file/message rendering ----
function actionsHtml(m) {
  if (isLocalhost) {
    return `
      <button class="act btn-open" data-id="${m.id}">${icon("i-open", "ico ico-sm")} Open</button>
      <button class="act btn-reveal" data-id="${m.id}" title="Show in folder">${icon("i-folder", "ico ico-sm")} Folder</button>
      <a class="act icon-only dl" href="${withAuth(`/api/download/${m.id}`)}" download title="Download">${icon("i-download", "ico ico-sm")}</a>`;
  }
  return `
    <a class="act primary dl" href="${withAuth(`/api/download/${m.id}`)}" download>${icon("i-download", "ico ico-sm")} Download</a>
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

  const avatar = grouped ? `<div class="avatar spacer"></div>` : avatarFor(m.senderName, mine);
  const meta = grouped ? "" :
    `<div class="meta"><span class="who">${escapeHtml(m.senderName)}</span><span>${fmtTime(m.createdAt)}</span></div>`;
  const body = m.kind === "file" ? (m.deleted ? goneCard(m) : fileCard(m)) : `<div class="bubble">${linkify(m.text || "")}</div>`;

  row.innerHTML = `${avatar}<div class="bubble-col">${meta}${body}</div>`;
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
function lastMsgInfo() {
  const rows = thread.querySelectorAll(".msg");
  const last = rows[rows.length - 1];
  return last ? { senderId: last.dataset.sender, createdAt: Number(last.dataset.ts), deleted: false } : null;
}
function lastDayKey() {
  const seps = thread.querySelectorAll(".day-sep");
  return seps.length ? seps[seps.length - 1].dataset.daykey : null;
}
function addMessage(m) {
  const existing = thread.querySelector(`.msg[data-id="${m.id}"]`);
  if (existing) { existing.replaceWith(buildNode(m, null)); }
  else {
    const stick = atBottom();
    if (lastDayKey() !== dateKey(m.createdAt)) thread.appendChild(daySep(m.createdAt));
    thread.appendChild(buildNode(m, lastMsgInfo()));
    if (stick) scrollDown();
  }
  const i = cachedMessages.findIndex((x) => x.id === m.id);
  if (i >= 0) cachedMessages[i] = m; else cachedMessages.push(m);
}
function renderEmpty() {
  thread.innerHTML = `<div class="empty">
    <div class="ring">${icon("i-logo")}</div>
    <h2>Nothing here yet</h2>
    <p>Send a message or drop a file to start the thread between your phone and laptop.</p>
  </div>`;
}
async function loadHistory() {
  const msgs = await apiJson("/api/messages");
  cachedMessages = msgs;
  thread.innerHTML = "";
  if (!msgs.length) { renderEmpty(); return; }
  let prev = null, curDay = null;
  for (const m of msgs) {
    const dk = dateKey(m.createdAt);
    if (dk !== curDay) { thread.appendChild(daySep(m.createdAt)); curDay = dk; prev = null; }
    thread.appendChild(buildNode(m, prev));
    prev = m;
  }
  scrollDown();
}

// ---- sending ----
async function sendText() {
  const text = input.value.trim();
  if (!text) return;
  input.value = "";
  autoGrow();
  await apiFetch("/api/messages", {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ text, senderId: device.id, senderName: device.name }),
  });
}
async function uploadFiles(files) {
  for (const f of files) {
    const q = new URLSearchParams({ name: f.name, senderId: device.id, senderName: device.name });
    if (authToken) q.set("token", authToken);
    await apiFetch(`/api/upload?${q}`, { method: "POST", body: f });
  }
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
  fill.style.width = pct + "%";
  fill.classList.toggle("warn", s.overLimit);

  const rem = $("#reminder");
  if (s.overLimit) {
    rem.innerHTML = `${icon("i-warn", "ico ico-sm")}<span>Storage is at ${fmtBytes(s.fileBytes)}, past your ${fmtBytes(s.limitBytes)} reminder. Open Settings to clean up.</span>`;
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
    impact.textContent = "Pick an age to preview what gets removed.";
    run.disabled = true; label.textContent = "Delete old files";
    return;
  }
  const { n, bytes } = computeImpact(cleanupDays);
  if (n === 0) {
    impact.innerHTML = `Nothing older than <b>${cleanupDays} days</b>.`;
    run.disabled = true; label.textContent = "Delete old files";
  } else {
    impact.innerHTML = `Removes <b>${n} file${n > 1 ? "s" : ""}</b> · frees <b>${fmtBytes(bytes)}</b>`;
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
      $("#cleanupRunLabel").textContent = "Click again to confirm";
      cleanupArmTimer = setTimeout(() => { cleanupArmed = false; run.classList.remove("armed"); updateImpact(); }, 3000);
      return;
    }
    disarmCleanup();
    const r = await (await apiFetch("/api/cleanup", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ days: cleanupDays }),
    })).json();
    $("#cleanupImpact").innerHTML = `${icon("i-check", "ico ico-sm")} Removed ${r.removed} file${r.removed !== 1 ? "s" : ""}, freed ${fmtBytes(r.freedBytes)}.`;
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
  $("#connectUrl").textContent = primary.replace(/^https?:\/\//, "");
  renderQR(primary);
  $("#connectAlt").innerHTML = alt.length
    ? `<b>Also reachable at:</b> ${alt.map((u) => `<code>${u.replace(/^https?:\/\//, "")}</code>`).join(" · ")}` : "";
}

async function refreshAuthStatus() {
  const status = $("#authStatus");
  try {
    const info = await apiJson("/api/info");
    lastInfo = info;
    status.textContent = authToken || isLocalhost ? "Pairing is active." : "This device is not paired.";
    status.classList.toggle("ok", !!(authToken || isLocalhost));
  } catch {
    status.textContent = "Pair this device from the laptop QR code.";
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
    btn.textContent = "Click again to rotate";
    rotateTimer = setTimeout(disarmRotate, 3500);
    return;
  }
  disarmRotate();
  const data = await apiJson("/api/auth/rotate", { method: "POST" });
  const newUrl = new URL(data.info.primary);
  authToken = newUrl.searchParams.get("token") || authToken;
  if (authToken) localStorage.setItem(AUTH_KEY, authToken);
  lastInfo = data.info;
  $("#authImpact").textContent = "Token rotated. Scan the new QR on other devices.";
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
  if (meta) meta.setAttribute("content", t === "light" ? "#f4f1f5" : "#121013");
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
  $("#deviceLabel").textContent = on ? `This device: ${device.name}` : "Reconnecting...";
}
function connectWS() {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const qs = authQuery();
  const ws = new WebSocket(`${proto}://${location.host}/ws${qs ? `?${qs}` : ""}`);
  ws.onopen = () => setConn(true);
  ws.onmessage = (ev) => {
    const data = JSON.parse(ev.data);
    if (data.type === "message") addMessage(data.message);
    else if (data.type === "storage") refreshStorage(data.storage);
    else if (data.type === "cleanup") loadHistory();
    else if (data.type === "auth") showAuthRequired();
  };
  ws.onclose = () => { setConn(false); setTimeout(connectWS, authToken ? 1500 : 5000); };
}

// ---- input UX ----
function autoGrow() {
  input.style.height = "auto";
  input.style.height = Math.min(input.scrollHeight, 120) + "px";
}
input.addEventListener("input", autoGrow);
input.addEventListener("keydown", (e) => { if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendText(); } });
sendBtn.addEventListener("click", sendText);
$("#attachBtn").addEventListener("click", () => fileInput.click());
fileInput.addEventListener("change", () => { if (fileInput.files.length) uploadFiles([...fileInput.files]); fileInput.value = ""; });

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
  if (open) { e.preventDefault(); apiFetch(`/api/open/${open.dataset.id}`, { method: "POST" }).catch(() => {}); }
  else if (reveal) { e.preventDefault(); apiFetch(`/api/reveal/${reveal.dataset.id}`, { method: "POST" }).catch(() => {}); }
  else if (thumb) { openLightbox(thumb.dataset.img, thumb.dataset.name); }
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

// drag & drop (desktop)
const dropHint = $("#dropHint");
window.addEventListener("dragover", (e) => { e.preventDefault(); dropHint.classList.remove("hidden"); });
window.addEventListener("dragleave", (e) => { if (e.relatedTarget === null) dropHint.classList.add("hidden"); });
window.addEventListener("drop", (e) => { e.preventDefault(); dropHint.classList.add("hidden"); if (e.dataTransfer.files.length) uploadFiles([...e.dataTransfer.files]); });

// connect modal controls
$("#connectBtn").addEventListener("click", openConnect);
$("#closeConnect").addEventListener("click", () => $("#connectModal").classList.add("hidden"));
$("#connectModal").addEventListener("click", (e) => { if (e.target.id === "connectModal") e.currentTarget.classList.add("hidden"); });
$("#copyUrl").addEventListener("click", (e) => copyText(connectFullUrl, e.currentTarget));
$("#copyPairLink").addEventListener("click", (e) => copyPairLink(e.currentTarget).catch(() => showAuthRequired()));
$("#rotateToken").addEventListener("click", () => rotateSharedToken().catch(() => showAuthRequired()));

// settings controls
$("#settingsBtn").addEventListener("click", openSettings);
$("#closeSettings").addEventListener("click", closeSettings);
$("#settingsBackdrop").addEventListener("click", closeSettings);
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") { $("#connectModal").classList.add("hidden"); if ($("#settingsDrawer").classList.contains("open")) closeSettings(); }
});
wireCleanup();

// ---- boot ----
applyTheme();
setConn(false);
loadHistory().catch(() => {});
refreshStorage().catch(() => {});
connectWS();
