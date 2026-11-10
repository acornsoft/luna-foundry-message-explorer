# Credential vault: saved logins on every platform

Gotham Savings and Loan (GSL) needs one safe place for the logins it uses with every system the app talks
to. This page describes that place: one contract, one backend per platform, and what each backend has
actually been proven to do.

## The contract

`ICredentialVault` (library `ME.Credentials`, no UI and no Azure dependency) keeps a list of logins. Each
login is `{ id, displayName, system, kind, non-secret fields, secret }`:

| System | Kinds | Secret |
|---|---|---|
| Microsoft Entra ID | Entra account (user name, tenant, `AuthenticationRecord`) | none (tokens stay in the MSAL `lfme` cache) |
| Azure Service Bus | Connection string or SAS token; namespace with an Entra account | the string or token; none |
| Azure Event Hubs | Connection string or SAS token; namespace with an Entra account | the string or token; none |
| Apache Kafka | SASL (PLAIN, SCRAM-SHA-256, SCRAM-SHA-512) user name; mutual TLS (certificate, key and CA paths) | the SASL secret; the key's passphrase, if any |
| RabbitMQ | Host, port, virtual host, user name | the user's secret |
| AWS SQS and SNS | Access key ID and region; or a named profile or SSO profile | the secret access key; none |
| Service Bus emulator | Emulator connection string | the string |

- **Secrets** go only to the platform's secure store (`ISecretStore`), under the login's ID. Nothing else
  ever holds them: not the config file, not a log, not an exception message, not `ToString`, not the
  command line.
- **Everything else** goes in `logins.json` in the config folder, written atomically. Example (fake):

  ```json
  { "version": 1, "logins": [ { "id": "login-3f9a0c1b2d4e5f60", "displayName": "Kafka dev (demo)",
    "system": "Kafka", "kind": "KafkaSasl", "fields": { "bootstrapServers": "kafka-demo.contoso.example:9093",
    "mechanism": "SCRAM-SHA-512", "username": "demo-reader" }, "hasSecret": true, "created": "…", "lastUsed": null } ] }
  ```

- **Validation before any write.** A field the kind doesn't know, a missing required field, a field that
  contains the secret, a field shaped like a secret (a key, a signature, a private key block), or an
  Entra-based kind without an account are all refused with a plain message. Nothing is written.
- **Fail closed.** When the platform has no secure store, a login with a secret is refused ("no secure
  store") and nothing is written anywhere. Logins with no secret (Entra namespaces, AWS profiles) still save.
- **Account binding.** A login that signs in with Entra records the account it uses (`accountId`, plus the
  user name for display). The Connect panel and the login editor let you pick any signed-in account, so a
  typed namespace or an endpoint-only string no longer has to use the first account.
- **The vault's log** carries login IDs, systems and kinds only. `SecretRedactor` masks a secret (plain or
  URL-encoded) as `***` in any message built from broker or SDK errors.
- **Async by design**, because the browser's crypto API is async-only.

## One backend per platform, chosen at startup

`SecretStores.ForCurrentPlatform()` picks the backend. Each one sits behind a small native-API interface,
so the vault logic is tested against fakes and the native calls are tested on real machines in CI.

| Platform | Store | How | Protection | Proven | Not yet proven |
|---|---|---|---|---|---|
| macOS | Keychain | `SecItemAdd`, `SecItemCopyMatching`, `SecItemUpdate`, `SecItemDelete` (Security framework, P/Invoke), generic password items under service `LunaFoundryMessageExplorer.logins` | OS store, tied to the user's login keychain | Unit tests with a fake keychain; integration test on the `macos-latest` CI runner (add, read, replace, delete, and the vault on the real Keychain with no secret in any file) | The "Always Allow" prompt flow on a signed, notarised build (the CI runner's keychain doesn't prompt) |
| iOS | Keychain | The same code; items use `kSecAttrAccessibleWhenUnlockedThisDeviceOnly` (not backed up, not moved to another device) | OS store, device-bound | Unit tests with a fake keychain, including the accessibility attribute | Anything on a device or simulator: there is no iOS app head yet |
| Windows | Credential Manager | `CredWriteW`, `CredReadW`, `CredDeleteW` (advapi32), generic credentials, `CRED_PERSIST_LOCAL_MACHINE` (never roams); Windows encrypts them with DPAPI for the current user | OS store, per user | Unit tests with a fake; integration test on the `windows-latest` CI runner | Very long secrets: Credential Manager caps a secret at 2,560 bytes, and the app refuses longer ones with a plain message |
| Linux | Secret Service (GNOME Keyring, KWallet's Secret Service, and others) | libsecret (`secret_password_storev_sync` and friends) over D-Bus, in the default collection, with attributes `application`, `service`, `login` | OS store, unlocked with the user's session | Unit tests with a fake; integration test on `ubuntu-latest` against GNOME Keyring in a private D-Bus session; fail closed proven on the build box (no D-Bus session: the store reports "No Secret Service keyring is running" and saving is refused) | KWallet and other Secret Service providers |
| Web (Avalonia browser) | IndexedDB, encrypted | AES-GCM with a **non-extractable** WebCrypto key kept in IndexedDB; a fresh 12-byte IV per write; the login ID as associated data, so a record can't be moved to another login | **Browser-protected (weaker than OS store)**, and the UI says so. Anyone who can run script in the page's origin can ask the browser to decrypt | Unit tests against a fake crypto bridge (real AES-GCM): round trip, tamper detection, wrong-login detection, nothing stored in plain text | Everything in a real browser: the script bridge isn't written (see below), and there is no browser app head yet |

Entra on the web should not store anything long-lived: sign in with an MSAL-style flow and keep tokens in
memory or session storage only. Raw secrets never go in `localStorage`.

## The app

- **Saved logins** tab: one row per login with a system badge, the name, the kind and when it was last
  used. Add, Edit, Rename, Delete (asks first) and Test connection. Test is read-only: for Service Bus and
  the emulator it lists queues; for systems whose provider isn't built yet it says so.
- Secrets are entered in a masked box with no reveal button; copy and cut are blocked. When you edit a
  login, leaving the secret empty keeps the stored one. There is no way to read a secret back in the UI.
- Entra accounts appear in the list as read-only rows; add, sign out and remove them on the Accounts tab.
- The store in use is shown at the top of the tab. A browser store is labelled
  "browser-protected (weaker than OS store)"; no store at all shows the reason and that secret logins
  can't be saved.

## Migration from 0.6.0

- **Saved connection strings** (one secure-store item holding every string saved with "Save to
  Keychain") are imported as Service Bus or emulator logins on first launch, then the old item is cleared.
  If there is no secure store, they are left where they are.
- **Account files** (`accounts/*.json`) are moved into the vault as Entra account logins, then deleted.

## Follow-ups (not in this change)

- **iOS and browser app heads.** The backends exist behind the contract and are unit-tested, but there is
  no iOS or WebAssembly app project yet.
- **The browser crypto bridge.** `IBrowserCryptoInterop` needs a small script module that calls WebCrypto
  and IndexedDB. The project's no-script rule rules it out today, so this needs a decision first.
- **Server-side Key Vault** as an option for the web: later, not built.
- **Removing one account from the MSAL `lfme` token cache** (Azure.Identity has no call for it).

## Out of scope

- **Sync across devices.** Logins live on one device. Nothing is synced or backed up by the app, and iOS
  items are marked this-device-only so the OS doesn't move them either.
