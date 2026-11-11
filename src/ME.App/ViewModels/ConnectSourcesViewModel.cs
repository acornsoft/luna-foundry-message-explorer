// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.Settings;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum BrowseNodeKind
{
    Subscription,
    ResourceGroup,
    Namespace,
    Message,
}

/// <summary>One row of the Browse tree: subscription, then resource group, then Service Bus or Event Hubs namespace.</summary>
public sealed class BrowseNode : ViewModelBase
{
    private bool _isExpanded;

    public BrowseNode(BrowseNodeKind kind, string label, string? detail, NamespaceRef? ns = null, AccountItemViewModel? account = null)
    {
        Kind = kind;
        Label = label;
        Detail = detail;
        Namespace = ns;
        Account = account;
    }

    public BrowseNodeKind Kind { get; }

    public string Label { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public NamespaceRef? Namespace { get; }

    public AccountItemViewModel? Account { get; }

    public ObservableCollection<BrowseNode> Children { get; } = [];

    public bool IsNamespace => Namespace is not null;

    public bool IsEventHubs => Namespace is { Kind: NamespaceKind.EventHubs };

    /// <summary>Font Awesome icon: key for a subscription (as in the Azure portal), folder, then the namespace kind.</summary>
    public string Icon => Kind switch
    {
        BrowseNodeKind.Subscription => "fa-solid fa-key",
        BrowseNodeKind.ResourceGroup => "fa-regular fa-folder",
        BrowseNodeKind.Namespace when IsEventHubs => "fa-solid fa-wave-square",
        BrowseNodeKind.Namespace => "fa-solid fa-envelopes-bulk",
        _ => "fa-solid fa-circle-info",
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public override string ToString() => Label;
}

/// <summary>A subscription the Browse filter can narrow to. "All subscriptions" has no ID.</summary>
public sealed record SubscriptionFilter(string? SubscriptionId, string Label)
{
    public static SubscriptionFilter All { get; } = new(null, "All subscriptions");

    public override string ToString() => Label;
}

/// <summary>One line of the Recent list.</summary>
public sealed class RecentItemViewModel(RecentConnection recent, string when)
{
    public RecentConnection Recent { get; } = recent;

    public string Title => Recent.Label;

    public string Detail => Recent.LoginId is not null
        ? $"Saved login · {when}"
        : $"{(Recent.Namespace?.Kind == NamespaceKind.EventHubs ? "Event Hubs" : "Service Bus")} · {Recent.AccountName} · {when}";

    public string Icon => Recent.LoginId is not null ? "fa-solid fa-bookmark" : "fa-solid fa-clock-rotate-left";

    public override string ToString() => Title;
}

/// <summary>
/// Where a connection comes from, as in Azure Data Studio's connection dialog: Recent and Saved on the Connect
/// panel, and Browse on the Accounts page (next to the accounts it lists). Browse walks every signed-in account's subscriptions, then resource groups, then Service Bus and Event Hubs
/// namespaces, all from read-only ARM lists that already ran on the Accounts page. Each account keeps its own
/// cloud. Recent keeps no secrets: saved logins by ID, namespaces by their ARM identity and account.
/// </summary>
public sealed class ConnectSourcesViewModel : ViewModelBase
{
    public const int RecentTab = 0;
    public const int SavedTab = 1;

    private readonly AccountsViewModel _accounts;
    private readonly ConnectViewModel _connect;
    private readonly IUiSettingsStore _store;
    private readonly ICredentialVault? _vault;
    private readonly TimeProvider _time;
    private int _tab;
    private string _filterText = string.Empty;
    private SubscriptionFilter _subscription = SubscriptionFilter.All;
    private BrowseNode? _selectedNode;
    private RecentItemViewModel? _selectedRecent;
    private string? _message;
    private bool _messageIsError;

    public ConnectSourcesViewModel(AccountsViewModel accounts, ConnectViewModel connect, IUiSettingsStore store, ICredentialVault? vault = null, TimeProvider? time = null)
    {
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vault = vault;
        _time = time ?? TimeProvider.System;
        OpenCommand = new AsyncCommand(OpenSelectedAsync, () => SelectedNode is { IsNamespace: true, IsEventHubs: false, Account.IsSignedIn: true });
        SaveHubCommand = new AsyncCommand(SaveHubAsync, () => SelectedNode is { IsEventHubs: true } && _vault is not null);
        OpenRecentCommand = new AsyncCommand(() => OpenRecentAsync(SelectedRecent), () => SelectedRecent is not null);
        ForgetRecentCommand = new RelayCommand(ForgetSelected, () => SelectedRecent is not null);
        ClearRecentCommand = new RelayCommand(ClearRecent, () => Recent.Count > 0);
        ClearFilterCommand = new RelayCommand(() => { FilterText = string.Empty; Subscription = SubscriptionFilter.All; });

        _accounts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AccountsViewModel.Namespaces) or nameof(AccountsViewModel.SignedInAccounts))
                Rebuild();
        };
        _accounts.NamespaceOpened += (ns, account) => Remember(new RecentConnection($"{ns.Name} as {account.Username}", _time.GetUtcNow(), Namespace: ns, AccountId: account.Id, AccountName: account.Username));
        _connect.LoginUsed += login => Remember(new RecentConnection(login.DisplayName, _time.GetUtcNow(), LoginId: login.Id));
        LoadRecent();
        Rebuild();
        _tab = Recent.Count > 0 ? RecentTab : SavedTab;
    }

    /// <summary>The Connect panel, for the Saved tab.</summary>
    public ConnectViewModel Connect => _connect;

    /// <summary>0 Recent, 1 Saved, on the Connect panel. Opens on Recent when there is something recent, else Saved.</summary>
    public int SelectedTab
    {
        get => _tab;
        set => Set(ref _tab, Math.Clamp(value, RecentTab, SavedTab));
    }

    // Recent ------------------------------------------------------------------------------------------------

    public ObservableCollection<RecentItemViewModel> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    public string RecentEmptyText => "Nothing yet. Open a namespace from Browse, or connect a saved login, and it shows up here. Pasted strings you don't save are never kept.";

    public RecentItemViewModel? SelectedRecent
    {
        get => _selectedRecent;
        set
        {
            if (Set(ref _selectedRecent, value))
            {
                OpenRecentCommand.RaiseCanExecuteChanged();
                ForgetRecentCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand OpenRecentCommand { get; }

    public RelayCommand ForgetRecentCommand { get; }

    public RelayCommand ClearRecentCommand { get; }

    // Browse ------------------------------------------------------------------------------------------------

    /// <summary>Subscription &gt; resource group &gt; namespace, after the filters.</summary>
    public ObservableCollection<BrowseNode> Tree { get; } = [];

    public ObservableCollection<SubscriptionFilter> Subscriptions { get; } = [SubscriptionFilter.All];

    public SubscriptionFilter Subscription
    {
        get => _subscription;
        set
        {
            if (Set(ref _subscription, value ?? SubscriptionFilter.All))
                Rebuild();
        }
    }

    /// <summary>Matches a namespace or resource group name, any case.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (Set(ref _filterText, value ?? string.Empty))
                Rebuild();
        }
    }

    public RelayCommand ClearFilterCommand { get; }

    public bool HasTree => Tree.Count > 0;

    public string BrowseSummary { get; private set; } = string.Empty;

    public string BrowseEmptyText => _accounts.SignedInAccounts.Count == 0
        ? "Sign in on the Accounts page and your subscriptions show up here."
        : _filterText.Length > 0 || _subscription.SubscriptionId is not null
            ? "Nothing matches the filter."
            : "No Service Bus or Event Hubs namespaces your accounts can read.";

    public BrowseNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (Set(ref _selectedNode, value))
            {
                OpenCommand.RaiseCanExecuteChanged();
                SaveHubCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(SelectionNote));
                OnPropertyChanged(nameof(IsHubSelected));
            }
        }
    }

    public bool IsHubSelected => SelectedNode is { IsEventHubs: true };

    public string? SelectionNote => SelectedNode switch
    {
        { IsEventHubs: true, Namespace: { } ns } => $"{ns.FullyQualifiedNamespace}: Event Hubs. The grids read Service Bus only, so save it as an Event Hubs login for the Event Hubs reader.",
        { IsNamespace: true, Namespace: { } ns, Account: { } account } => $"{ns.FullyQualifiedNamespace} as {account.Username}. Read-only: counts come from Azure Resource Manager.",
        _ => null,
    };

    /// <summary>Opens the selected Service Bus namespace with its account, as Open does on the Accounts page.</summary>
    public AsyncCommand OpenCommand { get; }

    /// <summary>Saves the selected Event Hubs namespace as an Event Hubs login with its account. No secret.</summary>
    public AsyncCommand SaveHubCommand { get; }

    public string? Message
    {
        get => _message;
        private set
        {
            if (Set(ref _message, value))
                OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public bool MessageIsError
    {
        get => _messageIsError;
        private set => Set(ref _messageIsError, value);
    }

    public async Task OpenAsync(BrowseNode? node)
    {
        if (node is not { Namespace: { Kind: NamespaceKind.ServiceBus } ns, Account: { } account })
            return;
        Say(null);
        if (!account.IsSignedIn)
        {
            Say($"Sign in as {account.Username} on the Accounts page first.", error: true);
            return;
        }

        await _accounts.OpenNamespaceAsync(ns, account).ConfigureAwait(true);
    }

    private Task OpenSelectedAsync() => OpenAsync(SelectedNode);

    private async Task SaveHubAsync()
    {
        if (SelectedNode is not { Namespace: { Kind: NamespaceKind.EventHubs } ns, Account: { } account } || _vault is null)
            return;
        var fields = new Dictionary<string, string> { ["namespace"] = ns.FullyQualifiedNamespace, ["accountName"] = account.Username };
        try
        {
            await _vault.AddAsync(new LoginDraft(ns.Name, LoginKind.EventHubsEntra, fields, account.Id), null).ConfigureAwait(true);
            Say($"Saved '{ns.Name}' to Saved logins as an Event Hubs login. No secret is stored.", error: false);
        }
        catch (Exception exception) when (exception is LoginRejectedException or SecretStoreException or IOException)
        {
            Say("Not saved: " + exception.Message, error: true);
        }
    }

    public async Task OpenRecentAsync(RecentItemViewModel? item)
    {
        if (item is null)
            return;
        Say(null);
        var recent = item.Recent;
        if (recent.LoginId is { } loginId)
        {
            var login = _connect.SavedConnections.FirstOrDefault(l => l.Id == loginId);
            if (login is null)
            {
                Say($"'{recent.Label}' is no longer in Saved logins.", error: true);
                return;
            }

            _connect.SelectedSaved = login;
            if (_connect.IsConnected)
                _connect.DisconnectCommand.Execute(null);
            await _connect.ConnectSavedCommand.ExecuteAsync().ConfigureAwait(true);
            return;
        }

        if (recent.Namespace is not { } ns)
            return;
        var account = _accounts.Accounts.FirstOrDefault(a => a.Id == recent.AccountId && a.IsSignedIn);
        if (account is null)
        {
            Say($"Sign in as {recent.AccountName} on the Accounts page to open {ns.Name} again.", error: true);
            return;
        }

        await _accounts.OpenNamespaceAsync(ns, account).ConfigureAwait(true);
    }

    private void Say(string? message, bool error = false)
    {
        MessageIsError = error;
        Message = message;
    }

    private void Remember(RecentConnection recent)
    {
        var data = SafeLoad();
        var list = data.Recent.Where(r => !r.SameAs(recent)).Prepend(recent).Take(UiSettingsData.MaxRecent).ToList();
        SafeSave(data with { Recent = list });
        LoadRecent();
    }

    private void ForgetSelected()
    {
        if (SelectedRecent is not { } item)
            return;
        var data = SafeLoad();
        SafeSave(data with { Recent = data.Recent.Where(r => !r.SameAs(item.Recent)).ToList() });
        LoadRecent();
    }

    private void ClearRecent()
    {
        SafeSave(SafeLoad() with { Recent = [] });
        LoadRecent();
    }

    private UiSettingsData SafeLoad()
    {
        try
        {
            return _store.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new UiSettingsData();
        }
    }

    private void SafeSave(UiSettingsData data)
    {
        try
        {
            _store.Save(data);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Say("Recent connections could not be saved: " + exception.Message, error: true);
        }
    }

    private void LoadRecent()
    {
        var now = _time.GetUtcNow();
        Recent.Clear();
        foreach (var recent in SafeLoad().Recent.OrderByDescending(r => r.UsedAt))
            Recent.Add(new RecentItemViewModel(recent, Ago(now - recent.UsedAt)));
        SelectedRecent = null;
        OnPropertyChanged(nameof(HasRecent));
        ClearRecentCommand.RaiseCanExecuteChanged();
    }

    internal static string Ago(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)span.TotalMinutes} min ago",
        < 60 * 24 => $"{(int)span.TotalHours} h ago",
        _ => $"{(int)span.TotalDays} d ago",
    };

    private void Rebuild()
    {
        var accounts = _accounts.Accounts.Where(a => a.IsSignedIn && a.Discovery is not null).ToList();
        var several = accounts.Count > 1;
        var subscriptions = accounts
            .SelectMany(a => a.Discovery!.Tenants.SelectMany(t => t.Subscriptions.Select(s => (Account: a, Tenant: t.Tenant, Found: s))))
            .ToList();

        // Keep the subscription filter's list in step with what is signed in; a vanished choice falls back to All.
        var choices = subscriptions
            .Select(s => new SubscriptionFilter(s.Found.Subscription.SubscriptionId, several ? $"{s.Found.Subscription.DisplayName} ({s.Account.Username})" : s.Found.Subscription.DisplayName))
            .DistinctBy(c => c.SubscriptionId)
            .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!Subscriptions.Skip(1).SequenceEqual(choices))
        {
            // Edit in place after "All subscriptions", so the picker never loses its selection mid-rebuild.
            var keep = _subscription.SubscriptionId;
            while (Subscriptions.Count > 1)
                Subscriptions.RemoveAt(Subscriptions.Count - 1);
            foreach (var choice in choices)
                Subscriptions.Add(choice);
            var kept = Subscriptions.FirstOrDefault(c => c.SubscriptionId == keep) ?? SubscriptionFilter.All;
            if (!ReferenceEquals(kept, _subscription))
            {
                _subscription = kept;
                OnPropertyChanged(nameof(Subscription));
            }
        }

        var filter = _filterText.Trim();
        bool Matches(NamespaceRef ns) =>
            filter.Length == 0
            || ns.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || ns.ResourceGroup.Contains(filter, StringComparison.OrdinalIgnoreCase);

        var selectedId = SelectedNode?.Namespace?.ResourceId;
        Tree.Clear();
        int serviceBus = 0, hubs = 0;
        BrowseNode? reselect = null;
        foreach (var (account, tenant, found) in subscriptions)
        {
            if (_subscription.SubscriptionId is { } only && !string.Equals(only, found.Subscription.SubscriptionId, StringComparison.OrdinalIgnoreCase))
                continue;
            var namespaces = found.Namespaces.Concat(found.EventHubs).Where(Matches).ToList();
            if (namespaces.Count == 0 && (filter.Length > 0 || found.Error is null))
                continue;
            var where = several ? $"{account.Username} · {tenant.DisplayName}" : tenant.DisplayName;
            var subscriptionNode = new BrowseNode(BrowseNodeKind.Subscription, found.Subscription.DisplayName, where, account: account) { IsExpanded = true };
            if (found.Error is { } error)
                subscriptionNode.Children.Add(new BrowseNode(BrowseNodeKind.Message, "Could not read everything", error, account: account));
            foreach (var group in namespaces.GroupBy(n => n.ResourceGroup, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var groupNode = new BrowseNode(BrowseNodeKind.ResourceGroup, group.Key.Length > 0 ? group.Key : "(no resource group)", "Resource group", account: account) { IsExpanded = true };
                foreach (var ns in group.OrderBy(n => n.Kind).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
                {
                    var kind = ns.Kind == NamespaceKind.EventHubs ? "Event Hubs" : "Service Bus";
                    var node = new BrowseNode(BrowseNodeKind.Namespace, ns.Name, $"{kind} · {ns.Location}", ns, account);
                    if (ns.Kind == NamespaceKind.EventHubs)
                        hubs++;
                    else
                        serviceBus++;
                    if (selectedId is not null && string.Equals(ns.ResourceId, selectedId, StringComparison.OrdinalIgnoreCase))
                        reselect = node;
                    groupNode.Children.Add(node);
                }

                subscriptionNode.Children.Add(groupNode);
            }

            Tree.Add(subscriptionNode);
        }

        SelectedNode = reselect;
        BrowseSummary = $"{Plural(serviceBus, "Service Bus namespace")}, {Plural(hubs, "Event Hubs namespace")}";
        OnPropertyChanged(nameof(BrowseSummary));
        OnPropertyChanged(nameof(HasTree));
        OnPropertyChanged(nameof(BrowseEmptyText));
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
