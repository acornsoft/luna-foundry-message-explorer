// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

/// <summary>Runs only when ME_SB_NAMESPACE is set. CI never sets it, so these are skipped there.</summary>
public sealed class ServiceBusIntegrationFactAttribute : FactAttribute
{
    public const string Variable = "ME_SB_NAMESPACE";

    public ServiceBusIntegrationFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Opt-in integration test. Set {Variable} to a namespace you can read with your Entra sign-in.";
    }
}

public class ServiceBusIntegrationTests
{
    [ServiceBusIntegrationFact]
    public async Task Lists_queues_from_a_real_namespace_twice_without_changing_counts()
    {
        var ns = Environment.GetEnvironmentVariable(ServiceBusIntegrationFactAttribute.Variable)!;
        var provider = new ServiceBusBrokerProvider();
        var connection = BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, ns);
        Assert.Null(provider.Validate(connection));

        await using var inventory = provider.OpenInventory(connection);
        var first = await inventory.ListQueuesAsync();
        var second = await inventory.ListQueuesAsync();

        Assert.True(first.PagesRead >= 1);
        Assert.All(first.Entities, entity => Assert.Equal(EntityKind.Queue, entity.Kind));
        Assert.Equal(first.Entities.Select(e => e.Name), second.Entities.Select(e => e.Name));
    }
}
