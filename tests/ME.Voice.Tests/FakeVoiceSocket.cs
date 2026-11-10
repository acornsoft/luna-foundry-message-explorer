// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using System.Threading.Channels;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Voice.Tests;

/// <summary>Records what the session sends and lets a test play server events. No network.</summary>
public sealed class FakeVoiceSocket : IVoiceSocket
{
    private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();

    public Uri? Endpoint { get; private set; }

    public string? BearerKey { get; private set; }

    public List<string> Sent { get; } = [];

    public IEnumerable<JsonObject> SentJson => Sent.Select(s => (JsonObject)JsonNode.Parse(s)!);

    public IEnumerable<string> SentTypes => SentJson.Select(j => j["type"]!.GetValue<string>());

    public bool Disposed { get; private set; }

    public Task ConnectAsync(Uri endpoint, RuntimeSecret apiKey, CancellationToken cancellationToken)
    {
        Endpoint = endpoint;
        BearerKey = apiKey.Reveal();
        return Task.CompletedTask;
    }

    public Task SendAsync(string message, CancellationToken cancellationToken)
    {
        lock (Sent)
            Sent.Add(message);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken) => await _incoming.Reader.ReadAsync(cancellationToken);

    public void Push(string message) => _incoming.Writer.TryWrite(message);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _incoming.Writer.TryWrite(null);
        return ValueTask.CompletedTask;
    }
}
