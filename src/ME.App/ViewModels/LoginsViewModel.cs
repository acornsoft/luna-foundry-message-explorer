// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Globalization;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.Accounts;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// The Saved logins tab: every login for every system in one list. Secrets are never shown, copied or
/// revealed here; the editor takes a new secret in a masked box and hands it straight to the vault.
/// </summary>
public sealed class LoginsViewModel : ViewModelBase
{
    private readonly ICredentialVault _vault;
    private readonly ILoginTester _tester;
    private readonly Func<IAccountCredentialSource?> _accounts;
    private LoginItemViewModel? _selected;
    private LoginEditorViewModel? _editor;
    private string _renameText = string.Empty;
    private bool _isRenaming;
    private bool _isConfirmingDelete;
    private string? _status;
    private bool _statusIsError;

    public LoginsViewModel(ICredentialVault vault, ILoginTester tester, Func<IAccountCredentialSource?> accounts)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _tester = tester ?? throw new ArgumentNullException(nameof(tester));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        AddCommand = new RelayCommand(Add, () => !IsEditing);
        EditCommand = new RelayCommand(Edit, () => Selected is { CanEdit: true } && !IsEditing);
        RenameCommand = new RelayCommand(BeginRename, () => Selected is { CanEdit: true } && !IsEditing);
        ApplyRenameCommand = new RelayCommand(ApplyRename, () => IsRenaming && !string.IsNullOrWhiteSpace(RenameText));
        CancelRenameCommand = new RelayCommand(() => IsRenaming = false);
        DeleteCommand = new RelayCommand(() => IsConfirmingDelete = true, () => Selected is { CanEdit: true } && !IsEditing);
        ConfirmDeleteCommand = new AsyncCommand(DeleteAsync, () => IsConfirmingDelete);
        CancelDeleteCommand = new RelayCommand(() => IsConfirmingDelete = false);
        TestCommand = new AsyncCommand(TestAsync, () => Selected is not null && !IsEditing);
        _vault.Changed += (_, _) => Reload();
        Reload();
    }

    public ObservableCollection<LoginItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    public LoginItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value))
            {
                IsRenaming = false;
                IsConfirmingDelete = false;
                Status = null;
                RaiseCommands();
            }
        }
    }

    /// <summary>Where secrets are kept, in one plain sentence.</summary>
    public string StoreText => _vault.Protection switch
    {
        SecretProtection.OperatingSystem => $"Secrets are kept only in your {_vault.SecureStoreName}. Names and settings are kept in logins.json, with no secrets.",
        SecretProtection.BrowserProtected => "Browser-protected (weaker than OS store): secrets are encrypted with a key this page can't read out. Prefer Entra sign-in.",
        SecretProtection.Memory => "Demo: secrets are kept in memory for this session only.",
        _ => $"{_vault.SecretsUnavailableReason} Logins with a secret can't be saved; Entra and profile logins still can.",
    };

    /// <summary>True when secrets are not in an OS store (browser storage, memory, or no store at all).</summary>
    public bool StoreIsWeak => _vault.Protection != SecretProtection.OperatingSystem;

    public string StoreBadge => _vault.Protection switch
    {
        SecretProtection.OperatingSystem => "OS secure store",
        SecretProtection.BrowserProtected => "browser-protected (weaker than OS store)",
        SecretProtection.Memory => "memory only (demo)",
        _ => "no secure store",
    };

    public LoginEditorViewModel? Editor
    {
        get => _editor;
        private set
        {
            if (Set(ref _editor, value))
            {
                OnPropertyChanged(nameof(IsEditing));
                RaiseCommands();
            }
        }
    }

    public bool IsEditing => _editor is not null;

    public bool IsRenaming
    {
        get => _isRenaming;
        private set
        {
            if (Set(ref _isRenaming, value))
                ApplyRenameCommand?.RaiseCanExecuteChanged();
        }
    }

    public string RenameText
    {
        get => _renameText;
        set
        {
            if (Set(ref _renameText, value ?? string.Empty))
                ApplyRenameCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        private set
        {
            if (Set(ref _isConfirmingDelete, value))
            {
                OnPropertyChanged(nameof(DeletePrompt));
                ConfirmDeleteCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public string DeletePrompt => Selected is { } item
        ? $"Delete '{item.Name}'? {(item.Entry.HasSecret ? $"Its secret is removed from your {_vault.SecureStoreName}." : "It has no stored secret.")} This can't be undone."
        : string.Empty;

    public string? Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
                OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrEmpty(_status);

    public bool StatusIsError
    {
        get => _statusIsError;
        private set => Set(ref _statusIsError, value);
    }

    public RelayCommand AddCommand { get; }

    public RelayCommand EditCommand { get; }

    public RelayCommand RenameCommand { get; }

    public RelayCommand ApplyRenameCommand { get; }

    public RelayCommand CancelRenameCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public AsyncCommand ConfirmDeleteCommand { get; }

    public RelayCommand CancelDeleteCommand { get; }

    public AsyncCommand TestCommand { get; }

    public override string ToString() => $"Saved logins: {Items.Count}, secrets in {_vault.SecureStoreName}";

    private void Add() => Editor = new LoginEditorViewModel(_vault, _accounts(), existing: null, Close);

    private void Edit()
    {
        if (Selected is { } item)
            Editor = new LoginEditorViewModel(_vault, _accounts(), item.Entry, Close);
    }

    private void Close(string? savedId, string? message)
    {
        Editor = null;
        Reload();
        if (savedId is not null)
            Selected = Items.FirstOrDefault(i => i.Entry.Id == savedId);
        if (message is not null)
            Show(message, error: false);
    }

    private void BeginRename()
    {
        if (Selected is not { } item)
            return;
        RenameText = item.Name;
        IsRenaming = true;
    }

    private void ApplyRename()
    {
        if (Selected is not { } item)
            return;
        try
        {
            var renamed = _vault.Rename(item.Entry.Id, RenameText);
            IsRenaming = false;
            Reload();
            Selected = Items.FirstOrDefault(i => i.Entry.Id == renamed.Id);
            Show($"Renamed to '{renamed.DisplayName}'.", error: false);
        }
        catch (LoginRejectedException exception)
        {
            Show(exception.Message, error: true);
        }
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } item)
            return;
        IsConfirmingDelete = false;
        try
        {
            await _vault.DeleteAsync(item.Entry.Id).ConfigureAwait(true);
            Selected = null;
            Reload();
            Show($"Deleted '{item.Name}'.", error: false);
        }
        catch (SecretStoreException exception)
        {
            Show(exception.Message, error: true);
        }
    }

    private async Task TestAsync()
    {
        if (Selected is not { } item)
            return;
        Show($"Testing '{item.Name}' (read-only)…", error: false);
        var result = await _tester.TestAsync(item.Entry).ConfigureAwait(true);
        if (result.Outcome == LoginTestOutcome.Passed)
        {
            _vault.MarkUsed(item.Entry.Id);
            Reload();
            Selected = Items.FirstOrDefault(i => i.Entry.Id == item.Entry.Id);
        }

        Show(result.Message, error: result.Outcome == LoginTestOutcome.Failed);
    }

    private void Show(string message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }

    private void Reload()
    {
        var keep = Selected?.Entry.Id;
        Items.Clear();
        foreach (var entry in _vault.List().OrderBy(e => e.System).ThenBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase))
            Items.Add(new LoginItemViewModel(entry, _vault.SecureStoreName));
        _selected = Items.FirstOrDefault(i => i.Entry.Id == keep);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(HasItems));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        AddCommand?.RaiseCanExecuteChanged();
        EditCommand?.RaiseCanExecuteChanged();
        RenameCommand?.RaiseCanExecuteChanged();
        DeleteCommand?.RaiseCanExecuteChanged();
        TestCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(DeletePrompt));
    }
}

/// <summary>One row in Saved logins: system badge, name, kind and when it was last used. No secret.</summary>
public sealed class LoginItemViewModel(LoginEntry entry, string secureStoreName)
{
    public LoginEntry Entry { get; } = entry;

    public string Badge => LoginKinds.SystemBadge(Entry.System);

    public string SystemText => LoginKinds.SystemLabel(Entry.System);

    public string Name => Entry.DisplayName;

    public string KindText => LoginKinds.Of(Entry.Kind).Label;

    public string LastUsedText => Entry.LastUsed is { } used
        ? "Last used " + used.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        : "Never used";

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Field("accountName") is { Length: > 0 } account)
                parts.Add($"Uses {account}");
            parts.Add(Entry.HasSecret ? $"Secret in {secureStoreName}" : "No stored secret");
            if (Entry.Kind == LoginKind.EntraAccount)
                parts.Add("Managed on the Accounts tab");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Entra accounts are added and removed on the Accounts tab.</summary>
    public bool CanEdit => Entry.Kind != LoginKind.EntraAccount;

    public override string ToString() => Entry.ToString();
}

/// <summary>A system in the editor's picker.</summary>
public sealed record SystemOption(LoginSystem System, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One field in the login editor. Never a secret: the secret has its own masked box.</summary>
public sealed class LoginFieldEditor(LoginField field, string value) : ViewModelBase
{
    private string _value = value;

    public LoginField Field { get; } = field;

    public string Label => Field.Required ? Field.Label + " *" : Field.Label;

    public string Watermark => Field.Watermark;

    public IReadOnlyList<string> Choices => Field.Choices ?? [];

    public bool HasChoices => Choices.Count > 0;

    public bool IsText => !HasChoices;

    public string Value
    {
        get => _value;
        set => Set(ref _value, value ?? string.Empty);
    }

    public override string ToString() => $"{Field.Name}: {Value}";
}

/// <summary>
/// Adds or edits one login. The secret box is masked, has no reveal, and blocks copy and cut. When editing,
/// an empty secret keeps the stored one. Nothing is written until Save, and the secret is cleared after.
/// </summary>
public sealed class LoginEditorViewModel : ViewModelBase
{
    private readonly ICredentialVault _vault;
    private readonly Action<string?, string?> _done;
    private readonly LoginEntry? _existing;
    private LoginSystem _system;
    private LoginKindInfo _kind;
    private string _name;
    private string _secret = string.Empty;
    private AccountChoice? _account;
    private string? _error;

    public LoginEditorViewModel(ICredentialVault vault, IAccountCredentialSource? accounts, LoginEntry? existing, Action<string?, string?> done)
    {
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _done = done ?? throw new ArgumentNullException(nameof(done));
        _existing = existing;
        foreach (var account in accounts?.SignedInAccounts ?? [])
            Accounts.Add(account);
        _system = existing?.System ?? LoginSystem.ServiceBus;
        _kind = LoginKinds.Of(existing?.Kind ?? LoginKind.ServiceBusConnectionString);
        _name = existing?.DisplayName ?? string.Empty;
        _account = Accounts.FirstOrDefault(a => a.Id == existing?.AccountId) ?? Accounts.FirstOrDefault();
        LoadFields(existing);
        SaveCommand = new AsyncCommand(SaveAsync, () => CanSave);
        CancelCommand = new RelayCommand(Cancel);
    }

    public bool IsNew => _existing is null;

    public string Title => IsNew ? "Add a login" : $"Edit '{_existing!.DisplayName}'";

    /// <summary>Entra accounts are added by signing in on the Accounts tab, so they are not offered here.</summary>
    public IReadOnlyList<SystemOption> Systems { get; } = Enum.GetValues<LoginSystem>()
        .Where(s => s != LoginSystem.Entra)
        .Select(s => new SystemOption(s, LoginKinds.SystemLabel(s)))
        .ToList();

    public SystemOption SelectedSystem
    {
        get => Systems.First(o => o.System == _system);
        set
        {
            if (value is null || !IsNew || value.System == _system)
                return;
            _system = value.System;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Kinds));
            SelectedKind = Kinds[0];
        }
    }

    public IReadOnlyList<LoginKindInfo> Kinds => LoginKinds.For(_system);

    public LoginKindInfo SelectedKind
    {
        get => _kind;
        set
        {
            if (value is null || !IsNew || !Set(ref _kind, value))
                return;
            LoadFields(null);
            OnPropertyChanged(nameof(HasSecret));
            OnPropertyChanged(nameof(SecretLabel));
            OnPropertyChanged(nameof(SecretHint));
            OnPropertyChanged(nameof(UsesAccount));
            OnPropertyChanged(nameof(AccountLabel));
            OnPropertyChanged(nameof(HasNoAccounts));
            Refresh();
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value ?? string.Empty))
                Refresh();
        }
    }

    public ObservableCollection<LoginFieldEditor> Fields { get; } = [];

    public bool HasSecret => _kind.HasSecret;

    public string SecretLabel => _kind.SecretLabel ?? string.Empty;

    /// <summary>Bound to a masked box only. Never logged or shown; cleared after Save or Cancel.</summary>
    public string Secret
    {
        get => _secret;
        set
        {
            if (Set(ref _secret, value ?? string.Empty))
                Refresh();
        }
    }

    public string SecretHint => _vault.SecretsUnavailableReason is { } reason && HasSecret
        ? reason
        : _existing is { HasSecret: true }
            ? $"Stored in your {_vault.SecureStoreName}. Leave empty to keep it; it is never shown."
            : $"Kept only in your {_vault.SecureStoreName}. It can't be shown or copied later.";

    /// <summary>Entra-based kinds need an account; a connection string may name one for endpoint-only strings.</summary>
    public bool UsesAccount => _kind.UsesAccount || _kind.Kind == LoginKind.ServiceBusConnectionString;

    public string AccountLabel => _kind.UsesAccount ? "Entra account *" : "Entra account (used only by endpoint-only strings)";

    public bool HasNoAccounts => UsesAccount && Accounts.Count == 0;

    public ObservableCollection<AccountChoice> Accounts { get; } = [];

    public AccountChoice? SelectedAccount
    {
        get => _account;
        set
        {
            if (Set(ref _account, value))
                Refresh();
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    public bool CanSave => !string.IsNullOrWhiteSpace(Name) && (!_kind.UsesAccount || SelectedAccount is not null);

    public AsyncCommand SaveCommand { get; }

    public RelayCommand CancelCommand { get; }

    public override string ToString() => $"Login editor: {(string.IsNullOrEmpty(Name) ? "(new)" : Name)} ({_kind.Label}), secret {(Secret.Length > 0 ? "entered" : "empty")}";

    private void LoadFields(LoginEntry? existing)
    {
        Fields.Clear();
        foreach (var field in _kind.Fields.Where(f => f.Name != "accountName"))
            Fields.Add(new LoginFieldEditor(field, existing?.Field(field.Name) ?? field.Choices?.FirstOrDefault() ?? string.Empty));
    }

    private async Task SaveAsync()
    {
        Error = null;
        var fields = Fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).ToDictionary(f => f.Field.Name, f => f.Value.Trim(), StringComparer.Ordinal);
        var account = UsesAccount ? SelectedAccount : null;
        if (account is not null)
            fields["accountName"] = account.Username;
        var draft = new LoginDraft(Name, _kind.Kind, fields, account?.Id);
        var secret = HasSecret && Secret.Length > 0 ? new RuntimeSecret(Secret) : null;
        try
        {
            var saved = _existing is null
                ? await _vault.AddAsync(draft, secret).ConfigureAwait(true)
                : await _vault.UpdateAsync(_existing.Id, draft, secret).ConfigureAwait(true);
            Secret = string.Empty;
            _done(saved.Id, $"Saved '{saved.DisplayName}'.");
        }
        catch (Exception exception) when (exception is LoginRejectedException or SecretStoreException)
        {
            Error = SecretRedactor.Redact(exception.Message, secret);
        }
    }

    private void Cancel()
    {
        Secret = string.Empty;
        _done(null, null);
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanSave));
        SaveCommand?.RaiseCanExecuteChanged();
    }
}
