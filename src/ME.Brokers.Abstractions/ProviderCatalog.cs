// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Brokers;

/// <summary>The providers the Connect panel lists. Planned ones are shown disabled.</summary>
public sealed class ProviderCatalog
{
    public const string KafkaId = "kafka";
    public const string AwsSqsSnsId = "aws-sqs-sns";

    private readonly Dictionary<string, IBrokerProvider> _enabled;

    public ProviderCatalog(IEnumerable<IBrokerProvider> enabled, IEnumerable<BrokerProviderDescriptor> planned)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        ArgumentNullException.ThrowIfNull(planned);

        _enabled = new Dictionary<string, IBrokerProvider>(StringComparer.Ordinal);
        foreach (var provider in enabled)
        {
            if (!provider.Descriptor.IsEnabled)
                throw new ArgumentException($"Provider '{provider.Descriptor.Id}' is registered as an implementation but is marked planned.", nameof(enabled));
            if (!_enabled.TryAdd(provider.Descriptor.Id, provider))
                throw new ArgumentException($"Provider '{provider.Descriptor.Id}' is registered twice.", nameof(enabled));
        }

        var all = _enabled.Values.Select(provider => provider.Descriptor).ToList();
        foreach (var descriptor in planned)
        {
            if (descriptor.IsEnabled)
                throw new ArgumentException($"Planned provider '{descriptor.Id}' must not be marked enabled.", nameof(planned));
            if (_enabled.ContainsKey(descriptor.Id) || all.Any(existing => existing.Id == descriptor.Id))
                throw new ArgumentException($"Provider '{descriptor.Id}' is listed twice.", nameof(planned));
            all.Add(descriptor);
        }

        Descriptors = all;
    }

    public IReadOnlyList<BrokerProviderDescriptor> Descriptors { get; }

    public IBrokerProvider Resolve(string providerId)
    {
        if (_enabled.TryGetValue(providerId, out var provider))
            return provider;
        if (Descriptors.Any(descriptor => descriptor.Id == providerId))
            throw new BrokerException(BrokerErrorKind.ProviderNotEnabled, BrokerException.Explain(BrokerErrorKind.ProviderNotEnabled));
        throw new BrokerException(BrokerErrorKind.InvalidConnection, $"Unknown provider '{providerId}'.");
    }

    public static IReadOnlyList<BrokerProviderDescriptor> PlannedProviders { get; } =
    [
        new(
            KafkaId,
            "Apache Kafka",
            ProviderAvailability.Planned,
            BrokerCapabilities.ListTopics,
            [],
            "bootstrap servers, for example localhost:9092",
            "Planned. Will be tested against a local Redpanda or Kafka KRaft container."),
        new(
            AwsSqsSnsId,
            "AWS SQS / SNS",
            ProviderAvailability.Planned,
            BrokerCapabilities.ListQueues | BrokerCapabilities.ListTopics,
            [],
            "region and endpoint, for example LocalStack at http://localhost:4566",
            "Planned. Will be tested against LocalStack."),
    ];
}
