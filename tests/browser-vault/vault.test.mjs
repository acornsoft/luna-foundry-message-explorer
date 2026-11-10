// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Loads the real lfme-vault.js into headless Chromium (served from 127.0.0.1, a secure context) and checks
// it against real WebCrypto and real IndexedDB. Fake secrets only.
import { test, before, after, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright";

const MODULE = fileURLToPath(new URL("../../src/ME.Credentials.Browser/wwwroot/lfme-vault.js", import.meta.url));
const PLANTED = "PLANTED-not-a-real-secret-b81d";
const KEY = "lfme-logins";

let server, origin, browser, context, page, requests, consoleLines;

before(async () => {
  const source = await readFile(MODULE, "utf8");
  server = createServer((req, res) => {
    if (req.url === "/lfme-vault.js") {
      res.writeHead(200, { "content-type": "text/javascript" }).end(source);
    } else if (req.url === "/") {
      res.writeHead(200, { "content-type": "text/html" }).end("<!doctype html><title>vault test</title>");
    } else {
      res.writeHead(404).end();
    }
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  origin = `http://127.0.0.1:${server.address().port}`;
  browser = await chromium.launch();
});

after(async () => {
  await browser?.close();
  await new Promise((resolve) => server.close(resolve));
});

beforeEach(async () => {
  context = await browser.newContext(); // a fresh profile: empty IndexedDB
  page = await context.newPage();
  requests = [];
  consoleLines = [];
  page.on("request", (r) => requests.push(r.url()));
  page.on("console", (m) => consoleLines.push(m.text()));
  await page.goto(origin + "/");
  await page.evaluate(async (url) => { globalThis.vault = await import(url); }, origin + "/lfme-vault.js");
});

afterEach(async () => {
  // The module never logs and never calls out: the only requests are the page and the module itself.
  assert.deepEqual(consoleLines, []);
  assert.deepEqual(requests, [origin + "/", origin + "/lfme-vault.js"]);
  await context.close();
});

/** Every value in the vault database, with byte arrays as hex and keys described. */
function dumpDb() {
  return page.evaluate(async () => {
    const db = await new Promise((resolve, reject) => {
      const open = indexedDB.open("lfme-vault");
      open.onsuccess = () => resolve(open.result);
      open.onerror = () => reject(open.error);
    });
    const hex = (b) => Array.from(new Uint8Array(b.buffer ?? b, b.byteOffset ?? 0, b.byteLength), (x) => x.toString(16).padStart(2, "0")).join("");
    const out = {};
    for (const name of db.objectStoreNames) {
      const tx = db.transaction(name, "readonly");
      const [keys, values] = await Promise.all(["getAllKeys", "getAll"].map((m) => new Promise((resolve, reject) => {
        const r = tx.objectStore(name)[m]();
        r.onsuccess = () => resolve(r.result);
        r.onerror = () => reject(r.error);
      })));
      out[name] = keys.map((k, i) => {
        const v = values[i];
        if (v instanceof CryptoKey) return { id: k, cryptoKey: { type: v.type, extractable: v.extractable, usages: v.usages, algorithm: v.algorithm } };
        return { id: k, v: v.v, iv: hex(v.iv), ct: hex(v.ct) };
      });
    }
    db.close();
    return { stores: out, local: localStorage.length, session: sessionStorage.length };
  });
}

const hexOf = (text) => Buffer.from(text, "utf8").toString("hex");

test("a secret round-trips, and a missing login reads as null", async () => {
  const result = await page.evaluate(async ([key, secret]) => {
    if (!(await vault.isAvailable())) throw new Error("not available");
    await vault.ensureKey(key);
    await vault.ensureKey(key); // idempotent
    await vault.encrypt(key, "login-a", secret);
    await vault.encrypt(key, "login-u", "ünïcødé ✓ fake");
    return [await vault.decrypt(key, "login-a"), await vault.decrypt(key, "login-u"), await vault.decrypt(key, "login-none")];
  }, [KEY, PLANTED]);
  assert.deepEqual(result, [PLANTED, "ünïcødé ✓ fake", null]);
});

test("the key is non-extractable: exportKey rejects for every format", async () => {
  await page.evaluate(async (key) => { await vault.ensureKey(key); }, KEY);
  const { stores } = await dumpDb();
  assert.equal(stores.keys.length, 1);
  assert.deepEqual(stores.keys[0].cryptoKey, { type: "secret", extractable: false, usages: ["encrypt", "decrypt"], algorithm: { name: "AES-GCM", length: 256 } });
  const outcomes = await page.evaluate(async (keyName) => {
    const db = await new Promise((resolve) => { const o = indexedDB.open("lfme-vault"); o.onsuccess = () => resolve(o.result); });
    const key = await new Promise((resolve) => { const r = db.transaction("keys").objectStore("keys").get(keyName); r.onsuccess = () => resolve(r.result); });
    const results = [];
    for (const format of ["raw", "jwk"]) {
      try { await crypto.subtle.exportKey(format, key); results.push(`${format}: exported`); } catch (e) { results.push(`${format}: ${e.name}`); }
    }
    db.close();
    return results;
  }, KEY);
  assert.deepEqual(outcomes, ["raw: InvalidAccessError", "jwk: InvalidAccessError"]);
});

test("every write gets a fresh 96-bit IV, even for the same login and the same secret", async () => {
  const ivs = await page.evaluate(async ([key, secret]) => {
    await vault.ensureKey(key);
    const seen = [];
    const read = (id) => new Promise((resolve) => {
      const o = indexedDB.open("lfme-vault");
      o.onsuccess = () => { const r = o.result.transaction("records").objectStore("records").get(id); r.onsuccess = () => { o.result.close(); resolve(r.result); }; };
    });
    for (let i = 0; i < 40; i++) {
      await vault.encrypt(key, i % 2 ? "login-same" : `login-${i}`, secret);
      const rec = await read(i % 2 ? "login-same" : `login-${i}`);
      seen.push({ iv: Array.from(rec.iv).join(","), len: rec.iv.length, ct: Array.from(rec.ct).join(",") });
    }
    return seen;
  }, [KEY, PLANTED]);
  assert.equal(ivs.length, 40);
  assert.ok(ivs.every((x) => x.len === 12));
  assert.equal(new Set(ivs.map((x) => x.iv)).size, 40, "IVs repeat");
  assert.equal(new Set(ivs.map((x) => x.ct)).size, 40, "ciphertexts repeat for the same secret");
});

test("delete removes the record, and deleting a missing one is fine", async () => {
  const result = await page.evaluate(async ([key, secret]) => {
    await vault.ensureKey(key);
    await vault.encrypt(key, "login-a", secret);
    await vault.encrypt(key, "login-b", secret);
    await vault.remove("login-a");
    await vault.remove("login-never-saved");
    return [await vault.decrypt(key, "login-a"), await vault.decrypt(key, "login-b")];
  }, [KEY, PLANTED]);
  assert.deepEqual(result, [null, PLANTED]);
  const { stores } = await dumpDb();
  assert.deepEqual(stores.records.map((r) => r.id), ["login-b"]);
  assert.equal(stores.keys.length, 1, "delete leaves the key alone");
});

test("the plaintext appears in no IndexedDB record, and nothing goes to localStorage or sessionStorage", async () => {
  await page.evaluate(async ([key, secret]) => {
    await vault.ensureKey(key);
    for (const id of ["login-a", "login-b", "login-c"]) await vault.encrypt(key, id, `${secret}-${id}`);
  }, [KEY, PLANTED]);
  const dump = await dumpDb();
  const text = JSON.stringify(dump);
  assert.equal(dump.stores.records.length, 3);
  assert.ok(dump.stores.records.every((r) => r.v === 1 && r.iv.length === 24 && r.ct.length === 2 * (PLANTED.length + "-login-a".length + 16)));
  assert.ok(!text.includes(PLANTED), "plaintext found as text");
  assert.ok(!text.includes(hexOf(PLANTED)), "plaintext found as bytes");
  assert.ok(!text.includes(hexOf(PLANTED.slice(0, 8))), "plaintext prefix found as bytes");
  assert.equal(dump.local, 0);
  assert.equal(dump.session, 0);
});

test("a changed record or a record moved to another login is refused", async () => {
  const outcomes = await page.evaluate(async ([key, secret]) => {
    await vault.ensureKey(key);
    await vault.encrypt(key, "login-a", secret);
    const db = await new Promise((resolve) => { const o = indexedDB.open("lfme-vault"); o.onsuccess = () => resolve(o.result); });
    const get = (id) => new Promise((resolve) => { const r = db.transaction("records").objectStore("records").get(id); r.onsuccess = () => resolve(r.result); });
    const put = (id, v) => new Promise((resolve) => { const t = db.transaction("records", "readwrite"); t.objectStore("records").put(v, id); t.oncomplete = resolve; });
    const record = await get("login-a");
    await put("login-moved", record);
    const flipped = { ...record, ct: record.ct.slice() };
    flipped.ct[0] ^= 1;
    await put("login-flipped", flipped);
    db.close();
    const outcome = async (id) => { try { return `read: ${await vault.decrypt(key, id) === null ? "null" : "value"}`; } catch (e) { return e.name; } };
    return [await outcome("login-moved"), await outcome("login-flipped"), await outcome("login-a")];
  }, [KEY, PLANTED]);
  assert.deepEqual(outcomes, ["OperationError", "OperationError", "read: value"]);
});
