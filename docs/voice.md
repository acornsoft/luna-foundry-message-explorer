# Grok Voice (Voice tab)

The Voice tab lets you ask about the connected namespace out loud, push-to-talk, and hear the answer from
Grok Voice. It is read-only: the voice model's only tools are the same four read-only tools the
[MCP server](mcp-server.md) offers (`list_entities`, `get_entity`, `check_limits`, `summarize_dead_letters`).
It can list and check counts. It cannot read message bodies, and it cannot send, receive, settle, move,
resubmit, purge or delete anything.

## Set up

1. **Saved logins > Add > xAI (Grok Voice) > xAI API key.** Paste the key. It is stored only in the platform
   secure store (Keychain, Windows Credential Manager, Secret Service, Android Keystore), never in the config
   JSON, never shown again, and never logged. The app reads it from the vault only when a session starts.
2. **Voice tab > Settings.** Pick a voice. The picker offers xAI's built-in voices only (custom, cloned voices
   are not offered). The choice is saved on the xAI login as a non-secret field. "Redact message bodies" is on
   by default.
3. Connect a namespace (Accounts or Saved logins), then **Start session**.
4. Hold **Hold to talk**, ask, and release. Or type a question and press **Ask**.

## How it works

- WebSocket `wss://api.x.ai/v1/realtime?model=grok-voice-latest`, with the key only in the
  `Authorization: Bearer` header of the upgrade request. No message on the socket carries it.
- `session.update` sets the voice, instructions, `turn_detection: { type: null }` (manual turns, so audio is
  committed when you release the button), PCM16 at 24 kHz both ways, and the four tools as `function` tools.
- Holding the button sends `input_audio_buffer.clear`, then `input_audio_buffer.append` chunks. Releasing it
  sends `input_audio_buffer.commit` and `response.create`.
- When the model calls a tool (`response.function_call_arguments.done`), the app runs it against the
  connected namespace if it is one of the four, and returns a `function_call_output`. Any other name runs
  nothing and gets a refusal. Then it sends `response.create`.
- Transcripts (`response.output_audio_transcript.delta`, `conversation.item.input_audio_transcription.completed`)
  appear in the Conversation list. The tools used appear on the right.

## Redaction

On by default. Before anything leaves the machine for the voice model:

- any `body`, `payload`, `data`, `properties`, `applicationProperties`, `userProperties` or `headers` field is
  replaced with `[redacted]`;
- every other string, including a typed question, goes through the PII net (`ME.Redaction`).

The tools return counts only, so this is a second line of defence. Turning it off sends tool results and typed
questions as they are.

## Limits

- **Audio devices.** The panel talks to an `IVoiceAudio` (24 kHz mono PCM16 capture and playback). This build
  ships no platform audio backend, so on every head the panel says so, the talk button stays disabled, and
  questions are typed; answers arrive as transcripts. A platform backend plugs into `IVoiceAudio` without other
  changes.
- No live xAI call is made in tests or CI. The protocol is tested against a fake socket.
- The API key is used directly from the desktop app. xAI recommends ephemeral tokens
  (`POST /v1/realtime/client_secrets`) for client apps; minting one per session is a follow-up.
- Tool results go back right away, without waiting for playback to finish, which xAI's docs suggest to avoid
  overlap.

## Sources (xAI public docs, checked November 10, 2026)

- [Voice overview and the built-in voice list](https://docs.x.ai/developers/model-capabilities/audio/voice) (updated October 22, 2026)
- [Speech to Speech: session parameters, audio formats, custom function tools](https://docs.x.ai/developers/model-capabilities/audio/speech-to-speech)
- [Voice API reference: realtime client and server messages](https://docs.x.ai/developers/rest-api-reference/inference/voice)
