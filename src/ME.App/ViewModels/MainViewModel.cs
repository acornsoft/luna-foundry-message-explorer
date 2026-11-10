// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private IEntityInventory? _inventory;

    public MainViewModel(ProviderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Shell = ShellModel.Observe();
        Queues = new QueuesViewModel();
        Connect = new ConnectViewModel(catalog, OnConnectedAsync, OnDisconnected);
    }

    public ShellModel Shell { get; }

    public ConnectViewModel Connect { get; }

    public QueuesViewModel Queues { get; }

    public string Title => Shell.Title;

    /// <summary>The production catalog: Service Bus enabled, Kafka and AWS SQS/SNS listed as planned.</summary>
    public static ProviderCatalog DefaultCatalog() =>
        new(
            [new ServiceBusBrokerProvider(
                () => new DefaultAzureCredential(includeInteractiveCredentials: true),
                client => new Adapters.ServiceBus.Inventory.AdministrationClientReader(client))],
            ProviderCatalog.PlannedProviders);

    private async Task OnConnectedAsync(IEntityInventory inventory)
    {
        await DisposeInventoryAsync().ConfigureAwait(true);
        _inventory = inventory;
        await Queues.LoadAsync(inventory).ConfigureAwait(true);
    }

    private void OnDisconnected()
    {
        Queues.Clear();
        _ = DisposeInventoryAsync();
    }

    private async Task DisposeInventoryAsync()
    {
        var previous = _inventory;
        _inventory = null;
        if (previous is not null)
            await previous.DisposeAsync().ConfigureAwait(true);
    }
}
