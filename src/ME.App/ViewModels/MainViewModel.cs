// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.Demo;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private IEntityInventory? _inventory;

    public MainViewModel(
        ProviderCatalog catalog,
        IUiScheduler? scheduler = null,
        Func<Jobs.Running.JobRunner>? runnerFactory = null,
        string? samplesFolder = null,
        AccountServices? accounts = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var services = accounts ?? AccountServices.None();
        Shell = ShellModel.Observe();
        Queues = new QueuesViewModel();
        Topics = new TopicsViewModel();
        Jobs = new JobsViewModel(scheduler ?? InlineUiScheduler.Instance, runnerFactory: runnerFactory, samplesFolder: samplesFolder);
        Connect = new ConnectViewModel(catalog, OnConnectedAsync, OnDisconnected, services.Vault);
        Accounts = new AccountsViewModel(services.Authenticator, services.Store, services.Arm, OpenFromAccountsAsync);
        Connect.Accounts = Accounts;
        Accounts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountsViewModel.DefaultUsername))
                Connect.RefreshAccountText();
        };
    }

    /// <summary>Signed-in Entra accounts and the namespaces each can read.</summary>
    public AccountsViewModel Accounts { get; }

    public ShellModel Shell { get; }

    public ConnectViewModel Connect { get; }

    public QueuesViewModel Queues { get; }

    public TopicsViewModel Topics { get; }

    /// <summary>Test jobs. Independent of the connection: jobs run against the in-memory broker only.</summary>
    public JobsViewModel Jobs { get; }

    public string Title => Shell.Title;

    public const int AccountsTab = 0;
    public const int QueuesTab = 1;
    public const int TopicsTab = 2;
    public const int JobsTab = 3;

    private int _selectedTab;

    public int SelectedTab
    {
        get => _selectedTab;
        set => Set(ref _selectedTab, value);
    }

    /// <summary>The production catalog: Service Bus enabled, Kafka and AWS SQS/SNS listed as planned.</summary>
    /// <param name="syntheticDemo">Adds a synthetic, in-memory demo provider (for screenshots and walkthroughs).</param>
    public static ProviderCatalog DefaultCatalog(bool syntheticDemo = false)
    {
        IBrokerProvider serviceBus = new ServiceBusBrokerProvider(
            () => new DefaultAzureCredential(includeInteractiveCredentials: true),
            client => new Adapters.ServiceBus.Inventory.AdministrationClientReader(client));
        return new(syntheticDemo ? [serviceBus, new SyntheticDemoProvider()] : [serviceBus], ProviderCatalog.PlannedProviders);
    }

    private async Task OpenFromAccountsAsync(IEntityInventory inventory, string label)
    {
        SelectedTab = QueuesTab;
        await Connect.ConnectWithAsync(inventory, label).ConfigureAwait(true);
    }

    private async Task OnConnectedAsync(IEntityInventory inventory)
    {
        await DisposeInventoryAsync().ConfigureAwait(true);
        _inventory = inventory;
        await Queues.LoadAsync(inventory).ConfigureAwait(true);
        await Topics.LoadAsync(inventory).ConfigureAwait(true);
    }

    private void OnDisconnected()
    {
        Queues.Clear();
        Topics.Clear();
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
