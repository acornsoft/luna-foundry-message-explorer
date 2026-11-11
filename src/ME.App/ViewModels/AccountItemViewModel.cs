// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Azure.Core;

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum AccountState
{
    SigningIn,
    Discovering,
    Ready,
    SignedOut,
    NeedsSignIn,
    Error,
}

/// <summary>One account in the Accounts list: who, which tenant, and how its sign-in and discovery went.</summary>
public sealed class AccountItemViewModel : ViewModelBase
{
    private StoredAccount _account;
    private AccountState _state;
    private string? _error;
    private DiscoveryResult? _discovery;

    private readonly Action<AccountItemViewModel>? _browserChanged;

    internal AccountItemViewModel(
        StoredAccount account,
        Func<AccountItemViewModel, Task> signIn,
        Action<AccountItemViewModel> signOut,
        Action<AccountItemViewModel> remove,
        IReadOnlyList<BrowserChoice>? browsers = null,
        Action<AccountItemViewModel>? browserChanged = null,
        Func<AccountItemViewModel, Task>? refresh = null)
    {
        _account = account;
        Browsers = browsers ?? [BrowserChoices.SystemDefault, BrowserChoices.CopyLinkOnly];
        _browserChanged = browserChanged;
        _state = account.SignedOut ? AccountState.SignedOut : AccountState.SigningIn;
        SignInCommand = new AsyncCommand(() => signIn(this), () => CanSignIn);
        SignOutCommand = new RelayCommand(() => signOut(this), () => IsSignedIn);
        RemoveCommand = new RelayCommand(() => remove(this));
        RefreshCommand = new AsyncCommand(() => refresh?.Invoke(this) ?? Task.CompletedTask, () => refresh is not null && IsSignedIn);
    }

    private bool _isExpanded;

    /// <summary>Lists this account's namespaces again with the credential it has. Read-only.</summary>
    public AsyncCommand RefreshCommand { get; }

    /// <summary>Open to show the account's tenants, as in Azure Data Studio's Linked accounts.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Two letters for the account's badge: the first letters of the name part of the user name.</summary>
    public string Initials
    {
        get
        {
            var name = Username.Split('@')[0];
            var parts = name.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
            var letters = parts.Length >= 2 ? $"{parts[0][0]}{parts[1][0]}" : name.Length >= 2 ? name[..2] : name;
            return letters.ToUpperInvariant();
        }
    }

    /// <summary>The tenants discovery found, each with its subscription count or its error.</summary>
    public IReadOnlyList<TenantRow> Tenants => _discovery?.Tenants
        .Select(t => new TenantRow(
            t.Tenant.DisplayName,
            t.Error ?? (t.Subscriptions.Count == 1 ? "1 subscription" : $"{t.Subscriptions.Count} subscriptions"),
            t.Error is not null))
        .ToList() ?? [];

    public bool HasTenants => Tenants.Count > 0;

    public string TenantsToggleText => Tenants.Count switch
    {
        0 => "No tenants yet",
        1 => "1 tenant",
        var n => $"{n} tenants",
    };

    public StoredAccount Account
    {
        get => _account;
        internal set
        {
            _account = value;
            OnPropertyChanged(nameof(Username));
            OnPropertyChanged(nameof(TenantId));
            OnPropertyChanged(nameof(TenantText));
            OnPropertyChanged(nameof(CloudText));
        }
    }

    public string Id => _account.Id;

    /// <summary>The browsers this account can sign in with.</summary>
    public IReadOnlyList<BrowserChoice> Browsers { get; }

    /// <summary>Where this account's sign-in page opens. Remembered with the account.</summary>
    public BrowserChoice Browser
    {
        get => BrowserChoices.Find(Browsers, _account.Browser);
        set
        {
            var id = (value ?? BrowserChoices.SystemDefault).Id;
            if (id == _account.Browser)
                return;
            _account = _account with { Browser = id };
            OnPropertyChanged();
            _browserChanged?.Invoke(this);
        }
    }

    public string Username => _account.Username;

    public string TenantId => _account.TenantId;

    /// <summary>The cloud this account signs in to, for the account card.</summary>
    public string CloudText => AzureClouds.Find(_account.Cloud).DisplayName;

    public string TenantText => _account.RequestedTenant is { } requested && !string.Equals(requested, _account.TenantId, StringComparison.OrdinalIgnoreCase)
        ? $"Tenant {_account.TenantId} (signed in to {requested})"
        : $"Tenant {_account.TenantId}";

    /// <summary>Null unless the account is signed in. In memory only.</summary>
    public TokenCredential? Credential { get; internal set; }

    public AccountState State
    {
        get => _state;
        internal set
        {
            if (Set(ref _state, value))
                RaiseState();
        }
    }

    public string? ErrorMessage
    {
        get => _error;
        internal set
        {
            if (Set(ref _error, value))
                RaiseState();
        }
    }

    public DiscoveryResult? Discovery
    {
        get => _discovery;
        internal set
        {
            if (Set(ref _discovery, value))
                RaiseState();
        }
    }

    public IReadOnlyList<NamespaceRef> Namespaces => _discovery?.Namespaces.ToList() ?? [];

    public bool IsSignedIn => Credential is not null && _state is AccountState.Ready or AccountState.Discovering or AccountState.Error;

    public bool CanSignIn => _state is AccountState.SignedOut or AccountState.NeedsSignIn;

    public bool HasError => !string.IsNullOrEmpty(_error);

    public string StatusText => _state switch
    {
        AccountState.SigningIn => "Signing in...",
        AccountState.Discovering => "Finding Service Bus namespaces...",
        AccountState.Ready => Namespaces.Count switch
        {
            0 => "Signed in. No Service Bus namespaces you can read.",
            1 => "Signed in. 1 namespace.",
            var n => $"Signed in. {n} namespaces.",
        } + PartialText,
        AccountState.SignedOut => "Signed out.",
        AccountState.NeedsSignIn => "Sign in again to use this account.",
        _ => "Could not list namespaces.",
    };

    public AsyncCommand SignInCommand { get; }

    public RelayCommand SignOutCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public override string ToString() => $"{Username} ({State})";

    private string PartialText
    {
        get
        {
            var failed = _discovery?.Tenants.Count(t => t.Error is not null) + _discovery?.Tenants.Sum(t => t.Subscriptions.Count(s => s.Error is not null));
            return failed is > 0 ? $" {failed} could not be read." : string.Empty;
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(Namespaces));
        OnPropertyChanged(nameof(Tenants));
        OnPropertyChanged(nameof(HasTenants));
        OnPropertyChanged(nameof(TenantsToggleText));
        SignInCommand.RaiseCanExecuteChanged();
        SignOutCommand.RaiseCanExecuteChanged();
        RefreshCommand?.RaiseCanExecuteChanged();
    }
}

/// <summary>A tenant under an account in the Linked accounts list.</summary>
public sealed record TenantRow(string Name, string Detail, bool IsError);
