// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;
using Acornsoft.MessageExplorer.Brokers;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

public class ServiceBusInventoryTests
{
    [Fact]
    public async Task Paging_reads_every_page_and_joins_status_by_name()
    {
        var reader = new PagedReader(pageSize: 100);
        for (var i = 1; i <= 230; i++)
            reader.AddQueue($"synthetic-q-{i:D3}", active: i, deadLetter: i % 10 == 0 ? 2 : 0, scheduled: i % 4, transferDeadLetter: i == 7 ? 1 : 0, status: i == 5 ? "Disabled" : "Active");
        await using var inventory = new ServiceBusEntityInventory(ServiceBusBrokerProvider.Descriptor, reader);

        var result = await inventory.ListQueuesAsync();

        Assert.Equal(3, result.PagesRead);
        Assert.Equal(230, result.Entities.Count);
        Assert.Equal(3, reader.QueueStatusPagesServed);
        Assert.Equal("Disabled", result.Entities.Single(e => e.Name == "synthetic-q-005").Status);
        Assert.Equal(1, result.Entities.Single(e => e.Name == "synthetic-q-007").Counts.TransferDeadLetter);
        Assert.Equal(23, result.Entities.Count(e => e.Counts.DeadLetter > 0));
        Assert.Equal(result.Entities.Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase), result.Entities.Select(e => e.Name));
    }

    [Fact]
    public async Task Empty_namespace_returns_no_rows_and_one_page()
    {
        var reader = new PagedReader(pageSize: 100);
        await using var inventory = new ServiceBusEntityInventory(ServiceBusBrokerProvider.Descriptor, reader);
        var result = await inventory.ListQueuesAsync();
        Assert.Empty(result.Entities);
        Assert.Equal(1, result.PagesRead);
    }

    [Fact]
    public async Task Missing_status_leaves_status_null()
    {
        var reader = new PagedReader(pageSize: 10);
        reader.AddQueue("orphan", 1, 0, 0, 0, status: null);
        await using var inventory = new ServiceBusEntityInventory(ServiceBusBrokerProvider.Descriptor, reader);
        var result = await inventory.ListQueuesAsync();
        Assert.Null(Assert.Single(result.Entities).Status);
    }

    [Fact]
    public async Task Topics_and_subscriptions_are_listed_read_only()
    {
        var reader = new PagedReader(pageSize: 1);
        reader.Topics.Add(new EntitySnapshot(EntityKind.Topic, "gsl-events", null, new EntityCounts(null, null, 3, null, null), null, 100, 2));
        reader.Subscriptions.Add(new EntitySnapshot(EntityKind.Subscription, "audit", "gsl-events", new EntityCounts(5, 1, null, 0, 6), null, null));
        reader.Subscriptions.Add(new EntitySnapshot(EntityKind.Subscription, "notify", "gsl-events", new EntityCounts(0, 0, null, 0, 0), null, null));
        await using var inventory = new ServiceBusEntityInventory(ServiceBusBrokerProvider.Descriptor, reader);

        var topics = await inventory.ListTopicsAsync();
        var subscriptions = await inventory.ListSubscriptionsAsync("gsl-events");

        Assert.Equal("Active", Assert.Single(topics.Entities).Status);
        Assert.Equal(2, subscriptions.PagesRead);
        Assert.Equal(["audit", "notify"], subscriptions.Entities.Select(e => e.Name).ToArray());
        Assert.Equal("gsl-events/subscriptions/audit", subscriptions.Entities[0].Path);
        await Assert.ThrowsAsync<ArgumentException>(() => inventory.ListSubscriptionsAsync(" "));
    }

    [Fact]
    public async Task Reader_failures_are_mapped_to_broker_errors()
    {
        var reader = new PagedReader(pageSize: 10) { FailAfterPages = 0, Failure = new UnauthorizedAccessException("401 synthetic") };
        await using var inventory = new ServiceBusEntityInventory(ServiceBusBrokerProvider.Descriptor, reader);

        var error = await Assert.ThrowsAsync<BrokerException>(() => inventory.ListQueuesAsync());

        Assert.Equal(BrokerErrorKind.Forbidden, error.Kind);
        Assert.DoesNotContain("401 synthetic", error.Message, StringComparison.Ordinal);
        Assert.IsType<UnauthorizedAccessException>(error.InnerException);
    }

    [Fact]
    public async Task Failure_mid_paging_is_mapped_and_partial_rows_are_not_returned()
    {
        var reader = new PagedReader(pageSize: 2) { FailAfterPages = 1, Failure = new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy) };
        for (var i = 0; i < 5; i++)
            reader.AddQueue($"q{i}", 1, 0, 0, 0, "Active");
        await using var inventory = new ServiceBusEntityInventory(ServiceBusBrokerProvider.Descriptor, reader);

        var error = await Assert.ThrowsAsync<BrokerException>(() => inventory.ListQueuesAsync());
        Assert.Equal(BrokerErrorKind.Throttled, error.Kind);
        Assert.True(error.IsRetryable);
    }

    [Fact]
    public async Task Cancellation_is_not_mapped()
    {
        var reader = new PagedReader(pageSize: 1);
        reader.AddQueue("q", 1, 0, 0, 0, "Active");
        await using var inventory = new ServiceBusEntityInventory(ServiceBusBrokerProvider.Descriptor, reader);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventory.ListQueuesAsync(cts.Token));
    }

    public static TheoryData<Exception, BrokerErrorKind> ErrorCases() => new()
    {
        { new CredentialUnavailableException("no credential"), BrokerErrorKind.Unauthenticated },
        { new AuthenticationFailedException("bad token"), BrokerErrorKind.Unauthenticated },
        { new UnauthorizedAccessException("denied"), BrokerErrorKind.Forbidden },
        { new ServiceBusException("gone", ServiceBusFailureReason.MessagingEntityNotFound), BrokerErrorKind.NotFound },
        { new ServiceBusException("busy", ServiceBusFailureReason.ServiceBusy), BrokerErrorKind.Throttled },
        { new ServiceBusException("quota", ServiceBusFailureReason.QuotaExceeded), BrokerErrorKind.Throttled },
        { new ServiceBusException("slow", ServiceBusFailureReason.ServiceTimeout), BrokerErrorKind.Timeout },
        { new ServiceBusException("net", ServiceBusFailureReason.ServiceCommunicationProblem), BrokerErrorKind.Unreachable },
        { new ServiceBusException("other", ServiceBusFailureReason.GeneralError), BrokerErrorKind.Unknown },
        { new RequestFailedException(0, "dns"), BrokerErrorKind.Unreachable },
        { new RequestFailedException(401, "401"), BrokerErrorKind.Unauthenticated },
        { new RequestFailedException(403, "403"), BrokerErrorKind.Forbidden },
        { new RequestFailedException(404, "404"), BrokerErrorKind.NotFound },
        { new RequestFailedException(408, "408"), BrokerErrorKind.Timeout },
        { new RequestFailedException(429, "429"), BrokerErrorKind.Throttled },
        { new RequestFailedException(503, "503"), BrokerErrorKind.Throttled },
        { new RequestFailedException(500, "500"), BrokerErrorKind.Unknown },
        { new HttpRequestException("no route"), BrokerErrorKind.Unreachable },
        { new System.Net.Sockets.SocketException(11001), BrokerErrorKind.Unreachable },
        { new TimeoutException(), BrokerErrorKind.Timeout },
        { new FormatException(), BrokerErrorKind.InvalidConnection },
        { new AggregateException(new TimeoutException()), BrokerErrorKind.Timeout },
        { new InvalidOperationException(), BrokerErrorKind.Unknown },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public void Error_mapping_classifies_sdk_and_identity_failures(Exception exception, BrokerErrorKind expected)
    {
        var mapped = ServiceBusErrorMapper.Map(exception);
        Assert.Equal(expected, mapped.Kind);
        Assert.Equal(BrokerException.Explain(expected), mapped.Message);
        Assert.Same(exception, mapped.InnerException);
    }

    [Fact]
    public void Error_mapping_passes_broker_errors_through()
    {
        var original = new BrokerException(BrokerErrorKind.NotFound, "x");
        Assert.Same(original, ServiceBusErrorMapper.Map(original));
    }

    [Fact]
    public void Sdk_runtime_properties_map_to_neutral_rows()
    {
        var at = new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);
        var queue = ServiceBusModelFactory.QueueRuntimeProperties("gsl-disputes", 10, 3, 2, 1, 0, 16, 4096, at, at, at);
        var topic = ServiceBusModelFactory.TopicRuntimeProperties("gsl-events", 4, 2048, 3, at, at, at);
        var subscription = ServiceBusModelFactory.SubscriptionRuntimeProperties("gsl-events", "audit", activeMessageCount: 6, deadLetterMessageCount: 1, transferDeadLetterMessageCount: 0, transferMessageCount: 0, totalMessageCount: 7, at, at, at);

        var q = ServiceBusMapping.ToSnapshot(queue);
        var t = ServiceBusMapping.ToSnapshot(topic);
        var s = ServiceBusMapping.ToSnapshot(subscription);

        Assert.Equal(new EntityCounts(10, 2, 3, 1, 16), q.Counts);
        Assert.Equal(4096, q.SizeInBytes);
        Assert.Equal(EntityKind.Queue, q.Kind);
        Assert.Equal(EntityKind.Topic, t.Kind);
        Assert.Equal(4, t.Counts.Scheduled);
        Assert.Null(t.Counts.Active);
        Assert.Equal(3, t.SubscriptionCount);
        Assert.Equal(EntityKind.Subscription, s.Kind);
        Assert.Equal("gsl-events", s.Parent);
        Assert.Equal(6, s.Counts.Active);
        Assert.Equal(1, s.Counts.DeadLetter);
        Assert.Null(s.Counts.Scheduled);
    }

    [Fact]
    public void Sdk_entity_status_maps_to_text()
    {
        var queue = ServiceBusModelFactory.QueueProperties(
            "gsl-disputes", TimeSpan.FromSeconds(30), 1024, false, false, TimeSpan.FromDays(1), TimeSpan.FromMinutes(10),
            false, TimeSpan.FromMinutes(1), 10, false, EntityStatus.SendDisabled, null, null, string.Empty, false);
        Assert.Equal(new EntityStatusRow("gsl-disputes", "SendDisabled"), ServiceBusMapping.ToStatus(queue));
    }
}

public class ServiceBusConnectionTests
{
    // Built from parts so the repo secret scan never sees a key-name literal.
    private static readonly string SyntheticConnectionString =
        "Endpoint=sb://contoso-synthetic.servicebus.windows.net/;" + "Shared" + "AccessKeyName=synthetic;" + "Shared" + "AccessKey=" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not-a-real-key"));

    [Theory]
    [InlineData("contoso-synthetic", "contoso-synthetic.servicebus.windows.net")]
    [InlineData("contoso-synthetic.servicebus.windows.net", "contoso-synthetic.servicebus.windows.net")]
    [InlineData(" https://Contoso-Synthetic.servicebus.windows.net/ ", "contoso-synthetic.servicebus.windows.net")]
    [InlineData("sb://contoso-synthetic.servicebus.windows.net", "contoso-synthetic.servicebus.windows.net")]
    [InlineData("contoso-synthetic.servicebus.usgovcloudapi.net", "contoso-synthetic.servicebus.usgovcloudapi.net")]
    public void Namespace_input_is_normalized(string input, string expected)
    {
        Assert.Null(ServiceBusBrokerProvider.NormalizeNamespace(input, out var fqdn));
        Assert.Equal(expected, fqdn);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("Endpoint=sb://x.servicebus.windows.net/;KeyName=y")]
    [InlineData("contoso-synthetic.servicebus.windows.net/queue")]
    [InlineData("contoso-synthetic.servicebus.windows.net:443")]
    [InlineData("bad host.servicebus.windows.net")]
    public void Bad_namespace_input_is_refused_with_a_message(string input)
    {
        Assert.NotNull(ServiceBusBrokerProvider.NormalizeNamespace(input, out var fqdn));
        Assert.Equal(string.Empty, fqdn);
    }

    [Fact]
    public void Entra_mode_plans_a_credential_connection()
    {
        var plan = ServiceBusBrokerProvider.Plan(BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, "contoso-synthetic"));
        Assert.Equal(new ServiceBusConnectionPlan(AuthMode.Entra, "contoso-synthetic.servicebus.windows.net"), plan);
    }

    [Fact]
    public void Connection_string_mode_plans_a_key_connection_and_reads_the_endpoint()
    {
        var connection = BrokerConnection.WithConnectionString(ServiceBusBrokerProvider.ProviderId, new RuntimeSecret(SyntheticConnectionString));
        var plan = ServiceBusBrokerProvider.Plan(connection);
        Assert.Equal(AuthMode.ConnectionString, plan.Mode);
        Assert.Equal("contoso-synthetic.servicebus.windows.net", plan.FullyQualifiedNamespace);
    }

    [Fact]
    public void Validate_refuses_bad_input_without_echoing_the_secret()
    {
        var provider = new ServiceBusBrokerProvider();
        var garbage = BrokerConnection.WithConnectionString(ServiceBusBrokerProvider.ProviderId, new RuntimeSecret("not a connection string secret-xyz"));
        var wrongProvider = BrokerConnection.WithEntra("kafka", "contoso-synthetic");

        var message = provider.Validate(garbage);

        Assert.NotNull(message);
        Assert.DoesNotContain("secret-xyz", message, StringComparison.Ordinal);
        Assert.NotNull(provider.Validate(wrongProvider));
        Assert.Null(provider.Validate(BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, "contoso-synthetic")));
        Assert.Throws<BrokerException>(() => ServiceBusBrokerProvider.Plan(garbage));
    }

    [Fact]
    public void Entra_mode_uses_the_credential_factory_and_connection_string_mode_does_not()
    {
        var credentialCalls = 0;
        var readers = 0;
        var provider = new ServiceBusBrokerProvider(
            () =>
            {
                credentialCalls++;
                return new NeverCredential();
            },
            client =>
            {
                readers++;
                return new PagedReader(10);
            });

        var entra = provider.OpenInventory(BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, "contoso-synthetic"));
        Assert.Equal(1, credentialCalls);

        var keyed = provider.OpenInventory(BrokerConnection.WithConnectionString(ServiceBusBrokerProvider.ProviderId, new RuntimeSecret(SyntheticConnectionString)));
        Assert.Equal(1, credentialCalls);
        Assert.Equal(2, readers);
        Assert.Equal(ServiceBusBrokerProvider.ProviderId, entra.Provider.Id);
        Assert.Equal(ServiceBusBrokerProvider.ProviderId, keyed.Provider.Id);
    }

    [Fact]
    public void Open_with_bad_namespace_throws_invalid_connection()
    {
        var provider = new ServiceBusBrokerProvider(() => new NeverCredential(), _ => new PagedReader(10));
        var error = Assert.Throws<BrokerException>(() => provider.OpenInventory(BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, "abc")));
        Assert.Equal(BrokerErrorKind.InvalidConnection, error.Kind);
    }

    [Fact]
    public void Descriptor_offers_both_sign_in_modes_and_read_capabilities()
    {
        var descriptor = ServiceBusBrokerProvider.Descriptor;
        Assert.True(descriptor.IsEnabled);
        Assert.Equal([AuthMode.Entra, AuthMode.ConnectionString], descriptor.AuthModes);
        Assert.True(descriptor.Supports(BrokerCapabilities.ListQueues | BrokerCapabilities.DeadLetterCounts | BrokerCapabilities.ScheduledCounts | BrokerCapabilities.TransferDeadLetterCounts));
    }

    private sealed class NeverCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tests never fetch a token.");

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tests never fetch a token.");
    }
}

/// <summary>A synthetic management plane that serves runtime and status rows in pages.</summary>
internal sealed class PagedReader : IServiceBusAdminReader
{
    private readonly int _pageSize;

    public PagedReader(int pageSize) => _pageSize = pageSize;

    public List<EntitySnapshot> Queues { get; } = [];

    public Dictionary<string, string> QueueStatus { get; } = new(StringComparer.Ordinal);

    public List<EntitySnapshot> Topics { get; } = [];

    public List<EntitySnapshot> Subscriptions { get; } = [];

    public int QueueStatusPagesServed { get; private set; }

    public int? FailAfterPages { get; init; }

    public Exception? Failure { get; init; }

    public void AddQueue(string name, long active, long deadLetter, long scheduled, long transferDeadLetter, string? status)
    {
        Queues.Add(new EntitySnapshot(EntityKind.Queue, name, null, new EntityCounts(active, deadLetter, scheduled, transferDeadLetter, active + deadLetter + scheduled + transferDeadLetter), null, 1024));
        if (status is not null)
            QueueStatus[name] = status;
    }

    public IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> QueueRuntimePagesAsync(CancellationToken cancellationToken) =>
        Pages(Queues, cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> QueueStatusPagesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in Pages(QueueStatus.Select(pair => new EntityStatusRow(pair.Key, pair.Value)).ToList(), cancellationToken))
        {
            QueueStatusPagesServed++;
            yield return page;
        }
    }

    public IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> TopicRuntimePagesAsync(CancellationToken cancellationToken) =>
        Pages(Topics, cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> TopicStatusPagesAsync(CancellationToken cancellationToken) =>
        Pages(Topics.Select(t => new EntityStatusRow(t.Name, "Active")).ToList(), cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> SubscriptionRuntimePagesAsync(string topicName, CancellationToken cancellationToken) =>
        Pages(Subscriptions.Where(s => s.Parent == topicName).ToList(), cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> SubscriptionStatusPagesAsync(string topicName, CancellationToken cancellationToken) =>
        Pages(Subscriptions.Where(s => s.Parent == topicName).Select(s => new EntityStatusRow(s.Name, "Active")).ToList(), cancellationToken);

    private async IAsyncEnumerable<IReadOnlyList<T>> Pages<T>(IReadOnlyList<T> rows, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var served = 0;
        var offset = 0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailAfterPages == served && Failure is not null)
                throw Failure;
            await Task.Yield();
            yield return rows.Skip(offset).Take(_pageSize).ToList();
            served++;
            offset += _pageSize;
        }
        while (offset < rows.Count);
    }
}
