// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Reflection;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Brokers.Tests;

public class ProviderCatalogTests
{
    [Fact]
    public void Catalog_lists_enabled_then_planned_providers()
    {
        var catalog = new ProviderCatalog([new FakeBrokerProvider()], ProviderCatalog.PlannedProviders);

        Assert.Equal([FakeBrokerProvider.Id, ProviderCatalog.KafkaId, ProviderCatalog.AwsSqsSnsId], catalog.Descriptors.Select(d => d.Id).ToArray());
        Assert.True(catalog.Descriptors[0].IsEnabled);
        Assert.All(catalog.Descriptors.Skip(1), d => Assert.Equal(ProviderAvailability.Planned, d.Availability));
    }

    [Fact]
    public void Resolving_a_planned_provider_says_it_is_not_enabled()
    {
        var catalog = new ProviderCatalog([new FakeBrokerProvider()], ProviderCatalog.PlannedProviders);

        var kafka = Assert.Throws<BrokerException>(() => catalog.Resolve(ProviderCatalog.KafkaId));
        var aws = Assert.Throws<BrokerException>(() => catalog.Resolve(ProviderCatalog.AwsSqsSnsId));
        var unknown = Assert.Throws<BrokerException>(() => catalog.Resolve("nope"));

        Assert.Equal(BrokerErrorKind.ProviderNotEnabled, kafka.Kind);
        Assert.Equal(BrokerErrorKind.ProviderNotEnabled, aws.Kind);
        Assert.Equal(BrokerErrorKind.InvalidConnection, unknown.Kind);
    }

    [Fact]
    public void Resolve_returns_the_registered_implementation()
    {
        var fake = new FakeBrokerProvider();
        var catalog = new ProviderCatalog([fake], []);
        Assert.Same(fake, catalog.Resolve(FakeBrokerProvider.Id));
    }

    [Fact]
    public void Planned_descriptors_have_no_sign_in_modes_yet()
    {
        Assert.All(ProviderCatalog.PlannedProviders, d => Assert.Empty(d.AuthModes));
        Assert.Contains("LocalStack", ProviderCatalog.PlannedProviders.Single(d => d.Id == ProviderCatalog.AwsSqsSnsId).Note, StringComparison.Ordinal);
        Assert.Contains("KRaft", ProviderCatalog.PlannedProviders.Single(d => d.Id == ProviderCatalog.KafkaId).Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_rejects_duplicates_and_mislabelled_entries()
    {
        Assert.Throws<ArgumentException>(() => new ProviderCatalog([new FakeBrokerProvider(), new FakeBrokerProvider()], []));
        var enabledAsPlanned = new FakeBrokerProvider().Descriptor;
        Assert.Throws<ArgumentException>(() => new ProviderCatalog([], [enabledAsPlanned]));
        var plannedClash = ProviderCatalog.PlannedProviders[0] with { Id = FakeBrokerProvider.Id };
        Assert.Throws<ArgumentException>(() => new ProviderCatalog([new FakeBrokerProvider()], [plannedClash]));
        Assert.Throws<ArgumentException>(() => new ProviderCatalog([new PlannedButImplemented()], []));
    }

    [Fact]
    public void Capability_flags_are_checked_as_a_set()
    {
        var descriptor = new FakeBrokerProvider().Descriptor;
        Assert.True(descriptor.Supports(BrokerCapabilities.ListQueues | BrokerCapabilities.DeadLetterCounts));
        Assert.False(descriptor.Supports(BrokerCapabilities.ListQueues | BrokerCapabilities.Peek));
    }

    private sealed class PlannedButImplemented : IBrokerProvider
    {
        public BrokerProviderDescriptor Descriptor => ProviderCatalog.PlannedProviders[0];

        public string? Validate(BrokerConnection connection) => null;

        public IEntityInventory OpenInventory(BrokerConnection connection) => throw new NotSupportedException();
    }
}

public class ConnectionTests
{
    private const string SyntheticSecret = "Endpoint=sb://synthetic.example/;KeyName=demo;Key=not-a-real-key";

    [Fact]
    public void Runtime_secret_never_prints_its_value()
    {
        var secret = new RuntimeSecret(SyntheticSecret);
        Assert.Equal("***", secret.ToString());
        Assert.Equal("***", $"{secret}");
        Assert.Equal(SyntheticSecret, secret.Reveal());
    }

    [Fact]
    public void Connection_text_does_not_carry_the_secret()
    {
        var connection = BrokerConnection.WithConnectionString("fake-broker", new RuntimeSecret(SyntheticSecret));
        Assert.DoesNotContain("not-a-real-key", connection.ToString(), StringComparison.Ordinal);
        Assert.Equal(AuthMode.ConnectionString, connection.AuthMode);
        Assert.Null(connection.Endpoint);
    }

    [Fact]
    public void Entra_connection_has_an_endpoint_and_no_secret()
    {
        var connection = BrokerConnection.WithEntra("fake-broker", "  contoso-synthetic.servicebus.windows.net ");
        Assert.Equal("contoso-synthetic.servicebus.windows.net", connection.Endpoint);
        Assert.Null(connection.Secret);
        Assert.Equal(AuthMode.Entra, connection.AuthMode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_inputs_are_refused(string value)
    {
        Assert.Throws<ArgumentException>(() => new RuntimeSecret(value));
        Assert.Throws<ArgumentException>(() => BrokerConnection.WithEntra("fake-broker", value));
    }

    [Fact]
    public void Secret_type_has_no_persistence_surface()
    {
        var members = typeof(RuntimeSecret).GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(member => member.Name)
            .ToArray();
        foreach (var forbidden in new[] { "Save", "Write", "Serialize", "Persist", "Export" })
            Assert.DoesNotContain(members, name => name.Contains(forbidden, StringComparison.Ordinal));
    }
}

public class InventoryContractTests
{
    private static readonly string[] MutatingVerbs =
    [
        "Send", "Receive", "Complete", "Abandon", "DeadLetter", "Defer", "Renew", "Lock", "Settle",
        "Schedule", "Cancel", "Create", "Update", "Delete", "Purge", "Put", "Post", "Set",
    ];

    [Theory]
    [InlineData(typeof(IEntityInventory))]
    [InlineData(typeof(IBrokerProvider))]
    public void Provider_contract_has_no_mutating_member(Type contract)
    {
        var methods = contract.GetMethods().Concat(contract.GetInterfaces().SelectMany(i => i.GetMethods())).Select(m => m.Name).ToArray();
        Assert.NotEmpty(methods);
        foreach (var verb in MutatingVerbs)
            Assert.DoesNotContain(methods, name => name.StartsWith(verb, StringComparison.Ordinal));
    }

    [Fact]
    public void Capability_flags_name_reads_only()
    {
        // Every flag is a listing, a count, an entity property, or a peek. A write flag would fail here.
        foreach (var name in Enum.GetNames<BrokerCapabilities>())
        {
            var isRead = name == "None"
                || name == "Peek"
                || name.StartsWith("List", StringComparison.Ordinal)
                || name.StartsWith("Entity", StringComparison.Ordinal)
                || name.EndsWith("Counts", StringComparison.Ordinal);
            Assert.True(isRead, $"{name} does not look like a read capability.");
        }
    }

    [Fact]
    public async Task Fake_provider_round_trip_lists_queues_through_the_abstraction()
    {
        IBrokerProvider provider = new FakeBrokerProvider();
        var connection = BrokerConnection.WithEntra(FakeBrokerProvider.Id, "synthetic");
        Assert.Null(provider.Validate(connection));

        await using var inventory = provider.OpenInventory(connection);
        var result = await inventory.ListQueuesAsync();

        Assert.Equal(4, result.Entities.Count);
        Assert.Equal(1, result.PagesRead);
        Assert.Equal(2, result.Entities.Count(entity => entity.Counts.HasDeadLetters));
    }

    [Fact]
    public async Task Fake_inventory_reports_pages()
    {
        var provider = new FakeBrokerProvider(SyntheticQueues.Many(250));
        var inventory = new FakeInventory(provider.Descriptor, SyntheticQueues.Many(250), pageSize: 100);
        var result = await inventory.ListQueuesAsync();
        Assert.Equal(250, result.Entities.Count);
        Assert.Equal(3, result.PagesRead);
    }

    [Fact]
    public void Counts_flag_dead_letters_and_unknowns()
    {
        Assert.False(EntityCounts.Unknown.HasDeadLetters);
        Assert.True(new EntityCounts(0, 1, 0, 0, 1).HasDeadLetters);
        Assert.True(new EntityCounts(0, 0, 0, 2, 2).HasDeadLetters);
        Assert.False(new EntityCounts(5, 0, 0, 0, 5).HasDeadLetters);
    }

    [Fact]
    public void Subscription_path_includes_the_topic()
    {
        var subscription = new EntitySnapshot(EntityKind.Subscription, "audit", "gsl-events", EntityCounts.Unknown, null, null);
        Assert.Equal("gsl-events/subscriptions/audit", subscription.Path);
        var queue = SyntheticQueues.Queue("q1", 1, 0);
        Assert.Equal("q1", queue.Path);
    }

    [Fact]
    public void Broker_errors_explain_every_kind_and_mark_retryable_ones()
    {
        foreach (var kind in Enum.GetValues<BrokerErrorKind>())
            Assert.False(string.IsNullOrWhiteSpace(BrokerException.Explain(kind)));
        Assert.True(new BrokerException(BrokerErrorKind.Throttled, "x").IsRetryable);
        Assert.True(new BrokerException(BrokerErrorKind.Unreachable, "x").IsRetryable);
        Assert.False(new BrokerException(BrokerErrorKind.Forbidden, "x").IsRetryable);
    }
}
