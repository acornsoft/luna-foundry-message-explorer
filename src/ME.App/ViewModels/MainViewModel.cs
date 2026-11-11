// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Credentials.Stores;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.Demo;
using Acornsoft.MessageExplorer.Shell.Settings;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private IEntityInventory? _inventory;
    private readonly QuickViewServices _quickViewServices;

    public MainViewModel(
        ProviderCatalog catalog,
        IUiScheduler? scheduler = null,
        Func<Jobs.Running.JobRunner>? runnerFactory = null,
        string? samplesFolder = null,
        AccountServices? accounts = null,
        MoveServices? move = null,
        QuickViewServices? quickView = null,
        VoiceServices? voice = null,
        IUiSettingsStore? settings = null)
    {
        var uiSettings = settings ?? new MemoryUiSettingsStore();
        Theme = new ThemeViewModel(uiSettings);
        _quickViewServices = quickView ?? QuickViewServices.Default();
        ArgumentNullException.ThrowIfNull(catalog);
        var services = accounts ?? AccountServices.None();
        Shell = ShellModel.Observe();
        Queues = new QueuesViewModel();
        Topics = new TopicsViewModel();
        QuickView = new QuickViewViewModel(scheduler, settings: uiSettings);
        Jobs = new JobsViewModel(scheduler ?? InlineUiScheduler.Instance, runnerFactory: runnerFactory, samplesFolder: samplesFolder);
        Connect = new ConnectViewModel(catalog, OnConnectedAsync, OnDisconnected, services.Vault);
        Accounts = new AccountsViewModel(services.Authenticator, services.Store, services.Arm, OpenFromAccountsAsync, services.SignInLog, browsers: services.Browsers);
        Connect.Accounts = Accounts;
        Sources = new ConnectSourcesViewModel(Accounts, Connect, uiSettings, services.Vault);
        Connect.Sources = Sources;
        Connect.Theme = Theme;
        Accounts.Theme = Theme;
        Accounts.Browse = Sources;
        Connect.LoginSaved += _ => Sources.SelectedTab = ConnectSourcesViewModel.SavedTab;
        Connect.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectViewModel.IsConnected))
                OnPropertyChanged(nameof(ShowConnectPanel));
        };
        var vault = services.Vault ?? new CredentialVault(
            new UnavailableSecretStore("secure store", "Saving logins is not available in this window."),
            new MemoryLoginMetadataStore());
        var tester = new LoginTester(vault, () => services.TestProvider ?? ServiceBusIn(catalog), () => Accounts);
        Logins = new LoginsViewModel(vault, tester, () => Accounts);
        Move = new MoveViewModel(MoveMode, move ?? MoveServices.InMemory());
        var voiceServices = voice ?? VoiceServices.None();
        Voice = new VoiceViewModel(vault, () => _inventory, () => Connect.ConnectedTo is { Length: > 0 } to ? to : "the connected namespace", audio: voiceServices.Audio, scheduler: scheduler, tokens: voiceServices.Tokens);
        Move.Moved += (_, _) => _ = RefreshGridsAsync();
        MoveMode.Changed += (_, _) => OnPropertyChanged(nameof(MoveModeOn));
        Accounts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountsViewModel.SignedInAccounts))
                Connect.RefreshAccountText();
        };
    }

    /// <summary>Light, Dark or System; shared by every theme picker in the window and remembered between launches.</summary>
    public ThemeViewModel Theme { get; }

    /// <summary>Recent, Saved and Browse on the Connect panel.</summary>
    public ConnectSourcesViewModel Sources { get; }

    /// <summary>Signed-in Entra accounts and the namespaces each can read.</summary>
    public AccountsViewModel Accounts { get; }

    /// <summary>Saved logins for every system.</summary>
    public LoginsViewModel Logins { get; }

    public ShellModel Shell { get; }

    /// <summary>Off on every launch. Nothing reads it from settings, files or arguments.</summary>
    public Operations.Move.MoveModeSwitch MoveMode { get; } = new();

    /// <summary>The Move tab: drag-and-drop message moves while Move mode is on.</summary>
    public MoveViewModel Move { get; }

    /// <summary>The toggle at the top of the window.</summary>
    public bool MoveModeOn
    {
        get => MoveMode.IsOn;
        set => Move.IsOn = value;
    }

    /// <summary>The Voice tab: push-to-talk Grok Voice with the read-only tools.</summary>
    public VoiceViewModel Voice { get; }

    public ConnectViewModel Connect { get; }

    public QueuesViewModel Queues { get; }

    public TopicsViewModel Topics { get; }

    /// <summary>The Quick view tab: entity tree, lane cards, cascading peek sections and a detail drawer.</summary>
    public QuickViewViewModel QuickView { get; }

    /// <summary>Test jobs. Independent of the connection: jobs run against the in-memory broker only.</summary>
    public JobsViewModel Jobs { get; }

    public string Title => Shell.Title;

    public const int AccountsTab = 0;
    public const int LoginsTab = 1;

    /// <summary>Queues and topics: the quick-view layout is the one entity view (tree, lane cards, sections, drawer).</summary>
    public const int ExplorerTab = 2;

    // The old Queues, Topics and Quick view tabs are one tab now; the names stay so --tab and callers still land there.
    public const int QueuesTab = ExplorerTab;
    public const int TopicsTab = ExplorerTab;
    public const int QuickViewTab = ExplorerTab;
    public const int JobsTab = 3;
    public const int MoveTab = 4;
    public const int VoiceTab = 5;

    private int _selectedTab;

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!Set(ref _selectedTab, value))
                return;
            OnPropertyChanged(nameof(ShowConnectPanel));
            if (value == QuickViewTab)
                _ = QuickView.EnsureLoadedAsync();
        }
    }

    private bool _connectPinned;

    /// <summary>Keeps the Connect panel open on every tab (the plug button in the header).</summary>
    public bool ConnectPinned
    {
        get => _connectPinned;
        set
        {
            if (Set(ref _connectPinned, value))
                OnPropertyChanged(nameof(ShowConnectPanel));
        }
    }

    /// <summary>
    /// The Connect panel shows on the setup tabs (Accounts, Saved logins), whenever nothing is connected, or when
    /// pinned. Once connected, the other tabs get the full width.
    /// </summary>
    public bool ShowConnectPanel => _connectPinned || !Connect.IsConnected || _selectedTab is AccountsTab or LoginsTab;

    /// <summary>The production catalog: Service Bus enabled, Kafka and AWS SQS/SNS listed as planned.</summary>
    /// <param name="syntheticDemo">Adds a synthetic, in-memory demo provider (for screenshots and walkthroughs).</param>
    public static ProviderCatalog DefaultCatalog(bool syntheticDemo = false)
    {
        IBrokerProvider serviceBus = new ServiceBusBrokerProvider(
            () => new DefaultAzureCredential(includeInteractiveCredentials: true),
            client => new Adapters.ServiceBus.Inventory.AdministrationClientReader(client));
        return new(syntheticDemo ? [serviceBus, new SyntheticDemoProvider()] : [serviceBus], ProviderCatalog.PlannedProviders);
    }

    private static IBrokerProvider? ServiceBusIn(ProviderCatalog catalog)
    {
        try
        {
            return catalog.Resolve(ServiceBusBrokerProvider.ProviderId);
        }
        catch (BrokerException)
        {
            return null;
        }
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
        var connection = Connect.LastConnection;
        var actor = connection?.Credential is not null ? Connect.SelectedAccount?.Username : null;
        await Move.LoadAsync(inventory, MoveServicesFor(inventory, connection), actor ?? ActorFromLabel()).ConfigureAwait(true);
        QuickView.Attach(inventory, NamespaceLabel(inventory, connection), _quickViewServices.Resolve(inventory, connection));
        if (SelectedTab == QuickViewTab)
            await QuickView.EnsureLoadedAsync().ConfigureAwait(true);
    }

    private static string NamespaceLabel(IEntityInventory inventory, BrokerConnection? connection) => inventory switch
    {
        Adapters.ServiceBus.Arm.ArmEntityInventory arm => arm.Namespace.Name,
        _ when inventory.Provider.Id == SyntheticDemoProvider.Id => SyntheticDemoProvider.Namespace,
        _ when connection?.Endpoint is { Length: > 0 } endpoint => endpoint.Split('.')[0],
        _ => inventory.Provider.DisplayName,
    };

    private string? ActorFromLabel() => Connect.ConnectedTo is { } text && text.LastIndexOf(" as ", StringComparison.Ordinal) is var at and >= 0 ? text[(at + 4)..] : null;

    private MoveAccess MoveServicesFor(IEntityInventory inventory, BrokerConnection? connection) => Move.Services.Resolve(inventory, connection);

    private async Task RefreshGridsAsync()
    {
        if (_inventory is null)
            return;
        await Queues.RefreshCommand.ExecuteAsync().ConfigureAwait(true);
        await Topics.RefreshCommand.ExecuteAsync().ConfigureAwait(true);
        if (QuickView.IsConnected && SelectedTab == QuickViewTab)
            await QuickView.RefreshAsync().ConfigureAwait(true);
    }

    private void OnDisconnected()
    {
        Queues.Clear();
        Topics.Clear();
        _ = QuickView.ClearAsync();
        _ = Move.ClearAsync();
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
