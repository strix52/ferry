import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test, { after, before } from "node:test";

const port = 18000 + Math.floor(Math.random() * 1000);
const dataDir = await mkdtemp(path.join(os.tmpdir(), "ferry-pins-"));
let server;
let serverError = "";

async function waitForServer() {
  const deadline = Date.now() + 10_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/api/info`);
      if (response.ok) return;
    } catch {}
    if (serverError) throw new Error(`temporary Ferry server failed: ${serverError}`);
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error("temporary Ferry server did not start");
}

async function api(pathname, options = {}) {
  const response = await fetch(`http://127.0.0.1:${port}${pathname}`, options);
  const body = await response.json();
  return { response, body };
}

before(async () => {
  server = spawn(process.execPath, ["--no-warnings", "server.js"], {
    cwd: process.cwd(),
    env: { ...process.env, PORT: String(port), FERRY_DATA_DIR: dataDir },
    stdio: ["ignore", "ignore", "pipe"],
  });
  server.stderr.on("data", (chunk) => { serverError += chunk.toString(); });
  await waitForServer();
});

after(async () => {
  if (server && !server.killed) {
    const exited = new Promise((resolve) => server.once("exit", resolve));
    server.kill();
    await exited;
  }
  await rm(dataDir, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
});

test("pins are shared, ordered, and capped at five items", async () => {
  const ids = [];
  for (let index = 0; index < 6; index += 1) {
    const { response, body } = await api("/api/messages", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ text: `message ${index + 1}`, senderId: "test", senderName: "Test" }),
    });
    assert.equal(response.status, 200);
    ids.push(body.id);
  }

  for (const id of ids) {
    const { response } = await api(`/api/messages/${id}/pin`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pinned: true }),
    });
    assert.equal(response.status, 200);
  }

  const { response, body: pins } = await api("/api/pins");
  assert.equal(response.status, 200);
  assert.equal(pins.length, 5);
  assert.deepEqual(pins.map((pin) => pin.id), ids.slice(1).reverse());
});
