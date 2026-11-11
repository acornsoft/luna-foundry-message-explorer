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

/// <summary>The pages of the namespace switcher sheet.</summary>
public enum SwitcherPage
{
    Connect,
    Accounts,
    Logins,
}

/// <summary>Places <c>--tab</c> can open. There are no tabs any more: each maps to a sheet, a panel, or the Resources view.</summary>
public enum AppSection
{
    Resources,
    Accounts,
    Logins,
    Jobs,
    Move,
    Voice,
}

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
            if (e.PropertyName is nameof(ConnectViewModel.IsConnected) or nameof(ConnectViewModel.ConnectedTo))
            {
                OnPropertyChanged(nameof(NamespaceLabel));
                OnPropertyChanged(nameof(NamespaceDetail));
            }
        };
        OpenSwitcherCommand = new RelayCommand(() => OpenSwitcher());
        CloseSwitcherCommand = new RelayCommand(() => IsSwitcherOpen = false);
        OpenJobsCommand = new RelayCommand(() => IsJobsOpen = true);
        CloseJobsCommand = new RelayCommand(() => IsJobsOpen = false);
        var vault = services.Vault ?? new CredentialVault(
            new UnavailableSecretStore("secure store", "Saving logins is not available in this window."),
            new MemoryLoginMetadataStore());
        var tester = new LoginTester(vault, () => services.TestProvider ?? ServiceBusIn(catalog), () => Accounts);
        Logins = new LoginsViewModel(vault, tester, () => Accounts);
        Move = new MoveViewModel(MoveMode, move ?? MoveServices.InMemory());
        var voiceServices = voice ?? VoiceServices.None();
        Voice = new VoiceViewModel(vault, () => _inventory, () => Connect.ConnectedTo is { Length: > 0 } to ? to : "the connected namespace", audio: voiceServices.Audio, scheduler: scheduler, tokens: voiceServices.Tokens);
        Move.Moved += (_, _) => _ = RefreshResourcesAsync();
        MoveMode.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(MoveModeOn));
            OnPropertyChanged(nameof(IsReadOnly));
            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(ModeIcon));
            OnPropertyChanged(nameof(ModeTip));
        };
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

    /// <summary>The Move workspace: drag-and-drop message moves while Move mode is on.</summary>
    public MoveViewModel Move { get; }

    /// <summary>The mode chip in the top bar. Off ("Read-only") on every launch; on shows the Move workspace.</summary>
    public bool MoveModeOn
    {
        get => MoveMode.IsOn;
        set => Move.IsOn = value;
    }

    public bool IsReadOnly => !MoveModeOn;

    public string ModeText => MoveModeOn ? "Move" : "Read-only";

    public string ModeIcon => MoveModeOn ? "fa-solid fa-right-left" : "fa-solid fa-lock";

    /// <summary>The read-only explanation, shown once: here, as the chip's tooltip.</summary>
    public string ModeTip => MoveModeOn
        ? Move.Banner + " Click to go back to Read-only."
        : Shell.ReadOnlyTip;

    /// <summary>Push-to-talk Grok Voice with the read-only tools; a side panel opened by the mic in the top bar.</summary>
    public VoiceViewModel Voice { get; }

    private bool _isVoiceOpen;

    public bool IsVoiceOpen
    {
        get => _isVoiceOpen;
        set => Set(ref _isVoiceOpen, value);
    }

    public ConnectViewModel Connect { get; }

    /// <summary>The Resources view: one list of queues and topics, lane cards, peek sections and a detail drawer.</summary>
    public QuickViewViewModel QuickView { get; }

    private bool _isSwitcherOpen = true;
    private SwitcherPage _switcherPage = SwitcherPage.Connect;

    /// <summary>
    /// The namespace switcher sheet (Connect, Accounts, Saved logins), in the style of Azure Data Studio's
    /// connection dialog. Open at launch, closed by a successful connect, reopened from the top bar.
    /// </summary>
    public bool IsSwitcherOpen
    {
        get => _isSwitcherOpen;
        set => Set(ref _isSwitcherOpen, value);
    }

    public SwitcherPage SwitcherPage
    {
        get => _switcherPage;
        set
        {
            if (!Set(ref _switcherPage, value))
                return;
            OnPropertyChanged(nameof(IsConnectPage));
            OnPropertyChanged(nameof(IsAccountsPage));
            OnPropertyChanged(nameof(IsLoginsPage));
        }
    }

    public bool IsConnectPage
    {
        get => _switcherPage == SwitcherPage.Connect;
        set
        {
            if (value)
                SwitcherPage = SwitcherPage.Connect;
        }
    }

    public bool IsAccountsPage
    {
        get => _switcherPage == SwitcherPage.Accounts;
        set
        {
            if (value)
                SwitcherPage = SwitcherPage.Accounts;
        }
    }

    public bool IsLoginsPage
    {
        get => _switcherPage == SwitcherPage.Logins;
        set
        {
            if (value)
                SwitcherPage = SwitcherPage.Logins;
        }
    }

    public RelayCommand OpenSwitcherCommand { get; }

    public RelayCommand CloseSwitcherCommand { get; }

    /// <summary>The switcher button's text: the open namespace, or a prompt.</summary>
    public string NamespaceLabel => Connect.IsConnected ? QuickView.NamespaceName : "Choose a namespace";

    /// <summary>The switcher button's tooltip: how the namespace was opened (never a secret).</summary>
    public string NamespaceDetail => Connect.ConnectedTo is { Length: > 0 } to ? to + ". Click to switch." : "Open the namespace switcher: connect, accounts and saved logins.";

    private bool _isJobsOpen;

    /// <summary>Test jobs, opened from Settings. Independent of the connection (in-memory broker only).</summary>
    public bool IsJobsOpen
    {
        get => _isJobsOpen;
        set => Set(ref _isJobsOpen, value);
    }

    public RelayCommand OpenJobsCommand { get; }

    public RelayCommand CloseJobsCommand { get; }

    public void OpenSwitcher(SwitcherPage? page = null)
    {
        if (page is { } p)
            SwitcherPage = p;
        IsSwitcherOpen = true;
    }

    /// <summary>Escape: closes the topmost sheet. The switcher stays open while nothing is connected.</summary>
    public bool CloseTopmost()
    {
        if (IsJobsOpen)
        {
            IsJobsOpen = false;
            return true;
        }

        if (IsSwitcherOpen && Connect.IsConnected)
        {
            IsSwitcherOpen = false;
            return true;
        }

        if (IsVoiceOpen)
        {
            IsVoiceOpen = false;
            return true;
        }

        return false;
    }

    /// <summary>Opens a place in the app (from <c>--tab</c>). Move mode is never turned on this way.</summary>
    public void Navigate(AppSection section)
    {
        switch (section)
        {
            case AppSection.Accounts:
                OpenSwitcher(SwitcherPage.Accounts);
                break;
            case AppSection.Logins:
                OpenSwitcher(SwitcherPage.Logins);
                break;
            case AppSection.Jobs:
                IsJobsOpen = true;
                break;
            case AppSection.Voice:
                IsVoiceOpen = true;
                break;
            default:
                if (Connect.IsConnected)
                    IsSwitcherOpen = false;
                break;
        }
    }

    /// <summary>Test jobs. Independent of the connection: jobs run against the in-memory broker only.</summary>
    public JobsViewModel Jobs { get; }

    public string Title => Shell.Title;

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

    private async Task OpenFromAccountsAsync(IEntityInventory inventory, string label) =>
        await Connect.ConnectWithAsync(inventory, label).ConfigureAwait(true);

    private async Task OnConnectedAsync(IEntityInventory inventory)
    {
        await DisposeInventoryAsync().ConfigureAwait(true);
        _inventory = inventory;
        var connection = Connect.LastConnection;
        var actor = connection?.Credential is not null ? Connect.SelectedAccount?.Username : null;
        await Move.LoadAsync(inventory, MoveServicesFor(inventory, connection), actor ?? ActorFromLabel()).ConfigureAwait(true);
        QuickView.Attach(inventory, NamespaceNameOf(inventory, connection), _quickViewServices.Resolve(inventory, connection));
        OnPropertyChanged(nameof(NamespaceLabel));
        IsSwitcherOpen = false;
        // The Resources view is the main surface: read the resources once, now.
        await QuickView.EnsureLoadedAsync().ConfigureAwait(true);
    }

    private static string NamespaceNameOf(IEntityInventory inventory, BrokerConnection? connection) => inventory switch
    {
        Adapters.ServiceBus.Arm.ArmEntityInventory arm => arm.Namespace.Name,
        _ when inventory.Provider.Id == SyntheticDemoProvider.Id => SyntheticDemoProvider.Namespace,
        _ when connection?.Endpoint is { Length: > 0 } endpoint => endpoint.Split('.')[0],
        _ => inventory.Provider.DisplayName,
    };

    private string? ActorFromLabel() => Connect.ConnectedTo is { } text && text.LastIndexOf(" as ", StringComparison.Ordinal) is var at and >= 0 ? text[(at + 4)..] : null;

    private MoveAccess MoveServicesFor(IEntityInventory inventory, BrokerConnection? connection) => Move.Services.Resolve(inventory, connection);

    private async Task RefreshResourcesAsync()
    {
        if (_inventory is not null && QuickView.IsConnected)
            await QuickView.RefreshAsync().ConfigureAwait(true);
    }

    private void OnDisconnected()
    {
        _ = QuickView.ClearAsync();
        OnPropertyChanged(nameof(NamespaceLabel));
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
