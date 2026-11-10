// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Net;
using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.ReadTools;

namespace Acornsoft.MessageExplorer.Voice.Tests;

/// <summary>Short-lived xAI voice tokens. A fake HTTP handler; no network. Keys and tokens are built at run time.</summary>
public sealed class VoiceTokenTests
{
    private static readonly string FakeKey = "xai-" + Guid.NewGuid().ToString("N") + "-not-real";
    private static readonly string FakeToken = "xai-realtime-client-secret-" + Guid.NewGuid().ToString("N");

    private static (XaiClientSecrets Minter, FakeHandler Handler) Minter(HttpStatusCode status, string body, TimeSpan? lifetime = null)
    {
        var handler = new FakeHandler(status, body);
        return (new XaiClientSecrets(new HttpClient(handler), lifetime), handler);
    }

    [Fact]
    public async Task Minting_posts_the_lifetime_with_the_key_in_the_header_and_reads_the_token()
    {
        var (minter, handler) = Minter(HttpStatusCode.OK, $$"""{"value":"{{FakeToken}}","expires_at":1774274445}""");

        var token = await minter.MintAsync(new RuntimeSecret(FakeKey), CancellationToken.None);

        Assert.Equal(FakeToken, token.Value.Reveal());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1774274445), token.ExpiresAt);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(XaiClientSecrets.DefaultEndpoint, handler.Uri);
        Assert.Equal("Bearer " + FakeKey, handler.Authorization);
        Assert.Equal(120, JsonNode.Parse(handler.Body!)!["expires_after"]!["seconds"]!.GetValue<int>());
        Assert.DoesNotContain(FakeKey, handler.Body!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(7200, 3600)]
    [InlineData(-5, 120)]
    public void The_lifetime_is_kept_between_one_second_and_an_hour(int seconds, int expected)
    {
        var (minter, _) = Minter(HttpStatusCode.OK, "{}", TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected, (int)minter.Lifetime.TotalSeconds);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_rejected_key_says_so(HttpStatusCode status)
    {
        var (minter, _) = Minter(status, "{\"error\":\"bad key\"}");

        var error = await Assert.ThrowsAsync<VoiceKeyRejectedException>(() => minter.MintAsync(new RuntimeSecret(FakeKey), CancellationToken.None));

        Assert.Contains("Saved logins", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "{}")]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, "{\"value\":42}")]
    public async Task Other_failures_are_http_errors(HttpStatusCode status, string body)
    {
        var (minter, _) = Minter(status, body);

        await Assert.ThrowsAsync<HttpRequestException>(() => minter.MintAsync(new RuntimeSecret(FakeKey), CancellationToken.None));
    }

    [Fact]
    public async Task With_a_minter_only_the_short_lived_token_goes_on_the_socket()
    {
        var socket = new FakeVoiceSocket();
        var minter = new FakeMinter { Token = FakeToken };
        await using var session = Session(socket, minter);
        var problems = new List<string>();
        session.Problem += (_, p) => problems.Add(p);

        await session.StartAsync();

        Assert.Equal(FakeToken, socket.BearerKey);
        Assert.Equal(FakeKey, minter.KeySeen);
        Assert.True(session.UsesShortLivedToken);
        Assert.NotNull(session.TokenExpiresAt);
        Assert.Empty(problems);
        Assert.All(socket.Sent, s =>
        {
            Assert.DoesNotContain(FakeKey, s, StringComparison.Ordinal);
            Assert.DoesNotContain(FakeToken, s, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task If_no_token_can_be_had_the_session_says_so_and_uses_the_key()
    {
        var socket = new FakeVoiceSocket();
        var minter = new FakeMinter { Failure = new HttpRequestException("service unavailable for " + FakeKey) };
        await using var session = Session(socket, minter);
        var problems = new List<string>();
        session.Problem += (_, p) => problems.Add(p);

        await session.StartAsync();

        Assert.Equal(FakeKey, socket.BearerKey);
        Assert.False(session.UsesShortLivedToken);
        var problem = Assert.Single(problems);
        Assert.Contains("connecting with the API key", problem, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_key_does_not_connect_at_all()
    {
        var socket = new FakeVoiceSocket();
        await using var session = Session(socket, new FakeMinter { Failure = new VoiceKeyRejectedException("rejected") });

        await Assert.ThrowsAsync<VoiceKeyRejectedException>(() => session.StartAsync());

        Assert.Null(socket.Endpoint);
    }

    [Fact]
    public async Task Server_errors_that_echo_the_token_are_redacted()
    {
        var socket = new FakeVoiceSocket();
        await using var session = Session(socket, new FakeMinter { Token = FakeToken });
        var problems = new List<string>();
        session.Problem += (_, p) => problems.Add(p);
        await session.StartAsync();

        await session.HandleEventAsync(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "bad token " + FakeToken } }.ToJsonString());

        Assert.Equal("bad token ***", Assert.Single(problems));
    }

    private static GrokVoiceSession Session(FakeVoiceSocket socket, IVoiceTokenMinter minter) =>
        new(socket, ReadOnlyToolset.Over(new OpenedSource(new SyntheticNamespace(), "synthetic")), new VoiceOptions(),
            _ => Task.FromResult<RuntimeSecret?>(new RuntimeSecret(FakeKey)), minter);

    private sealed class FakeMinter : IVoiceTokenMinter
    {
        public string? Token { get; init; }

        public Exception? Failure { get; init; }

        public string? KeySeen { get; private set; }

        public Task<VoiceToken> MintAsync(RuntimeSecret apiKey, CancellationToken cancellationToken)
        {
            KeySeen = apiKey.Reveal();
            if (Failure is { } failure)
                throw failure;
            return Task.FromResult(new VoiceToken(new RuntimeSecret(Token!), DateTimeOffset.UtcNow.AddMinutes(2)));
        }
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
