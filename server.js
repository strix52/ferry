import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import os from "node:os";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";
import { DatabaseSync } from "node:sqlite";
import { exec } from "node:child_process";
import { WebSocketServer } from "ws";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const PORT = Number(process.env.PORT) || 8787;
// Remind once total stored file bytes cross this. Override with STORAGE_LIMIT_MB.
const STORAGE_LIMIT_BYTES = (Number(process.env.STORAGE_LIMIT_MB) || 2048) * 1024 * 1024;

const DATA_DIR = path.join(__dirname, "data");
const FILES_DIR = path.join(DATA_DIR, "files");
const PUBLIC_DIR = path.join(__dirname, "public");
fs.mkdirSync(FILES_DIR, { recursive: true });

const db = new DatabaseSync(path.join(DATA_DIR, "flow.db"));
db.exec(`
  CREATE TABLE IF NOT EXISTS messages (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    kind        TEXT NOT NULL,            -- 'text' | 'file'
    sender_id   TEXT NOT NULL,
    sender_name TEXT NOT NULL,
    text        TEXT,
    filename    TEXT,
    stored_name TEXT,
    size        INTEGER,
    deleted     INTEGER NOT NULL DEFAULT 0,
    created_at  INTEGER NOT NULL
  );
`);

const insertMsg = db.prepare(`
  INSERT INTO messages (kind, sender_id, sender_name, text, filename, stored_name, size, created_at)
  VALUES (?, ?, ?, ?, ?, ?, ?, ?)
`);
const getMsg = db.prepare(`SELECT * FROM messages WHERE id = ?`);
const listMsgs = db.prepare(`SELECT * FROM messages ORDER BY id ASC`);

// ---- websocket broadcast ----
const wss = new WebSocketServer({ noServer: true });
function broadcast(obj) {
  const data = JSON.stringify(obj);
  for (const c of wss.clients) {
    if (c.readyState === 1) c.send(data);
  }
}

function msgToClient(row) {
  return {
    id: row.id,
    kind: row.kind,
    senderId: row.sender_id,
    senderName: row.sender_name,
    text: row.text,
    filename: row.filename,
    size: row.size,
    deleted: !!row.deleted,
    createdAt: row.created_at,
  };
}

// ---- storage stats ----
function storageStats() {
  const rows = db.prepare(`SELECT COALESCE(SUM(size),0) AS bytes, COUNT(*) AS n
                           FROM messages WHERE kind='file' AND deleted=0`).get();
  const msgCount = db.prepare(`SELECT COUNT(*) AS n FROM messages`).get().n;
  return {
    fileBytes: rows.bytes,
    fileCount: rows.n,
    messageCount: msgCount,
    limitBytes: STORAGE_LIMIT_BYTES,
    overLimit: rows.bytes > STORAGE_LIMIT_BYTES,
  };
}

// ---- helpers ----
function send(res, status, body, headers = {}) {
  const data = typeof body === "string" ? body : JSON.stringify(body);
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", ...headers });
  res.end(data);
}

function readJsonBody(req) {
  return new Promise((resolve, reject) => {
    let raw = "";
    req.on("data", (c) => {
      raw += c;
      if (raw.length > 1e6) reject(new Error("body too large"));
    });
    req.on("end", () => {
      try { resolve(raw ? JSON.parse(raw) : {}); }
      catch (e) { reject(e); }
    });
    req.on("error", reject);
  });
}

const MIME = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
  ".json": "application/json; charset=utf-8",
};

function serveStatic(req, res, urlPath) {
  let rel = urlPath === "/" ? "/index.html" : urlPath;
  const filePath = path.normalize(path.join(PUBLIC_DIR, rel));
  if (filePath !== PUBLIC_DIR && !filePath.startsWith(PUBLIC_DIR + path.sep)) {
    return send(res, 403, { error: "forbidden" });
  }
  fs.readFile(filePath, (err, buf) => {
    if (err) return send(res, 404, { error: "not found" });
    res.writeHead(200, { "Content-Type": MIME[path.extname(filePath)] || "application/octet-stream" });
    res.end(buf);
  });
}

// ---- request router ----
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, `http://${req.headers.host}`);
  const p = url.pathname;

  try {
    if (req.method === "GET" && p === "/api/messages") {
      return send(res, 200, listMsgs.all().map(msgToClient));
    }

    if (req.method === "GET" && p === "/api/storage") {
      return send(res, 200, storageStats());
    }

    if (req.method === "GET" && p === "/api/info") {
      const ips = lanIPs().filter((ip) => !ip.startsWith("169.254."));
      const rank = (ip) =>
        ip.startsWith("192.168.") ? 0 : ip.startsWith("10.") ? 1 : ip.startsWith("172.") ? 2 : 3;
      ips.sort((a, b) => rank(a) - rank(b));
      const urls = ips.map((ip) => `http://${ip}:${PORT}`);
      return send(res, 200, { port: PORT, ips, urls, primary: urls[0] || null });
    }

    if (req.method === "POST" && p === "/api/messages") {
      const body = await readJsonBody(req);
      const text = (body.text || "").toString().trim();
      if (!text) return send(res, 400, { error: "empty" });
      const senderId = (body.senderId || "unknown").toString().slice(0, 64);
      const senderName = (body.senderName || "Device").toString().slice(0, 40);
      const now = Date.now();
      const info = insertMsg.run("text", senderId, senderName, text, null, null, null, now);
      const row = getMsg.get(info.lastInsertRowid);
      broadcast({ type: "message", message: msgToClient(row) });
      return send(res, 200, { ok: true, id: row.id });
    }

    if (req.method === "POST" && p === "/api/upload") {
      const filename = (url.searchParams.get("name") || "file").toString();
      const senderId = (url.searchParams.get("senderId") || "unknown").slice(0, 64);
      const senderName = (url.searchParams.get("senderName") || "Device").slice(0, 40);
      // strip path, filesystem-reserved, shell-significant, and control chars
      const safeName = path.basename(filename).replace(/[\u0000-\u001f\\/:*?"<>|]/g, "_") || "file";
      const storedName = `${Date.now()}_${crypto.randomBytes(6).toString("hex")}_${safeName}`;
      const dest = path.join(FILES_DIR, storedName);
      const out = fs.createWriteStream(dest);
      let bytes = 0;
      req.on("data", (c) => (bytes += c.length));
      req.pipe(out);
      out.on("error", () => send(res, 500, { error: "write failed" }));
      out.on("finish", () => {
        const now = Date.now();
        const info = insertMsg.run("file", senderId, senderName, null, safeName, storedName, bytes, now);
        const row = getMsg.get(info.lastInsertRowid);
        broadcast({ type: "message", message: msgToClient(row) });
        broadcast({ type: "storage", storage: storageStats() });
        send(res, 200, { ok: true, id: row.id });
      });
      return;
    }

    if (req.method === "GET" && p.startsWith("/api/download/")) {
      const id = Number(p.split("/").pop());
      const row = getMsg.get(id);
      if (!row || row.kind !== "file" || row.deleted) return send(res, 404, { error: "gone" });
      const fp = path.join(FILES_DIR, row.stored_name);
      if (!fs.existsSync(fp)) return send(res, 404, { error: "missing" });
      res.writeHead(200, {
        "Content-Type": "application/octet-stream",
        "Content-Disposition": `attachment; filename="${encodeURIComponent(row.filename)}"`,
        "Content-Length": row.size,
      });
      return fs.createReadStream(fp).pipe(res);
    }

    if (req.method === "POST" && p.startsWith("/api/open/")) {
      const id = Number(p.split("/").pop());
      const row = getMsg.get(id);
      if (!row || row.kind !== "file" || row.deleted) return send(res, 404, { error: "gone" });
      const fp = path.resolve(FILES_DIR, row.stored_name);
      if (!fs.existsSync(fp)) return send(res, 404, { error: "missing" });

      exec(`start "" "${fp}"`, (err) => {
        if (err) console.error("Failed to open file:", err);
      });
      return send(res, 200, { ok: true });
    }

    if (req.method === "POST" && p.startsWith("/api/reveal/")) {
      const id = Number(p.split("/").pop());
      const row = getMsg.get(id);
      if (!row || row.kind !== "file" || row.deleted) return send(res, 404, { error: "gone" });
      const fp = path.resolve(FILES_DIR, row.stored_name);
      if (!fs.existsSync(fp)) return send(res, 404, { error: "missing" });

      exec(`explorer.exe /select,"${fp}"`, (err) => {
        if (err) console.error("Failed to reveal file in explorer:", err);
      });
      return send(res, 200, { ok: true });
    }

    if (req.method === "POST" && p === "/api/cleanup") {
      const body = await readJsonBody(req);
      const days = Number(body.days);
      if (!Number.isFinite(days) || days < 0 || days > 3650) return send(res, 400, { error: "days must be between 0 and 3650" });
      const cutoff = Date.now() - days * 86400000;
      const rows = db.prepare(`SELECT * FROM messages WHERE kind='file' AND deleted=0 AND created_at < ?`).all(cutoff);
      let freed = 0;
      for (const r of rows) {
        const fp = path.join(FILES_DIR, r.stored_name);
        try { freed += r.size || 0; fs.rmSync(fp, { force: true }); } catch {}
        db.prepare(`UPDATE messages SET deleted=1 WHERE id=?`).run(r.id);
      }
      broadcast({ type: "cleanup", removed: rows.length });
      broadcast({ type: "storage", storage: storageStats() });
      return send(res, 200, { ok: true, removed: rows.length, freedBytes: freed });
    }

    return serveStatic(req, res, p);
  } catch (e) {
    return send(res, 500, { error: String(e && e.message || e) });
  }
});

server.on("upgrade", (req, socket, head) => {
  if (new URL(req.url, `http://${req.headers.host}`).pathname !== "/ws") {
    socket.destroy();
    return;
  }
  wss.handleUpgrade(req, socket, head, (ws) => wss.emit("connection", ws, req));
});

function lanIPs() {
  const out = [];
  for (const ifaces of Object.values(os.networkInterfaces())) {
    for (const i of ifaces || []) {
      if (i.family === "IPv4" && !i.internal) out.push(i.address);
    }
  }
  return out;
}

server.listen(PORT, "0.0.0.0", () => {
  console.log("\n  Ferry is running.\n");
  console.log(`  On this laptop:  http://localhost:${PORT}`);
  for (const ip of lanIPs()) console.log(`  On your phone:   http://${ip}:${PORT}`);
  console.log("\n  Open one of the phone URLs in your phone browser (same Wi-Fi) and bookmark it.");
  console.log("  Press Ctrl+C to stop.\n");
});
