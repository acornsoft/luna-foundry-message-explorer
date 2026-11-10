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
- **Queues** and **Topics** tabs: connect to a real Azure Service Bus namespace, read-only.
  - **Connection string**: paste it at runtime. It is held in memory only and never saved.
  - **Microsoft Entra ID**: uses `DefaultAzureCredential`. On the Mac, sign in once with the Azure CLI
    (`brew install azure-cli`, then `az login`); the app picks that sign-in up. Your account needs a
    management read role on the namespace.

## Build the DMGs yourself

```bash
eng/package-macos.sh                 # both architectures, into artifacts/dist
eng/package-macos.sh --rid osx-arm64 # one architecture
eng/macos/verify-dmg.sh artifacts/dist/LunaFoundryMessageExplorer-0.5.0-arm64.dmg arm64
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

- Until a Developer ID build exists, the first launch needs the steps above.
- There are two DMGs, not one universal app. .NET self-contained runtimes are per architecture.
- The Linux-built DMG is checked on a real Mac in CI, and the DMG built on the Mac is the reference.
