// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
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

/// <summary>Where the switcher puts the user's attention when it lands on a page by itself.</summary>
public enum SwitcherFocus
{
    /// <summary>Nothing special: the Recent or Saved list is the next step.</summary>
    None,

    /// <summary>First run: focus on "Add an account".</summary>
    AddAccount,

    /// <summary>Accounts but nothing saved yet: bring the namespace browser into view.</summary>
    Browse,
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
                IdentityChanged();
            if (e.PropertyName == nameof(ConnectViewModel.HasSavedConnections))
                QuickSwitchChanged();
        };
        Connect.SavedConnections.CollectionChanged += (_, _) =>
        {
            QuickSwitchChanged();
            LandSwitcher();
        };
        Sources.Recent.CollectionChanged += (_, _) =>
        {
            QuickSwitchChanged();
            LandSwitcher();
        };
        Accounts.Accounts.CollectionChanged += (_, _) => LandSwitcher();
        QuickOpenRecentCommand = new AsyncCommand<RecentItemViewModel>(QuickOpenRecentAsync);
        QuickConnectSavedCommand = new AsyncCommand<LoginEntry>(QuickConnectSavedAsync);
        QuickSwitchCommand = new AsyncCommand<QuickSwitchItem>(item => item.Login is { } login ? QuickConnectSavedAsync(login)
            : item.Recent is { } recent ? QuickOpenRecentAsync(recent) : Task.CompletedTask);
        RebuildQuickSwitch();
        ManageAccountsCommand = new RelayCommand(() => OpenSwitcher());
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
            OnPropertyChanged(nameof(MoveBannerText));
        };
        LandSwitcher();
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

    /// <summary>The chip's tooltip: one phrase while Read-only.</summary>
    public string ModeTip => MoveModeOn
        ? Move.Banner + " Click to go back to Read-only."
        : Shell.ReadOnlyTip;

    /// <summary>The amber banner while Move mode is on: which namespace, as whom, in which cloud.</summary>
    public string MoveBannerText => string.IsNullOrEmpty(NamespaceAccount)
        ? Move.Banner
        : $"Move mode is on for {NamespaceLabel} ({NamespaceAccount}, {NamespaceCloud}). Dropping messages moves them. Every move asks first and is written to the audit log.";

    /// <summary>The search box's watermark, with the platform's shortcut.</summary>
    public string SearchHint => "Search resources  " + Shortcuts.Format("F");

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
    private SwitcherFocus _switcherFocus;
    private bool _pagePicked;

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
            _pagePicked = true;
            SwitcherFocus = SwitcherFocus.None;
            ShowPage(value);
        }
    }

    /// <summary>Where the view puts focus when the switcher landed on a page by itself (see <see cref="LandSwitcher"/>).</summary>
    public SwitcherFocus SwitcherFocus
    {
        get => _switcherFocus;
        private set => Set(ref _switcherFocus, value);
    }

    private void ShowPage(SwitcherPage value)
    {
        {
            if (!Set(ref _switcherPage, value, nameof(SwitcherPage)))
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

    private string? _namespaceAccount;
    private string? _namespaceCloud;

    /// <summary>Who the namespace is open as: an account, "Azure sign-in", "connection string" or "synthetic demo".</summary>
    public string? NamespaceAccount => Connect.IsConnected ? _namespaceAccount : null;

    /// <summary>The cloud the namespace lives in, e.g. "Azure public", or the provider's name.</summary>
    public string? NamespaceCloud => Connect.IsConnected ? _namespaceCloud : null;

    /// <summary>The switcher's secondary label: "account · cloud".</summary>
    public string? NamespaceIdentity => NamespaceAccount is { } account ? $"{account} · {NamespaceCloud}" : null;

    public bool HasNamespaceIdentity => NamespaceIdentity is not null;

    /// <summary>The switcher button's tooltip: how the namespace was opened (never a secret).</summary>
    public string NamespaceDetail => Connect.ConnectedTo is { Length: > 0 } to
        ? $"{to} ({NamespaceIdentity}). Click for recent and saved namespaces."
        : "Open the namespace switcher: connect, accounts and saved logins.";

    /// <summary>
    /// The account and cloud for a namespace, from what the connection knows. Never reads a secret.
    /// </summary>
    public static (string Account, string Cloud) IdentityOf(string? endpoint, string? account, string providerName, bool connectionString = false)
    {
        var host = endpoint is { Length: > 0 } e && Uri.TryCreate(e.Contains("://", StringComparison.Ordinal) ? e : "sb://" + e, UriKind.Absolute, out var uri) ? uri.Host : endpoint;
        var cloud = AzureClouds.FromHost(host) is { } c ? CloudLabel(c) : providerName;
        if (connectionString)
            return ("connection string", cloud);
        return (string.IsNullOrWhiteSpace(account) ? "Azure sign-in" : account, cloud);
    }

    private static string CloudLabel(AzureCloud cloud) => cloud.DisplayName.Replace("(", string.Empty, StringComparison.Ordinal).Replace(")", string.Empty, StringComparison.Ordinal);

    private void IdentityChanged()
    {
        OnPropertyChanged(nameof(NamespaceLabel));
        OnPropertyChanged(nameof(NamespaceAccount));
        OnPropertyChanged(nameof(NamespaceCloud));
        OnPropertyChanged(nameof(NamespaceIdentity));
        OnPropertyChanged(nameof(HasNamespaceIdentity));
        OnPropertyChanged(nameof(NamespaceDetail));
        OnPropertyChanged(nameof(MoveBannerText));
        RebuildQuickSwitch();
    }

    // ---- Quick switch (the switcher button's popover) ----

    /// <summary>Recent and saved namespaces for the quick switch.</summary>
    public bool HasQuickSwitchItems => Sources.HasRecent || Connect.HasSavedConnections;

    public bool HasNoQuickSwitchItems => !HasQuickSwitchItems;

    /// <summary>Opens a recent namespace from the quick switch.</summary>
    public AsyncCommand<RecentItemViewModel> QuickOpenRecentCommand { get; }

    /// <summary>Connects a saved login from the quick switch.</summary>
    public AsyncCommand<LoginEntry> QuickConnectSavedCommand { get; }

    /// <summary>"Manage accounts…": the full switcher sheet, on the page with the next step.</summary>
    public RelayCommand ManageAccountsCommand { get; }

    /// <summary>Opens a quick-switch row: its recent namespace or its saved login.</summary>
    public AsyncCommand<QuickSwitchItem> QuickSwitchCommand { get; }

    /// <summary>The quick switch's Recent rows: namespace on top, account and cloud below.</summary>
    public ObservableCollection<QuickSwitchItem> QuickRecent { get; } = [];

    /// <summary>The quick switch's Saved rows: namespace on top, account and cloud below.</summary>
    public ObservableCollection<QuickSwitchItem> QuickSaved { get; } = [];

    private void QuickSwitchChanged()
    {
        OnPropertyChanged(nameof(HasQuickSwitchItems));
        OnPropertyChanged(nameof(HasNoQuickSwitchItems));
        RebuildQuickSwitch();
    }

    private void RebuildQuickSwitch()
    {
        // The constructor wires events before every part exists.
        if (Sources is null || Connect is null || QuickView is null)
            return;
        var open = Connect.IsConnected ? QuickView.NamespaceName : null;
        QuickRecent.Clear();
        foreach (var recent in Sources.Recent)
            QuickRecent.Add(QuickSwitchFor(recent, Connect.SavedConnections, open));
        QuickSaved.Clear();
        foreach (var login in Connect.SavedConnections)
            QuickSaved.Add(QuickSwitchFor(login, open));
    }

    /// <summary>A recent namespace (or a recent use of a saved login) as a two-line quick-switch row.</summary>
    public static QuickSwitchItem QuickSwitchFor(RecentItemViewModel recent, IEnumerable<LoginEntry> saved, string? openNamespace)
    {
        if (recent.Recent.LoginId is { } id && saved.FirstOrDefault(l => l.Id == id) is { } login)
            return QuickSwitchFor(login, openNamespace) with { Recent = recent, Login = null, Icon = recent.Icon };
        if (recent.Recent.Namespace is { } ns)
        {
            var cloud = CloudLabel(AzureClouds.Find(ns.Cloud));
            var account = string.IsNullOrWhiteSpace(recent.Recent.AccountName) ? "Azure sign-in" : recent.Recent.AccountName;
            return new QuickSwitchItem(ns.Name, $"{account} · {cloud}", IsOpen(ns.Name, openNamespace), recent.Icon) { Recent = recent };
        }

        return new QuickSwitchItem(recent.Title, recent.Detail, IsOpen(recent.Title, openNamespace), recent.Icon) { Recent = recent };
    }

    /// <summary>A saved login as a two-line quick-switch row. Reads only the login's non-secret fields.</summary>
    public static QuickSwitchItem QuickSwitchFor(LoginEntry login, string? openNamespace)
    {
        var endpoint = login.Field("endpoint") is { Length: > 0 } given ? given : login.Field("namespace");
        var title = endpoint is { Length: > 0 } e ? HostOf(e).Split('.')[0] : login.DisplayName;
        var connectionString = login.Kind.ToString().EndsWith("ConnectionString", StringComparison.Ordinal);
        var (account, cloud) = IdentityOf(endpoint, login.Field("accountName"), LoginKinds.SystemLabel(login.System), connectionString);
        return new QuickSwitchItem(title, $"{account} · {cloud}", IsOpen(title, openNamespace), "fa-solid fa-bookmark") { Login = login, LoginName = login.DisplayName };
    }

    private static bool IsOpen(string title, string? openNamespace) =>
        openNamespace is { Length: > 0 } && string.Equals(title, openNamespace, StringComparison.OrdinalIgnoreCase);

    private static string HostOf(string endpoint) =>
        Uri.TryCreate(endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "sb://" + endpoint, UriKind.Absolute, out var uri) ? uri.Host : endpoint;

    private async Task QuickOpenRecentAsync(RecentItemViewModel item)
    {
        if (Connect.IsConnected)
            Connect.DisconnectCommand.Execute(null);
        await Sources.OpenRecentAsync(item).ConfigureAwait(true);
    }

    private async Task QuickConnectSavedAsync(LoginEntry login)
    {
        if (Connect.IsConnected)
            Connect.DisconnectCommand.Execute(null);
        Connect.SelectedSaved = login;
        await Connect.ConnectSavedCommand.ExecuteAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Opens the switcher on the page with the next action, from the data: Recent if there are recents, else Saved
    /// if there are saved logins, else Accounts (focus on Add an account on first run; the browser when there are
    /// accounts). Only while nothing is connected and the user hasn't picked a page.
    /// </summary>
    private void LandSwitcher()
    {
        if (!IsSwitcherOpen || Connect.IsConnected || _pagePicked)
            return;
        if (Sources.HasRecent)
        {
            Sources.SelectedTab = ConnectSourcesViewModel.RecentTab;
            ShowPage(SwitcherPage.Connect);
            SwitcherFocus = SwitcherFocus.None;
        }
        else if (Connect.HasSavedConnections)
        {
            Sources.SelectedTab = ConnectSourcesViewModel.SavedTab;
            ShowPage(SwitcherPage.Connect);
            SwitcherFocus = SwitcherFocus.None;
        }
        else
        {
            ShowPage(SwitcherPage.Accounts);
            SwitcherFocus = Accounts.HasAccounts ? SwitcherFocus.Browse : SwitcherFocus.AddAccount;
        }
    }

    private bool _isJobsOpen;

    /// <summary>Test jobs, opened from Settings. Independent of the connection (in-memory broker only).</summary>
    public bool IsJobsOpen
    {
        get => _isJobsOpen;
        set => Set(ref _isJobsOpen, value);
    }

    public RelayCommand OpenJobsCommand { get; }

    public RelayCommand CloseJobsCommand { get; }

    /// <summary>Opens the switcher: on <paramref name="page"/>, or (with none) on the page with the next action.</summary>
    public void OpenSwitcher(SwitcherPage? page = null)
    {
        if (page is { } p)
        {
            SwitcherPage = p;
            IsSwitcherOpen = true;
            return;
        }

        _pagePicked = false;
        IsSwitcherOpen = true;
        LandFor(Connect.IsConnected);
    }

    private void LandFor(bool connected)
    {
        if (!connected)
        {
            LandSwitcher();
            return;
        }

        if (Sources.HasRecent || Connect.HasSavedConnections)
        {
            Sources.SelectedTab = Sources.HasRecent ? ConnectSourcesViewModel.RecentTab : ConnectSourcesViewModel.SavedTab;
            ShowPage(SwitcherPage.Connect);
            SwitcherFocus = SwitcherFocus.None;
        }
        else
        {
            ShowPage(SwitcherPage.Accounts);
            SwitcherFocus = Accounts.HasAccounts ? SwitcherFocus.Browse : SwitcherFocus.AddAccount;
        }
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
        (_namespaceAccount, _namespaceCloud) = IdentityFor(inventory, connection, actor ?? ActorFromLabel());
        IdentityChanged();
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

    private static (string Account, string Cloud) IdentityFor(IEntityInventory inventory, BrokerConnection? connection, string? actor) => inventory switch
    {
        Adapters.ServiceBus.Arm.ArmEntityInventory arm => (actor ?? "Azure sign-in", CloudLabel(AzureClouds.Find(arm.Namespace.Cloud))),
        _ when inventory.Provider.Id == SyntheticDemoProvider.Id => ("synthetic demo", inventory.Provider.DisplayName),
        _ => IdentityOf(connection?.Endpoint, actor, inventory.Provider.DisplayName, connectionString: connection?.AuthMode == AuthMode.ConnectionString),
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
        IdentityChanged();
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
