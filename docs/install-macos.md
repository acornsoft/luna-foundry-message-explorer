# Install on macOS

Gotham Savings and Loan (GSL) work. The desktop app ships as a DMG, one per Mac type:

| Mac | DMG |
|---|---|
| Apple Silicon (M1 or later, including a current Mac mini) | `LunaFoundryMessageExplorer-<version>-arm64.dmg` |
| Intel | `LunaFoundryMessageExplorer-<version>-x64.dmg` |

macOS 12 (Monterey) or later. The app is self-contained: no .NET install is needed.

## Install

1. Open the DMG.
2. Drag **Luna Foundry Message Explorer** onto the **Applications** folder.
3. Eject the DMG.

## First launch

Today's builds are ad-hoc signed. They are not signed with a Developer ID and not notarized, so macOS
blocks a plain double-click the first time. Do one of these once:

- In Finder, open Applications, **right-click** (or Control-click) the app, choose **Open**, then **Open** again.
  On macOS 15 and later, if there is no Open button, go to **System Settings > Privacy & Security** and
  choose **Open Anyway**.
- Or, in Terminal:

  ```bash
  xattr -dr com.apple.quarantine "/Applications/Luna Foundry Message Explorer.app"
  ```

After that it opens normally.

## What you can do

- **Test jobs** tab: the bundled sample collections are listed under "Or open a bundled sample", and the
  folder picker starts in them. `erp-standin` opens at startup and needs nothing else. `crew-stock` and
  `python-hello` have a Python plugin, so they need `python3` (macOS asks to install the Command Line Tools
  the first time). Test jobs run against the in-memory broker only.
- **Accounts** tab (opens first): **Add account** opens your browser to sign in with Microsoft Entra ID.
  Add more accounts the same way. Every Service Bus namespace each account can read shows up, across all
  of its tenants and subscriptions. Double-click one to open its queues and topics. No Azure CLI needed.
- **Queues** and **Topics** tabs: read-only grids for the namespace you opened.
  - **Connection string**: paste any flavor (namespace key, entity key, SAS token, endpoint only, or the
    emulator). The panel says which one it found. Tick **Save to Keychain** to keep it; the secret goes only
    into your login Keychain, never a file.
  - **Microsoft Entra ID**: type a namespace and pick which signed-in account to use.
- **Saved logins** tab: every saved login (Service Bus, Event Hubs, Kafka, RabbitMQ, AWS, the emulator and
  your Entra accounts) with Add, Edit, Rename, Delete and a read-only Test connection. Secrets are kept in
  the Keychain under `LunaFoundryMessageExplorer.logins`, masked, and never shown or copied.

## First sign-in: what you approve

- The sign-in page belongs to Microsoft. The app uses the public **Microsoft Azure CLI** application
  (client ID `04b07795-8ddb-461a-bbee-02f9e1bf7b46`). Most tenants already allow it, so there is usually no
  consent prompt. If your tenant blocks it, ask an admin to allow it, or register your own public client
  app (redirect URI `http://localhost`) and set `LFME_ENTRA_CLIENT_ID` to its ID.
- The app asks only for Azure Resource Manager access (`https://management.azure.com/.default`, which is
  `user_impersonation` on Azure Service Management) and only makes GET calls with it.
- To see a namespace, your account needs at least the **Reader** role on it, its resource group, or its
  subscription.
- Other tenants that need MFA or their own consent show an error row under the account until you sign in to
  them. Type that tenant in **Tenant (optional)** and add the account again.
- macOS asks whether the app may use the Keychain the first time it saves the token cache (`lfme`) or a
  connection string. Choose **Always Allow**.
- Login and account records (no secrets, no tokens) are in
  `~/Library/Application Support/LunaFoundryMessageExplorer/logins.json`. Strings saved by 0.6.0 move from
  the old `saved-connections` Keychain item into Saved logins on first launch.

## Build the DMGs yourself

```bash
eng/package-macos.sh                 # both architectures, into artifacts/dist
eng/package-macos.sh --rid osx-arm64 # one architecture
eng/macos/verify-dmg.sh artifacts/dist/LunaFoundryMessageExplorer-0.6.1-arm64.dmg arm64
```

On a Mac it uses `codesign` and `hdiutil`. On Linux, first run `eng/macos/install-linux-tools.sh`
(plus `apt-get install hfsprogs zlib1g-dev libbz2-dev`), then set `LFME_MAC_TOOLS=.tools/macos`.
CI builds the DMGs both ways, launches the app on a Mac runner, and keeps the DMGs as workflow artifacts.

## Developer ID signing and notarization (ready, not used yet)

When these environment variables are set, `eng/package-macos.sh` signs with a Developer ID instead of
ad-hoc, using `rcodesign` on Linux or macOS:

| Variable | Value |
|---|---|
| `LFME_MAC_P12_PATH` | the "Developer ID Application" certificate and key, exported as .p12 |
| `LFME_MAC_P12_PASSWORD` | its password |
| `LFME_ASC_KEY_PATH` | App Store Connect API key (.p8), for notarization |
| `LFME_ASC_KEY_ID` | its key ID |
| `LFME_ASC_ISSUER_ID` | its issuer ID |

With the first two, the app is signed with the hardened runtime, a secure timestamp and
`eng/macos/entitlements.plist` (JIT and native-library entitlements that .NET needs), and then the DMG is
signed. With all five, the DMG is also notarized and the ticket stapled (`rcodesign notary-submit --staple`).
Secrets are passed through files in a private temp folder and are never printed. Keep them out of the repo;
in CI they would be repository secrets.

If rcodesign says the .p12 password is wrong, the export may use a newer cipher. Re-export it from
Keychain Access, or convert it with OpenSSL using `-certpbe PBE-SHA1-3DES -keypbe PBE-SHA1-3DES -macalg sha1`.

## Limits

- Remove and Sign out forget an account in the app, but its tokens stay in the `lfme` Keychain item until
  they expire. Delete that item in Keychain Access to clear every account at once.
- Until a Developer ID build exists, the first launch needs the steps above.
- There are two DMGs, not one universal app. .NET self-contained runtimes are per architecture.
- The Linux-built DMG is checked on a real Mac in CI, and the DMG built on the Mac is the reference.
