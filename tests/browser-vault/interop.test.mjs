// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// End to end: the C# BrowserSecretStore and CredentialVault, compiled to WebAssembly, call the real
// lfme-vault.js through [JSImport] in headless Chromium. Needs `dotnet publish interop-app -o <dir>` first;
// set LFME_INTEROP_DIR to that dir. Fake secrets only.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { existsSync } from "node:fs";
import { extname, join, normalize } from "node:path";
import { chromium } from "playwright";

const ROOT = process.env.LFME_INTEROP_DIR ? join(process.env.LFME_INTEROP_DIR, "wwwroot") : null;
const PLANTED = "PLANTED-not-a-real-secret-7c20";
const TYPES = { ".html": "text/html", ".js": "text/javascript", ".mjs": "text/javascript", ".wasm": "application/wasm", ".json": "application/json" };
const skip = (!ROOT || !existsSync(ROOT)) && !process.env.LFME_REQUIRE_INTEROP ? "set LFME_INTEROP_DIR to a published interop-app" : false;

let server, origin, browser;

before(async () => {
  if (skip) return;
  server = createServer(async (req, res) => {
    const path = normalize(decodeURIComponent(new URL(req.url, "http://x").pathname)).replace(/^([/\\])+/, "");
    const file = join(ROOT, path === "" ? "index.html" : path);
    if (!file.startsWith(ROOT) || !existsSync(file)) return res.writeHead(404).end();
    res.writeHead(200, { "content-type": TYPES[extname(file)] ?? "application/octet-stream" }).end(await readFile(file));
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = await chromium.launch();
});

after(async () => {
  await browser?.close();
  if (server) await new Promise((resolve) => server.close(resolve));
});

test("C# reaches the real module through [JSImport]: store, vault, delete, and a moved record is refused", { skip, timeout: 120_000 }, async () => {
  const context = await browser.newContext();
  const page = await context.newPage();
  const foreign = [];
  page.on("request", (r) => { if (!r.url().startsWith(origin)) foreign.push(r.url()); });
  await page.goto(origin + "/");
  await page.waitForFunction(() => globalThis.interopReady === true, null, { timeout: 60_000 });

  const report = Object.fromEntries((await page.evaluate((s) => runInterop(s), PLANTED)).split("\n").map((l) => l.split("=")));
  assert.equal(report.ready, "True");
  assert.equal(report.roundtrip, "True");
  assert.equal(report["missing-null"], "True");
  assert.equal(report["vault-roundtrip"], "True");
  assert.equal(report["vault-deleted"], "True");

  // Move the C# store's record to another login ID inside IndexedDB; the C# side must refuse it plainly.
  await page.evaluate(async () => {
    const db = await new Promise((resolve) => { const o = indexedDB.open("lfme-vault"); o.onsuccess = () => resolve(o.result); });
    const record = await new Promise((resolve) => { const r = db.transaction("records").objectStore("records").get("login-cs"); r.onsuccess = () => resolve(r.result); });
    await new Promise((resolve) => { const t = db.transaction("records", "readwrite"); t.objectStore("records").put(record, "login-moved"); t.oncomplete = resolve; });
    db.close();
  });
  assert.equal(await page.evaluate(([s]) => readOutcome("login-moved", s), [PLANTED]), "refused leak=False");
  assert.equal(await page.evaluate(([s]) => readOutcome("login-cs", s), [PLANTED]), "match");

  // Nothing went to web storage, the plaintext isn't in IndexedDB, and nothing left the page's origin.
  const dump = await page.evaluate(async () => {
    const db = await new Promise((resolve) => { const o = indexedDB.open("lfme-vault"); o.onsuccess = () => resolve(o.result); });
    const all = await new Promise((resolve) => { const r = db.transaction("records").objectStore("records").getAll(); r.onsuccess = () => resolve(r.result); });
    db.close();
    return { local: localStorage.length, session: sessionStorage.length, bytes: all.map((x) => Array.from(x.ct).concat(Array.from(x.iv))) };
  });
  assert.equal(dump.local, 0);
  assert.equal(dump.session, 0);
  const planted = Array.from(Buffer.from(PLANTED, "utf8"));
  for (const bytes of dump.bytes) assert.ok(!Buffer.from(bytes).includes(Buffer.from(planted)), "plaintext in a record");
  assert.deepEqual(foreign, []);
  await context.close();
});
