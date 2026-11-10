<!-- SPDX-License-Identifier: MPL-2.0 -->
# Environments: sovereign clouds and more platforms

Message Explorer works with any Service Bus queue, topic or subscription. This page covers where it runs
and which Azure clouds an account can sign in to.

## Azure clouds, per account

Each Entra account picks its cloud when it is added: **Azure (public)**, **Azure US Government** or
**Azure China (21Vianet)**. The account keeps that cloud. Signing in again, silent sign-in on launch,
namespace discovery and opening a namespace all use the account's own cloud, whatever the picker shows.

| Cloud | Sign-in host | Resource Manager | Namespace suffix |
| --- | --- | --- | --- |
| Azure (public) | `login.microsoftonline.com` | `management.azure.com` | `.servicebus.windows.net` |
| Azure US Government | `login.microsoftonline.us` | `management.usgovcloudapi.net` | `.servicebus.usgovcloudapi.net` |
| Azure China (21Vianet) | `login.chinacloudapi.cn` | `management.chinacloudapi.cn` | `.servicebus.chinacloudapi.cn` |

- Browser sign-in, **Copy sign-in link** and **Sign in with a code** (device code) all go to the account's
  cloud. The token cache is the same OS store as before; each cloud and tenant gets its own MSAL client.
- Accounts saved before clouds existed load as **Azure (public)**, so nothing changes for them.
- Pasted connection strings are not tied to a cloud: the namespace host in the string decides.

## Platforms

| Platform | What CI builds | How it is checked |
| --- | --- | --- |
| macOS arm64 and x64 | DMG (ad-hoc signed in CI) | packaged, verified and launched on a Mac runner |
| Windows x64 and arm64 | self-contained zip | published; app host must be a Windows executable; Skia and HarfBuzz present |
| Linux x64 and arm64 | self-contained tar.gz | published; app host must be ELF; Skia and HarfBuzz present |
| Android (arm64 phones, x64 emulators) | APK, debug-signed | built with the .NET 10 Android workload; runtime and Skia present for both ABIs |

Build one locally:

```bash
bash eng/platforms/package-desktop.sh win-arm64            # or osx-x64, win-x64, linux-x64, linux-arm64
ANDROID_SDK_ROOT=~/android-sdk JAVA_HOME=/path/to/jdk-17 bash eng/platforms/package-android.sh
```

### Android

The Android head (`src/ME.App.Android`) shows the phone and tablet shell: **Saved logins** and
**Accounts**, read-only. Move mode and plugin jobs are not offered on a phone. Saved secrets are sealed
with an AES-256-GCM key held in the Android Keystore; only the sealed blob is kept in app-private
storage, bound to its key name, and every write is refused when the Keystore key can't be used.

The head targets .NET 10 (`src/ME.App.Android/global.json`); the rest of the solution stays on .NET 8.
It is not in `MessageExplorer.sln`, so the desktop build needs no Android tooling.

To preview the phone shell on a desktop: `--phone-view` (add `--synthetic-demo` for made-up data).

## Limits

- Windows and Linux builds are compiled and checked, not launched in CI. macOS is launched.
- The APK is debug-signed, not store-signed, and has not been run on a device or emulator in CI.
- On Android, use **Sign in with a code**. Browser sign-in returns to a localhost listener, which Android
  apps cannot rely on; a custom redirect for Android is not built yet.
- US Government and China sign-in use the same public client ID as the public cloud. A tenant that
  blocks that client needs its own app registration (`LFME_ENTRA_CLIENT_ID`).
- Azure Germany and other retired clouds are not offered.
