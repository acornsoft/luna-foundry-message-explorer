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
    private readonly ISignInLog _log;
    private readonly TimeSpan _signInTimeout;
    private CancellationTokenSource? _signIn;

    /// <summary>How long a browser sign-in may take before it is stopped and the button comes back.</summary>
    public static readonly TimeSpan DefaultSignInTimeout = TimeSpan.FromMinutes(3);

    public AccountsViewModel(
        IAccountAuthenticator authenticator,
        IAccountStore store,
        IArmReader arm,
        Func<IEntityInventory, string, Task> open,
        ISignInLog? log = null,
        TimeSpan? signInTimeout = null,
        IReadOnlyList<BrowserChoice>? browsers = null)
    {
        Browsers = browsers ?? [BrowserChoices.SystemDefault, BrowserChoices.CopyLinkOnly];
        _selectedBrowser = BrowserChoices.SystemDefault;
        _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _arm = arm ?? throw new ArgumentNullException(nameof(arm));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _log = log ?? new MemorySignInLog();
        _signInTimeout = signInTimeout ?? DefaultSignInTimeout;
        AddAccountCommand = new AsyncCommand(AddAccountAsync, () => !IsSigningIn);
        SignInWithCodeCommand = new AsyncCommand(SignInWithCodeAsync, () => !IsSigningIn);
        CopySignInLinkCommand = new AsyncCommand(CopySignInLinkAsync, () => SignInLink is not null);
        CancelSignInCommand = new RelayCommand(CancelSignIn, () => IsSigningIn);
        OpenCommand = new AsyncCommand(() => OpenAsync(SelectedNode), () => SelectedNode is { IsNamespace: true });
    }

    private BrowserChoice _selectedBrowser;
    private Uri? _signInLink;
    private string? _deviceCodeText;

    /// <summary>System default, each browser found on this computer, then "don't open a browser".</summary>
    public IReadOnlyList<BrowserChoice> Browsers { get; }

    /// <summary>Where Add account opens the sign-in page. The account remembers it for later sign-ins.</summary>
    public BrowserChoice SelectedBrowser
    {
        get => _selectedBrowser;
        set => Set(ref _selectedBrowser, value ?? BrowserChoices.SystemDefault);
    }

    /// <summary>The sign-in link of the sign-in that is waiting. Paste it into any browser on this computer.</summary>
    public Uri? SignInLink
    {
        get => _signInLink;
        private set
        {
            if (Set(ref _signInLink, value))
            {
                OnPropertyChanged(nameof(HasSignInLink));
                CopySignInLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasSignInLink => _signInLink is not null;

    /// <summary>The device code instructions while Sign in with a code waits.</summary>
    public string? DeviceCodeText
    {
        get => _deviceCodeText;
        private set
        {
            if (Set(ref _deviceCodeText, value))
                OnPropertyChanged(nameof(HasDeviceCode));
        }
    }

    public bool HasDeviceCode => !string.IsNullOrEmpty(_deviceCodeText);

    /// <summary>Puts text on the clipboard. The view sets it; tests use a fake.</summary>
    public Func<string, Task>? Clipboard { get; set; }

    public AsyncCommand SignInWithCodeCommand { get; }

    public AsyncCommand CopySignInLinkCommand { get; }

    /// <summary>True while a browser sign-in is waiting. Cancel sign-in stops it.</summary>
    public bool IsSigningIn => _signIn is not null;

    public RelayCommand CancelSignInCommand { get; }

    /// <summary>Where the sign-in log is, shown with errors. Null in tests and the demo.</summary>
    public string? LogLocation => _log.Location;

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

    /// <summary>Every signed-in account, for the account picker on the Connect panel and in Saved logins.</summary>
    public IReadOnlyList<AccountChoice> SignedInAccounts => Accounts
        .Where(a => a.IsSignedIn)
        .Select(a => new AccountChoice(a.Id, a.Username, a.Credential!))
        .ToList();

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
        var browser = SelectedBrowser;
        var outcome = await InteractiveAsync("add account", browser,
            (prompt, token) => _authenticator.SignInInteractiveAsync(Blank(TenantText), null, prompt, token)).ConfigureAwait(true);
        if (outcome.Error is { } error)
        {
            Message = error;
            return;
        }

        var signedIn = outcome.Account!;
        signedIn = signedIn with { Account = signedIn.Account with { Browser = browser.Id } };
        var item = Accounts.FirstOrDefault(a => a.Id == signedIn.Account.Id) ?? Track(signedIn.Account);
        await UseAsync(item, signedIn, CancellationToken.None).ConfigureAwait(true);
    }

    /// <summary>Device code sign-in, for when no browser on this computer fits: enter the code in any browser, on any device.</summary>
    public async Task SignInWithCodeAsync()
    {
        Message = null;
        var outcome = await InteractiveAsync("sign in with a code", SelectedBrowser,
            (prompt, token) => _authenticator.SignInWithDeviceCodeAsync(Blank(TenantText), prompt, token)).ConfigureAwait(true);
        if (outcome.Error is { } error)
        {
            Message = error;
            return;
        }

        var signedIn = outcome.Account!;
        var item = Accounts.FirstOrDefault(a => a.Id == signedIn.Account.Id) ?? Track(signedIn.Account);
        await UseAsync(item, signedIn, CancellationToken.None).ConfigureAwait(true);
    }

    private async Task CopySignInLinkAsync()
    {
        if (SignInLink is not { } link || Clipboard is not { } clipboard)
            return;
        await clipboard(link.AbsoluteUri).ConfigureAwait(true);
        _log.Write("sign-in link copied to the clipboard");
    }

    /// <summary>Stops the browser sign-in that is waiting, so Add account works again at once.</summary>
    public void CancelSignIn()
    {
        if (_signIn is null)
            return;
        _log.Write("sign-in cancelled by the user");
        _userCancelled = true;
        _signIn.Cancel();
    }

    private sealed record Outcome(SignedInAccount? Account, string? Error);

    /// <summary>
    /// One browser sign-in, always bounded: it ends on success, on any error, on Cancel sign-in, or after the
    /// timeout. The token source is disposed every time, so nothing from a failed try is left to block the next.
    /// The real error goes to the UI and the sign-in log.
    /// </summary>
    private async Task<Outcome> InteractiveAsync(string what, BrowserChoice browser, Func<SignInPrompt, CancellationToken, Task<SignedInAccount>> run)
    {
        _signIn?.Cancel();
        using var attempt = new CancellationTokenSource(_signInTimeout);
        _signIn = attempt;
        SignInLink = null;
        DeviceCodeText = null;
        SigningInChanged();
        _log.Write($"{what}: started (client {_authenticator.ClientId}, browser {browser.Name}, timeout {_signInTimeout.TotalSeconds:0}s)");
        // MSAL calls back on its own thread; the screen is updated on the thread that started the sign-in.
        var ui = SynchronizationContext.Current;
        void OnUi(Action action)
        {
            if (ui is null || ReferenceEquals(SynchronizationContext.Current, ui))
                action();
            else
                ui.Post(_ => action(), null);
        }

        // A callback that arrives after this attempt ended (failed, cancelled) is dropped, so a stale link never lingers.
        var prompt = new SignInPrompt(
            browser,
            link => OnUi(() =>
            {
                if (ReferenceEquals(_signIn, attempt))
                    SignInLink = link;
            }),
            text => OnUi(() =>
            {
                if (ReferenceEquals(_signIn, attempt))
                    DeviceCodeText = text;
            }));
        try
        {
            var account = await run(prompt, attempt.Token).ConfigureAwait(true);
            _log.Write($"{what}: signed in (tenant {account.Account.TenantId})");
            return new Outcome(account, null);
        }
        catch (OperationCanceledException) when (attempt.IsCancellationRequested && !_userCancelled)
        {
            _log.Write($"{what}: timed out after {_signInTimeout.TotalSeconds:0}s waiting for the browser");
            return new Outcome(null, $"Sign-in timed out after {Minutes(_signInTimeout)}: the browser never returned to the app. Try again.{LogHint()}");
        }
        catch (OperationCanceledException)
        {
            _log.Write($"{what}: cancelled");
            return new Outcome(null, "Sign-in was cancelled.");
        }
        catch (Exception exception)
        {
            _log.Write($"{what}: failed: {Describe(exception)}");
            return new Outcome(null, "Sign-in did not finish. " + Explain(exception) + LogHint());
        }
        finally
        {
            if (ReferenceEquals(_signIn, attempt))
            {
                _signIn = null;
                _userCancelled = false;
                SignInLink = null;
                DeviceCodeText = null;
            }

            SigningInChanged();
        }
    }

    private bool _userCancelled;

    private void SigningInChanged()
    {
        OnPropertyChanged(nameof(IsSigningIn));
        CancelSignInCommand.RaiseCanExecuteChanged();
        AddAccountCommand.RaiseCanExecuteChanged();
        SignInWithCodeCommand.RaiseCanExecuteChanged();
    }

    private string LogHint() => _log.Location is { } location ? $" Details: {location}" : string.Empty;

    private static string Minutes(TimeSpan span) => span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0} minutes" : $"{span.TotalSeconds:0} seconds";

    /// <summary>Type and first message line of the exception and each inner one. Scrubbed; no tokens.</summary>
    internal static string Describe(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null && parts.Count < 4; current = current.InnerException)
            parts.Add($"{current.GetType().FullName}: {FirstLine(current.Message)}");
        return LogScrubber.Scrub(string.Join(" <- ", parts));
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
        var item = new AccountItemViewModel(account, SignInAgainAsync, SignOut, Remove, Browsers, SaveBrowser);
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
            _log.Write($"silent sign-in for a saved account failed: {Describe(exception)}");
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
        var outcome = await InteractiveAsync("sign in again", item.Browser,
            (prompt, token) => _authenticator.SignInInteractiveAsync(item.Account.RequestedTenant, item.Username, prompt, token)).ConfigureAwait(true);
        if (outcome.Error is { } error)
        {
            item.ErrorMessage = error;
            item.State = item.Account.SignedOut ? AccountState.SignedOut : AccountState.NeedsSignIn;
            Rebuild();
            return;
        }

        var signedIn = outcome.Account!;
        signedIn = signedIn with { Account = signedIn.Account with { Browser = item.Account.Browser } };

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
        try
        {
            _store.Save(item.Account);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Signed in, but the account could not be kept for next launch. Say so; keep going this session.
            _log.Write($"account not saved: {Describe(exception)}");
            item.ErrorMessage = "Signed in, but the account could not be saved for next time. " + Explain(exception) + LogHint();
        }

        Rebuild();

        var discovery = await NamespaceDiscovery.DiscoverAsync(_arm, signedIn.Credential, cancellationToken).ConfigureAwait(true);
        if (!ReferenceEquals(item.Credential, signedIn.Credential))
            return; // Signed out or removed while discovery ran.
        item.Discovery = discovery;
        item.ErrorMessage = discovery.Error ?? item.ErrorMessage;
        item.State = discovery.Error is null ? AccountState.Ready : AccountState.Error;
        Rebuild();
    }

    private void SaveBrowser(AccountItemViewModel item)
    {
        try
        {
            _store.Save(item.Account);
            _log.Write($"browser for a saved account set to {item.Browser.Name}");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            item.ErrorMessage = "The browser choice could not be saved. " + Explain(exception);
        }
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
        OnPropertyChanged(nameof(SignedInAccounts));
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
        Azure.Identity.CredentialUnavailableException unavailable => LogScrubber.Scrub(FirstLine(unavailable.Message)),
        Microsoft.Identity.Client.MsalException msal => LogScrubber.Scrub($"{msal.ErrorCode}: {FirstLine(msal.Message)}"),
        Azure.Identity.AuthenticationFailedException failed => LogScrubber.Scrub(FirstLine(failed.InnerException is { } inner ? failed.Message + " " + FirstLine(inner.Message) : failed.Message)),
        Azure.RequestFailedException or System.Net.Http.HttpRequestException or TimeoutException => ServiceBusErrorMapper.Map(exception).Message,
        _ => LogScrubber.Scrub($"{exception.GetType().Name}: {FirstLine(exception.Message)}"),
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 240 ? line[..240] + "..." : line;
    }
}
