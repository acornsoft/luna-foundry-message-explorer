# Grok Voice (Voice panel)

The Voice panel (the mic at the top right) lets you ask about the connected namespace out loud, push-to-talk, and hear the answer from
Grok Voice. It is read-only: the voice model's only tools are the same four read-only tools the
[MCP server](mcp-server.md) offers (`list_entities`, `get_entity`, `check_limits`, `summarize_dead_letters`).
It can list and check counts. It cannot read message bodies, and it cannot send, receive, settle, move,
resubmit, purge or delete anything.

## Set up

1. **Saved logins > Add > xAI (Grok Voice) > xAI API key.** Paste the key. It is stored only in the platform
   secure store (Keychain, Windows Credential Manager, Secret Service, Android Keystore), never in the config
   JSON, never shown again, and never logged. The app reads it from the vault only when a session starts.
2. **Voice panel > Settings.** Pick a voice. The picker offers xAI's built-in voices only (custom, cloned voices
   are not offered). The choice is saved on the xAI login as a non-secret field. "Redact message bodies" is on
   by default.
3. Connect a namespace (Accounts or Saved logins), then **Start session**.
4. Hold **Hold to talk**, ask, and release. Or type a question and press **Ask**. On macOS the first hold asks
   for microphone permission.

## How it works

- **Sign-in.** When a session starts, the app reads the key from the vault and swaps it for a short-lived xAI
  token (`POST https://api.x.ai/v1/realtime/client_secrets` with `expires_after.seconds = 120`, answered with
  `value` and `expires_at`). The WebSocket then opens with only that token in its `Authorization: Bearer` header,
  so the long-lived key is never on the voice connection. The token only has to be valid when the socket opens.
  The panel says which was used. If xAI rejects the key (401 or 403), the session doesn't start. If xAI can't
  issue a token for another reason, the panel says so and connects with the key, as earlier builds did.
- WebSocket `wss://api.x.ai/v1/realtime?model=grok-voice-latest`. No message on the socket carries the key or
  the token.
- `session.update` sets the voice, instructions, `turn_detection: { type: null }` (manual turns, so audio is
  committed when you release the button), PCM16 at 24 kHz both ways, and the four tools as `function` tools.
- **Push-to-talk.** Holding the button stops any answer still playing (barge-in) and sends
  `input_audio_buffer.clear`. The microphone opens on first use (on macOS that's when the permission prompt
  appears) and its audio goes out as 100 ms `input_audio_buffer.append` chunks, in order. Releasing it sends the
  last partial chunk, waits for every chunk to be sent, then sends `input_audio_buffer.commit` and
  `response.create`.
- **Playback.** `response.output_audio.delta` audio is decoded and queued for the speaker. Up to 120 seconds are
  queued; beyond that the oldest audio is dropped. A sample split across two deltas is joined back together.
- **Typed fallback.** The question box works with or without audio. If there's no microphone or speaker, or
  one fails to open, the panel shows why, disables the talk button and keeps taking typed questions.
- When the model calls a tool (`response.function_call_arguments.done`), the app runs it against the
  connected namespace if it is one of the four, and returns a `function_call_output`. Any other name runs
  nothing and gets a refusal. Then it sends `response.create`.
- Transcripts (`response.output_audio_transcript.delta`, `conversation.item.input_audio_transcription.completed`)
  appear in the Conversation list. The tools used appear on the right.

## Audio devices

- **Library.** PortAudio (MIT-style) through PortAudioSharp2 (Apache-2.0), which ships prebuilt PortAudio for
  win-x64, linux-x64, linux-arm64, osx-x64 and osx-arm64. Notices are in `THIRD-PARTY-NOTICES.md`.
- **Format.** Mono, 16-bit, 24 kHz. If a device won't open at 24 kHz, it opens at its own default rate and the
  app resamples (linear) both ways.
- **macOS.** `Info.plist` has `NSMicrophoneUsageDescription`, and the hardened-runtime entitlements include
  `com.apple.security.device.audio-input`. Without the entitlement a signed build gets silence instead of a
  prompt.
- **Linux.** PortAudio needs the system's ALSA (`libasound2`) and JACK (`libjack0` or `libjack-jackd2-0`)
  libraries. If they're missing, the panel says so and takes typed questions.
- **Not covered.** Windows on Arm and Android have no PortAudio build here, so they use typed questions. The
  shared code (`ME.Voice.Audio`) has no native dependencies; a platform backend only implements
  `IAudioBackend`.

## Redaction

On by default. Before anything leaves the machine for the voice model:

- any `body`, `payload`, `data`, `properties`, `applicationProperties`, `userProperties` or `headers` field is
  replaced with `[redacted]`;
- every other string, including a typed question, goes through the PII net (`ME.Redaction`).

The tools return counts only, so this is a second line of defence. Turning it off sends tool results and typed
questions as they are.

## Limits

- **Not tested on real hardware in CI.** The audio pipeline (chunking, ordering, resampling, playback queue,
  barge-in, device failures) is tested against a fake device. CI machines have no sound card, so the PortAudio
  backend is only checked to load and answer its probe without throwing.
- No live xAI call is made in tests or CI. The protocol and the token exchange are tested against a fake socket
  and a fake HTTP handler.
- **The desktop app still holds the API key.** The short-lived token keeps the key off the voice connection,
  but the app reads the key from the vault to request the token. A server that issues tokens on the app's
  behalf would keep the key off the machine entirely; that is not part of this build.
- Tool results go back right away, without waiting for playback to finish, which xAI's docs suggest to avoid
  overlap.
- There's no keyboard shortcut for push-to-talk yet; use the button.
- Resampling is linear (good enough for speech, not for music).

## Sources (xAI public docs, checked November 10, 2026)

- [Voice overview and the built-in voice list](https://docs.x.ai/developers/model-capabilities/audio/voice) (updated October 22, 2026)
- [Speech to Speech: session parameters, audio formats, custom function tools](https://docs.x.ai/developers/model-capabilities/audio/speech-to-speech)
- [Voice API reference: realtime client and server messages, and `POST /v1/realtime/client_secrets`](https://docs.x.ai/developers/rest-api-reference/inference/voice)
