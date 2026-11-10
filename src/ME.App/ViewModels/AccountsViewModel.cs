// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Azure.Core;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// The Accounts screen. Sign in with one or more Entra accounts; each account's Service Bus namespaces are
/// found across all of its tenants and subscriptions with no extra clicks. One account failing shows an
/// error on that account only. Picking a namespace opens the grids with that account's credential.
/// </summary>
public sealed class AccountsViewModel : ViewModelBase, IAccountCredentialSource
{
    private readonly IAccountAuthenticator _authenticator;
    private readonly IAccountStore _store;
    private readonly IArmReader _arm;
    private readonly Func<IEntityInventory, string, Task> _open;
    private string _tenantText = string.Empty;
    private string? _message;
    private DiscoveryNode? _selectedNode;

    public AccountsViewModel(IAccountAuthenticator authenticator, IAccountStore store, IArmReader arm, Func<IEntityInventory, string, Task> open)
    {
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _arm = arm ?? throw new ArgumentNullException(nameof(arm));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        AddAccountCommand = new AsyncCommand(AddAccountAsync);
        OpenCommand = new AsyncCommand(() => OpenAsync(SelectedNode), () => SelectedNode is { IsNamespace: true });
    }

    public ObservableCollection<AccountItemViewModel> Accounts { get; } = [];

    /// <summary>Account &gt; Subscription &gt; Namespace, rebuilt whenever an account changes.</summary>
    public ObservableCollection<DiscoveryNode> Tree { get; } = [];

    /// <summary>Optional tenant ID or domain to sign in to. Empty means any work or school account.</summary>
    public string TenantText
    {
        get => _tenantText;
        set => Set(ref _tenantText, value ?? string.Empty);
    }

    public string ClientIdText => $"Signs in with the app ID {_authenticator.ClientId} in your browser.";

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

    public bool HasAccounts => Accounts.Count > 0;

    public string EmptyText => "No accounts yet. Add an account to sign in with Microsoft Entra ID. Your Service Bus namespaces will show up here.";

    public string Summary
    {
        get
        {
            var namespaces = Namespaces.Count;
            var accounts = Accounts.Count == 1 ? "1 account" : $"{Accounts.Count} accounts";
            return $"{accounts}, {(namespaces == 1 ? "1 namespace" : $"{namespaces} namespaces")}";
        }
    }

    /// <summary>Every namespace from every signed-in account, in one list.</summary>
    public IReadOnlyList<NamespaceRef> Namespaces => Accounts.SelectMany(a => a.Namespaces).ToList();

    /// <summary>The first signed-in account's credential, for a typed namespace or an endpoint-only string.</summary>
    public TokenCredential? DefaultCredential => Accounts.FirstOrDefault(a => a.Credential is not null)?.Credential;

    public string? DefaultUsername => Accounts.FirstOrDefault(a => a.Credential is not null)?.Username;

    public DiscoveryNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (Set(ref _selectedNode, value))
                OpenCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand AddAccountCommand { get; }

    public AsyncCommand OpenCommand { get; }

    /// <summary>On launch: bring back every stored account silently. Signed-out accounts stay signed out.</summary>
    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StoredAccount> stored;
        try
        {
            stored = _store.Load();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Message = "Saved accounts could not be read. Add your accounts again.";
            return;
        }

        var pending = new List<Task>();
        foreach (var account in stored)
        {
            var item = Track(account);
            if (!account.SignedOut)
                pending.Add(SignInSilentlyAsync(item, cancellationToken));
        }

        Rebuild();
        await Task.WhenAll(pending).ConfigureAwait(true);
    }

    public async Task AddAccountAsync()
    {
        Message = null;
        SignedInAccount signedIn;
        try
        {
            signedIn = await _authenticator.SignInInteractiveAsync(Blank(TenantText), null, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Message = "Sign-in did not finish. " + Explain(exception);
            return;
        }
        catch (OperationCanceledException)
        {
            Message = "Sign-in was cancelled.";
            return;
        }

        var item = Accounts.FirstOrDefault(a => a.Id == signedIn.Account.Id) ?? Track(signedIn.Account);
        await UseAsync(item, signedIn, CancellationToken.None).ConfigureAwait(true);
    }

    public async Task OpenAsync(DiscoveryNode? node)
    {
        if (node is not { Namespace: { } ns, Account: { Credential: { } credential } account })
            return;
        Message = null;
        var inventory = new ArmEntityInventory(_arm, credential, ns);
        await _open(inventory, $"{ns.Name} as {account.Username}").ConfigureAwait(true);
    }

    private AccountItemViewModel Track(StoredAccount account)
    {
        var item = new AccountItemViewModel(account, SignInAgainAsync, SignOut, Remove);
        Accounts.Add(item);
        OnPropertyChanged(nameof(HasAccounts));
        return item;
    }

    private async Task SignInSilentlyAsync(AccountItemViewModel item, CancellationToken cancellationToken)
    {
        item.State = AccountState.SigningIn;
        SignedInAccount signedIn;
        try
        {
            signedIn = await _authenticator.SignInSilentAsync(item.Account, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.Credential = null;
            item.ErrorMessage = Explain(exception);
            item.State = AccountState.NeedsSignIn;
            Rebuild();
            return;
        }

        await UseAsync(item, signedIn, cancellationToken).ConfigureAwait(true);
    }

    private async Task SignInAgainAsync(AccountItemViewModel item)
    {
        item.ErrorMessage = null;
        item.State = AccountState.SigningIn;
        SignedInAccount signedIn;
        try
        {
            signedIn = await _authenticator.SignInInteractiveAsync(item.Account.RequestedTenant, item.Username, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            item.ErrorMessage = exception is OperationCanceledException ? "Sign-in was cancelled." : Explain(exception);
            item.State = item.Account.SignedOut ? AccountState.SignedOut : AccountState.NeedsSignIn;
            Rebuild();
            return;
        }

        var target = signedIn.Account.Id == item.Id ? item : Accounts.FirstOrDefault(a => a.Id == signedIn.Account.Id) ?? Track(signedIn.Account);
        if (!ReferenceEquals(target, item))
            item.State = item.Account.SignedOut ? AccountState.SignedOut : AccountState.NeedsSignIn;
        await UseAsync(target, signedIn, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task UseAsync(AccountItemViewModel item, SignedInAccount signedIn, CancellationToken cancellationToken)
    {
        item.Account = signedIn.Account with { SignedOut = false };
        item.Credential = signedIn.Credential;
        item.ErrorMessage = null;
        item.State = AccountState.Discovering;
        _store.Save(item.Account);
        Rebuild();

        var discovery = await NamespaceDiscovery.DiscoverAsync(_arm, signedIn.Credential, cancellationToken).ConfigureAwait(true);
        if (!ReferenceEquals(item.Credential, signedIn.Credential))
            return; // Signed out or removed while discovery ran.
        item.Discovery = discovery;
        item.ErrorMessage = discovery.Error;
        item.State = discovery.Error is null ? AccountState.Ready : AccountState.Error;
        Rebuild();
    }

    private void SignOut(AccountItemViewModel item)
    {
        item.Credential = null;
        item.Discovery = null;
        item.ErrorMessage = null;
        item.Account = item.Account with { SignedOut = true };
        item.State = AccountState.SignedOut;
        _store.Save(item.Account);
        Rebuild();
    }

    private void Remove(AccountItemViewModel item)
    {
        item.Credential = null;
        _store.Delete(item.Id);
        Accounts.Remove(item);
        OnPropertyChanged(nameof(HasAccounts));
        Rebuild();
    }

    private void Rebuild()
    {
        Tree.Clear();
        foreach (var account in Accounts)
            Tree.Add(BuildAccountNode(account));
        if (SelectedNode is not null && !Tree.SelectMany(Flatten).Contains(SelectedNode))
            SelectedNode = null;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Namespaces));
        OnPropertyChanged(nameof(DefaultCredential));
        OnPropertyChanged(nameof(DefaultUsername));
    }

    private static DiscoveryNode BuildAccountNode(AccountItemViewModel account)
    {
        var node = new DiscoveryNode(DiscoveryNodeKind.Account, account.Username, account.StatusText, account: account);
        if (account.ErrorMessage is { } error)
            node.Children.Add(new DiscoveryNode(DiscoveryNodeKind.Error, "Error", error, account: account));
        if (account.Discovery is not { } discovery)
            return node;

        var multipleTenants = discovery.Tenants.Count > 1;
        foreach (var tenant in discovery.Tenants)
        {
            if (tenant.Error is not null)
                node.Children.Add(new DiscoveryNode(DiscoveryNodeKind.Error, $"Tenant {tenant.Tenant.DisplayName}", tenant.Error, account: account));
            foreach (var subscription in tenant.Subscriptions)
            {
                if (subscription.Error is null && subscription.Namespaces.Count == 0)
                    continue;
                var detail = multipleTenants ? $"Subscription in {tenant.Tenant.DisplayName}" : "Subscription";
                var subscriptionNode = new DiscoveryNode(DiscoveryNodeKind.Subscription, subscription.Subscription.DisplayName, detail, account: account);
                if (subscription.Error is not null)
                    subscriptionNode.Children.Add(new DiscoveryNode(DiscoveryNodeKind.Error, "Error", subscription.Error, account: account));
                foreach (var ns in subscription.Namespaces)
                    subscriptionNode.Children.Add(new DiscoveryNode(DiscoveryNodeKind.Namespace, ns.Name, $"{ns.ResourceGroup} · {ns.Location}", ns, account));
                node.Children.Add(subscriptionNode);
            }
        }

        if (account.State == AccountState.Ready && !discovery.Namespaces.Any())
            node.Children.Add(new DiscoveryNode(DiscoveryNodeKind.Message, "No Service Bus namespaces you can read", "Ask for the Reader role on a namespace, resource group or subscription.", account: account));
        return node;
    }

    private static IEnumerable<DiscoveryNode> Flatten(DiscoveryNode node) => node.Children.SelectMany(Flatten).Prepend(node);

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Explain(Exception exception) => exception switch
    {
        Azure.Identity.AuthenticationRequiredException => "The saved sign-in has expired or needs approval again.",
        Azure.Identity.AuthenticationFailedException failed => FirstLine(failed.Message),
        _ => ServiceBusErrorMapper.Map(exception).Message,
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 240 ? line[..240] + "..." : line;
    }
}
