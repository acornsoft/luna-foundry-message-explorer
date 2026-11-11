// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Jobs.Live;
using Acornsoft.MessageExplorer.Operations.ServiceBus;
using Azure.Messaging.ServiceBus;

namespace Acornsoft.MessageExplorer.Operations.Tests;

/// <summary>A sender that keeps what it is given. No network.</summary>
internal sealed class CapturingSender(string entity) : ServiceBusSender
{
    public List<ServiceBusMessage> Messages { get; } = [];

    public override string EntityPath => entity;

    public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        Messages.Add(message);
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A client that hands out capturing senders. Creating it opens no connection.</summary>
internal sealed class CapturingClient : ServiceBusClient
{
    public Dictionary<string, CapturingSender> Senders { get; } = new(StringComparer.Ordinal);

    public override ServiceBusSender CreateSender(string queueOrTopicName) =>
        Senders.TryGetValue(queueOrTopicName, out var sender) ? sender : Senders[queueOrTopicName] = new CapturingSender(queueOrTopicName);

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ServiceBusLiveSinkTests
{
    [Fact]
    public async Task Each_message_carries_its_ttl_correlation_id_and_kind()
    {
        var client = new CapturingClient();
        await using var sink = new ServiceBusLiveSink(client, "sbns-demo-001.servicebus.windows.net");
        var poison = LiveMessage.Create("sbq-lfme-synth-dev-001", "ord-0007", new JsonObject { ["orderRef"] = "ord-0007", ["lines"] = 0 }, "order-generator");

        await sink.SendAsync(poison, TimeSpan.FromHours(1), CancellationToken.None);

        var sent = Assert.Single(client.Senders["sbq-lfme-synth-dev-001"].Messages);
        Assert.Equal(TimeSpan.FromHours(1), sent.TimeToLive);
        Assert.Equal("ord-0007", sent.CorrelationId);
        Assert.Equal("application/json", sent.ContentType);
        Assert.Equal("poison", sent.ApplicationProperties["lfme.kind"]);
        Assert.Equal("order-generator", sent.ApplicationProperties["lfme.generator"]);
        Assert.Equal(true, sent.ApplicationProperties["lfme.synthetic"]);
        Assert.Equal(0, JsonNode.Parse(sent.Body.ToString())!["lines"]!.GetValue<int>());
    }

    [Fact]
    public async Task One_sender_per_entity_is_reused()
    {
        var client = new CapturingClient();
        await using var sink = new ServiceBusLiveSink(client, "sbns-demo-001.servicebus.windows.net");
        for (var i = 0; i < 3; i++)
            await sink.SendAsync(LiveMessage.Create("sbt-lfme-synth-dev-001", $"tkt-{i}", new JsonObject(), "crew-ticket-feed"), TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.Equal(3, Assert.Single(client.Senders).Value.Messages.Count);
    }

    [Fact]
    public void Opening_a_namespace_off_the_allow_list_is_refused_before_any_client_exists()
    {
        var refused = Assert.Throws<LiveSendException>(() => ServiceBusLiveSink.Open("sbns-other-001.servicebus.windows.net", new NamespaceAllowList(["sbns-demo-001"])));
        Assert.Contains("not on the live allow-list", refused.Message, StringComparison.Ordinal);
        Assert.Throws<LiveSendException>(() => ServiceBusLiveSink.Open("sbns-demo-001.servicebus.windows.net", new NamespaceAllowList([])));
    }

    [Fact]
    public void The_credential_is_entra_only_and_never_interactive()
    {
        var options = ServiceBusLiveSink.CredentialOptions();
        Assert.True(options.ExcludeInteractiveBrowserCredential);
    }

    [Fact]
    public void The_live_sink_only_sends_it_never_receives_or_settles()
    {
        var violations = MutationScanner.Scan(typeof(ServiceBusLiveSink).Assembly.Location, type => type.FullName.Contains(nameof(ServiceBusLiveSink), StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("SendMessageAsync", StringComparison.Ordinal));
        Assert.DoesNotContain(violations, v => v.Contains("Receive", StringComparison.Ordinal) || v.Contains("Complete", StringComparison.Ordinal)
            || v.Contains("Abandon", StringComparison.Ordinal) || v.Contains("DeadLetter", StringComparison.Ordinal) || v.Contains("Defer", StringComparison.Ordinal)
            || v.Contains("Processor", StringComparison.Ordinal) || v.Contains("Administration", StringComparison.Ordinal));
    }
}
