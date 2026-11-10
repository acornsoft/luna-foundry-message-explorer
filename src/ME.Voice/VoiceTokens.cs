// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;

namespace Acornsoft.MessageExplorer.Voice;

/// <summary>A short-lived voice token and when it stops working.</summary>
public sealed record VoiceToken(RuntimeSecret Value, DateTimeOffset ExpiresAt);

/// <summary>Swaps the long-lived API key for a short-lived token used to open the voice socket.</summary>
public interface IVoiceTokenMinter
{
    Task<VoiceToken> MintAsync(RuntimeSecret apiKey, CancellationToken cancellationToken);
}

/// <summary>xAI rejected the API key (401 or 403). Not retried with the key itself.</summary>
public sealed class VoiceKeyRejectedException(string message) : Exception(message);

/// <summary>
/// xAI ephemeral client secrets: <c>POST https://api.x.ai/v1/realtime/client_secrets</c> with
/// <c>{"expires_after":{"seconds":N}}</c>, answered with <c>{"value":"…","expires_at":unix}</c>. The token
/// only has to be valid when the socket opens, so the default lifetime is two minutes. The API key goes only
/// in this request's Authorization header; the socket then carries only the token.
/// </summary>
public sealed class XaiClientSecrets(HttpClient http, TimeSpan? lifetime = null, Uri? endpoint = null) : IVoiceTokenMinter
{
    public static readonly Uri DefaultEndpoint = new("https://api.x.ai/v1/realtime/client_secrets");
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);

    private static readonly Lazy<XaiClientSecrets> SharedInstance = new(() => new XaiClientSecrets(new HttpClient { Timeout = TimeSpan.FromSeconds(20) }));

    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>One HttpClient per process.</summary>
    public static XaiClientSecrets Shared => SharedInstance.Value;

    public TimeSpan Lifetime { get; } = lifetime is { } l && l > TimeSpan.Zero ? (l > TimeSpan.FromMinutes(60) ? TimeSpan.FromMinutes(60) : l) : DefaultLifetime;

    public async Task<VoiceToken> MintAsync(RuntimeSecret apiKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        var body = new JsonObject { ["expires_after"] = new JsonObject { ["seconds"] = (int)Lifetime.TotalSeconds } };
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint ?? DefaultEndpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Reveal());
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new VoiceKeyRejectedException($"xAI did not accept the saved API key ({(int)response.StatusCode}). Check it in Saved logins > xAI (Grok Voice).");
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"xAI could not issue a short-lived voice token ({(int)response.StatusCode}).", null, response.StatusCode);

        try
        {
            var json = JsonNode.Parse(text) as JsonObject;
            var value = json?["value"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
                throw new HttpRequestException("xAI answered without a voice token.");
            var expires = json?["expires_at"]?.GetValue<long>() is { } unix and > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix)
                : DateTimeOffset.UtcNow + Lifetime;
            return new VoiceToken(new RuntimeSecret(value), expires);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new HttpRequestException("xAI answered with something that is not a voice token: " + SecretRedactor.Redact(exception.Message, apiKey));
        }
    }
}
