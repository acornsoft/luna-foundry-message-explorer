// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Operations.InMemory;
using Acornsoft.MessageExplorer.Operations.ServiceBus;
using Acornsoft.MessageExplorer.Shell.Demo;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// How the quick view peeks the open connection: a function that opens a peek-only browser, or null when this
/// provider can't peek here (the quick view then shows counts only). Any provider can add a browser.
/// </summary>
public sealed class QuickViewServices(Func<IEntityInventory, BrokerConnection?, Func<IMessageBrowser?>?> resolve)
{
    public Func<IMessageBrowser?>? Resolve(IEntityInventory inventory, BrokerConnection? connection) => resolve(inventory, connection);

    /// <summary>Azure Service Bus peeks through the signed-in account or the connection; anything else is counts only.</summary>
    public static QuickViewServices Default() => new(Resolver(null));

    /// <summary>The synthetic demo: every namespace peeks the in-memory demo messages.</summary>
    public static QuickViewServices ForDemo(InMemoryMoveBroker broker) => new(Resolver(broker));

    public static Func<IEntityInventory, BrokerConnection?, Func<IMessageBrowser?>?> Resolver(InMemoryMoveBroker? demo) => (inventory, connection) =>
    {
        ArgumentNullException.ThrowIfNull(inventory);
        if (inventory.Provider.Id == SyntheticDemoProvider.Id)
            return () => SyntheticMessageBrowser.ForDemo(demo ?? SyntheticDemoProvider.SharedBroker);
        if (demo is not null && inventory is ArmEntityInventory)
            return () => SyntheticMessageBrowser.ForDemo(demo);
        if (inventory is ArmEntityInventory arm)
            return () => ServiceBusMessageBrowser.Open(arm.Namespace.FullyQualifiedNamespace, arm.Credential);
        if (connection is { ProviderId: ServiceBusBrokerProvider.ProviderId } && inventory.Provider.Supports(BrokerCapabilities.Peek))
            return () => ServiceBusMessageBrowser.Open(connection);
        return null;
    };
}
