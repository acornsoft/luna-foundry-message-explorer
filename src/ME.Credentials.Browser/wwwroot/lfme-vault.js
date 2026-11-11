// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Browser secret store for saved logins. The one small JS piece the web app uses (an approved exception).
// - One AES-GCM 256 key per key name, created with extractable: false. Only the CryptoKey object is kept
//   in IndexedDB; its bytes can't be read back by any script, this one included.
// - Each write gets a fresh 96-bit IV. The login ID is the associated data, so a record can't be moved
//   to another login. Only { v, iv, ct } goes to IndexedDB.
// - No localStorage, no sessionStorage, no console output, no network calls, no libraries.

const DB_NAME = "lfme-vault";
const DB_VERSION = 1;
const KEYS = "keys";
const RECORDS = "records";
const RECORD_VERSION = 1;
const IV_BYTES = 12;

let dbPromise = null;

function request(req) {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

function openDb() {
  if (!dbPromise) {
    dbPromise = new Promise((resolve, reject) => {
      const open = indexedDB.open(DB_NAME, DB_VERSION);
      open.onupgradeneeded = () => {
        const db = open.result;
        if (!db.objectStoreNames.contains(KEYS)) db.createObjectStore(KEYS);
        if (!db.objectStoreNames.contains(RECORDS)) db.createObjectStore(RECORDS);
      };
      open.onsuccess = () => resolve(open.result);
      open.onerror = () => reject(open.error);
      open.onblocked = () => reject(new Error("The vault database is blocked by another tab."));
    });
    dbPromise.catch(() => { dbPromise = null; });
  }
  return dbPromise;
}

async function store(name, mode, work) {
  const db = await openDb();
  const tx = db.transaction(name, mode);
  const done = new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve();
    tx.onerror = () => reject(tx.error);
    tx.onabort = () => reject(tx.error || new Error("The vault transaction was aborted."));
  });
  const [result] = await Promise.all([request(work(tx.objectStore(name))), done]);
  return result;
}

const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });

function requireText(value, what) {
  if (typeof value !== "string" || value.length === 0) throw new TypeError(`A ${what} is required.`);
}

async function loadKey(keyName) {
  requireText(keyName, "key name");
  const key = await store(KEYS, "readonly", (s) => s.get(keyName));
  if (!key) throw new Error("The vault key is missing. Call ensureKey first.");
  return key;
}

/** True when this page has WebCrypto (secure context) and IndexedDB. */
export async function isAvailable() {
  if (!globalThis.isSecureContext || !globalThis.crypto?.subtle || !globalThis.indexedDB) return false;
  try {
    await openDb();
    return true;
  } catch {
    return false;
  }
}

/** Creates the non-extractable key once and keeps the CryptoKey in IndexedDB. */
export async function ensureKey(keyName) {
  requireText(keyName, "key name");
  const existing = await store(KEYS, "readonly", (s) => s.get(keyName));
  if (existing) return;
  const key = await crypto.subtle.generateKey({ name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]);
  await store(KEYS, "readwrite", (s) => s.put(key, keyName));
}

/** Encrypts the secret for one login with a fresh IV and stores only the ciphertext. */
export async function encrypt(keyName, id, plaintext) {
  requireText(id, "login ID");
  if (typeof plaintext !== "string") throw new TypeError("The secret must be text.");
  const key = await loadKey(keyName);
  const iv = crypto.getRandomValues(new Uint8Array(IV_BYTES));
  const data = encoder.encode(plaintext);
  try {
    const ct = await crypto.subtle.encrypt({ name: "AES-GCM", iv, additionalData: encoder.encode(id), tagLength: 128 }, key, data);
    await store(RECORDS, "readwrite", (s) => s.put({ v: RECORD_VERSION, iv, ct: new Uint8Array(ct) }, id));
  } finally {
    data.fill(0);
  }
}

/** Returns the secret for one login, or null when none is stored. Rejects when the record was changed or moved. */
export async function decrypt(keyName, id) {
  requireText(id, "login ID");
  const record = await store(RECORDS, "readonly", (s) => s.get(id));
  if (!record) return null;
  if (record.v !== RECORD_VERSION || !(record.iv instanceof Uint8Array) || record.iv.length !== IV_BYTES) {
    throw new Error("A saved secret in browser storage is damaged.");
  }
  const key = await loadKey(keyName);
  const data = await crypto.subtle.decrypt({ name: "AES-GCM", iv: record.iv, additionalData: encoder.encode(id), tagLength: 128 }, key, record.ct);
  const bytes = new Uint8Array(data);
  try {
    return decoder.decode(bytes);
  } finally {
    bytes.fill(0);
  }
}

/** Deletes the stored ciphertext for one login. Deleting a missing record is not an error. */
export async function remove(id) {
  requireText(id, "login ID");
  await store(RECORDS, "readwrite", (s) => s.delete(id));
}
