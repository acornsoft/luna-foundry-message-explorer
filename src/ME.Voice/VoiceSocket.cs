// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net.WebSockets;
using System.Text;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Voice;

/// <summary>A text WebSocket to the voice API. The key is used once, for the connect header, and never sent as a message.</summary>
public interface IVoiceSocket : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, RuntimeSecret apiKey, CancellationToken cancellationToken);

    Task SendAsync(string message, CancellationToken cancellationToken);

    /// <summary>The next text message, or null when the socket closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>The real socket: <see cref="ClientWebSocket"/> with <c>Authorization: Bearer</c> on the upgrade request.</summary>
public sealed class ClientVoiceSocket : IVoiceSocket
{
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _send = new(1, 1);

    public async Task ConnectAsync(Uri endpoint, RuntimeSecret apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey.Reveal());
        await _socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _send.Release();
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return result.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(message.ToArray()) : string.Empty;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }

        _socket.Dispose();
        _send.Dispose();
    }
}

/// <summary>Microphone capture and speaker playback, 24 kHz mono PCM16. Platform heads supply one.</summary>
public interface IVoiceAudio
{
    /// <summary>Null when audio works; otherwise why not (the panel then takes typed questions).</summary>
    string? UnavailableReason { get; }

    void StartCapture(Action<byte[]> onChunk);

    void StopCapture();

    void Play(byte[] pcm16);

    void StopPlayback();
}

/// <summary>No microphone or speaker: questions are typed and answers are shown as transcripts.</summary>
public sealed class NoVoiceAudio(string reason = "No audio device is set up in this build: type a question instead.") : IVoiceAudio
{
    public string? UnavailableReason { get; } = reason;

    public void StartCapture(Action<byte[]> onChunk)
    {
    }

    public void StopCapture()
    {
    }

    public void Play(byte[] pcm16)
    {
    }

    public void StopPlayback()
    {
    }
}
