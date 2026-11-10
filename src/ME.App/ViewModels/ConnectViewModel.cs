// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.ComponentModel;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Azure.Core;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>A signed-in Entra account the user can pick for a typed namespace or an endpoint-only string.</summary>
public sealed record AccountChoice(string Id, string Username, TokenCredential Credential)
{
    public override string ToString() => Username;
}

/// <summary>The signed-in Entra accounts, any of which a connection or saved login can use.</summary>
public interface IAccountCredentialSource
{
    IReadOnlyList<AccountChoice> SignedInAccounts { get; }
}

/// <summary>
/// The Connect panel. A pasted connection string lives only in this object's memory, is shown masked,
/// and is cleared as soon as a connection opens or the user disconnects. One paste box takes every flavor
/// (namespace key, entity key, SAS token, endpoint only, emulator) and says which one it found. A string
/// is saved only when asked, as a login in the credential vault (its secret only in the OS secure store).
/// A typed namespace or an endpoint-only string uses the Entra account picked in AccountChoices.
/// </summary>
public sealed class ConnectViewModel : ViewModelBase
{
    private readonly ProviderCatalog _catalog;
    private readonly Func<IEntityInventory, Task> _onConnected;
    private readonly Action _onDisconnected;
    private bool _useConnectionString;
    private string _namespaceText = string.Empty;
    private string _connectionString = string.Empty;
    private string? _validationMessage;
    private string? _connectedTo;
    private readonly ICredentialVault? _vault;
    private bool _saveConnection;
    private string _saveName = string.Empty;
    private LoginEntry? _selectedSaved;
    private AccountChoice? _selectedAccount;
    private IAccountCredentialSource? _accounts;
    private string? _usingLoginId;

    private static readonly LoginKind[] ConnectKinds = [LoginKind.ServiceBusConnectionString, LoginKind.EmulatorConnectionString, LoginKind.ServiceBusEntra];

    public ConnectViewModel(ProviderCatalog catalog, Func<IEntityInventory, Task> onConnected, Action onDisconnected, ICredentialVault? vault = null)
    {
        _vault = vault;
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _onConnected = onConnected ?? throw new ArgumentNullException(nameof(onConnected));
        _onDisconnected = onDisconnected ?? throw new ArgumentNullException(nameof(onDisconnected));

        Providers = new ReadOnlyCollection<ProviderOption>(catalog.Descriptors.Select(descriptor => new ProviderOption(descriptor)).ToList());
        foreach (var option in Providers)
            option.PropertyChanged += OnProviderPropertyChanged;
        var first = Providers.FirstOrDefault(option => option.IsEnabled);
        if (first is not null)
            first.IsSelected = true;

        ConnectCommand = new AsyncCommand(ConnectAsync, () => CanConnect);
        DisconnectCommand = new RelayCommand(Disconnect, () => IsConnected);
        ConnectSavedCommand = new AsyncCommand(ConnectSavedAsync, () => SelectedSaved is not null && !IsConnected);
        if (_vault is not null)
            _vault.Changed += (_, _) => ReloadSaved();
        ReloadSaved();
    }

    /// <summary>Set by the main view model once accounts exist.</summary>
    public IAccountCredentialSource? Accounts
    {
        get => _accounts;
        set
        {
            _accounts = value;
            RefreshAccountText();
        }
    }

    /// <summary>Signed-in accounts to pick from for a typed namespace or an endpoint-only string.</summary>
    public ObservableCollection<AccountChoice> AccountChoices { get; } = [];

    public AccountChoice? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (Set(ref _selectedAccount, value))
            {
                OnPropertyChanged(nameof(EntraHint));
                OnPropertyChanged(nameof(DetectedText));
            }
        }
    }

    public bool HasAccountChoices => AccountChoices.Count > 0;

    /// <summary>The account picker shows for a typed namespace and for an endpoint-only string.</summary>
    public bool ShowAccountPicker => HasAccountChoices && (UseEntra || Pasted is { Flavor: ConnectionFlavor.EndpointOnly });

    public string EntraHint => SelectedAccount is { } account
        ? $"Uses {account.Username}. No secret is stored."
        : "Uses your Azure sign-in. Add an account on the Accounts tab to choose which one.";

    public IReadOnlyList<ProviderOption> Providers { get; }

    public ProviderOption? SelectedProvider => Providers.FirstOrDefault(option => option.IsSelected);

    public bool UseEntra
    {
        get => !_useConnectionString;
        set => UseConnectionString = !value;
    }

    public bool UseConnectionString
    {
        get => _useConnectionString;
        set
        {
            if (Set(ref _useConnectionString, value))
            {
                OnPropertyChanged(nameof(UseEntra));
                OnPropertyChanged(nameof(Mode));
                OnPropertyChanged(nameof(ShowAccountPicker));
                OnPropertyChanged(nameof(CanSave));
                OnPropertyChanged(nameof(SaveNote));
                OnPropertyChanged(nameof(SaveLabel));
                ValidationMessage = null;
                Refresh();
            }
        }
    }

    public AuthMode Mode => UseConnectionString ? AuthMode.ConnectionString : AuthMode.Entra;

    public string NamespaceText
    {
        get => _namespaceText;
        set
        {
            if (Set(ref _namespaceText, value ?? string.Empty))
                Refresh();
        }
    }

    /// <summary>Bound to a masked field. Never logged. Saved only to the OS secure store, and only when asked.</summary>
    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (Set(ref _connectionString, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(DetectedText));
                OnPropertyChanged(nameof(HasDetectedText));
                OnPropertyChanged(nameof(DetectedIsError));
                OnPropertyChanged(nameof(ShowAccountPicker));
                Refresh();
            }
        }
    }

    private ServiceBusConnectionInput? Pasted =>
        SelectedProvider?.Id == ServiceBusBrokerProvider.ProviderId && !string.IsNullOrWhiteSpace(_connectionString)
            ? ServiceBusConnectionInput.Parse(_connectionString)
            : null;

    /// <summary>What the paste box holds, in words, with no secret in it.</summary>
    public string? DetectedText => Pasted switch
    {
        null => null,
        { Error: { } error } => error,
        { Flavor: ConnectionFlavor.EntitySas } input => input.Description + ". Message counts are not available with an entity-level key.",
        { Flavor: ConnectionFlavor.EndpointOnly } input => SelectedAccount is { } account
            ? $"{input.FullyQualifiedNamespace} with your Entra account {account.Username}"
            : input.Description,
        var input => input.Description,
    };

    public bool HasDetectedText => DetectedText is not null;

    public bool DetectedIsError => Pasted is { Error: not null };

    /// <summary>Where saved secrets go on this computer, for example "macOS Keychain".</summary>
    public string SecureStoreName => _vault?.SecureStoreName ?? "secure store";

    /// <summary>
    /// A typed namespace saves with no secret, so it needs only the vault. A pasted string needs the
    /// secure store; with none, saving is refused rather than written to a file.
    /// </summary>
    public bool CanSave => _vault is not null && (UseEntra || _vault.SecretsUnavailableReason is null);

    public string SaveLabel => UseEntra ? "Save to Saved logins" : $"Save to {SecureStoreName}";

    public string SaveNote => _vault is null
        ? "Saving is not available in this window."
        : UseEntra
            ? "Saves the namespace and the account to use. No secret is stored."
            : _vault.SecretsUnavailableReason
              ?? $"Held in memory only, unless you save it. Saved strings go only to your {SecureStoreName}, never to a file.";

    public bool SaveConnection
    {
        get => _saveConnection;
        set => Set(ref _saveConnection, value && CanSave);
    }

    public string SaveName
    {
        get => _saveName;
        set => Set(ref _saveName, value ?? string.Empty);
    }

    /// <summary>Saved Service Bus logins. Secrets stay in the secure store until used.</summary>
    public ObservableCollection<LoginEntry> SavedConnections { get; } = [];

    public bool HasSavedConnections => SavedConnections.Count > 0;

    public LoginEntry? SelectedSaved
    {
        get => _selectedSaved;
        set
        {
            if (Set(ref _selectedSaved, value))
                ConnectSavedCommand?.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand ConnectSavedCommand { get; }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (Set(ref _validationMessage, value))
                OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public bool HasValidationMessage => !string.IsNullOrEmpty(_validationMessage);

    public string? ConnectedTo
    {
        get => _connectedTo;
        private set
        {
            if (Set(ref _connectedTo, value))
            {
                OnPropertyChanged(nameof(IsConnected));
                Refresh();
            }
        }
    }

    public bool IsConnected => _connectedTo is not null;

    public bool CanConnect =>
        SelectedProvider is { IsEnabled: true }
        && !IsConnected
        && (UseConnectionString ? !string.IsNullOrWhiteSpace(ConnectionString) : !string.IsNullOrWhiteSpace(NamespaceText));

    public AsyncCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public BrokerConnection BuildConnection()
    {
        var provider = SelectedProvider ?? throw new InvalidOperationException("Choose a provider.");
        var credential = SelectedAccount?.Credential;
        return UseConnectionString
            ? BrokerConnection.WithConnectionString(provider.Id, new RuntimeSecret(ConnectionString), credential)
            : BrokerConnection.WithEntra(provider.Id, NamespaceText, credential);
    }

    /// <summary>Shows an inventory opened elsewhere (a namespace picked on the Accounts tab).</summary>
    public async Task ConnectWithAsync(IEntityInventory inventory, string label)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ValidationMessage = null;
        if (IsConnected)
            Disconnect();
        ConnectedTo = $"{inventory.Provider.DisplayName}: {label}";
        await _onConnected(inventory).ConfigureAwait(true);
    }

    private async Task ConnectAsync()
    {
        ValidationMessage = null;
        IBrokerProvider provider;
        BrokerConnection connection;
        try
        {
            provider = _catalog.Resolve(SelectedProvider?.Id ?? string.Empty);
            connection = BuildConnection();
        }
        catch (BrokerException exception)
        {
            ValidationMessage = exception.Message;
            return;
        }

        var problem = provider.Validate(connection);
        if (problem is not null)
        {
            ValidationMessage = problem;
            return;
        }

        IEntityInventory inventory;
        try
        {
            inventory = provider.OpenInventory(connection);
        }
        catch (BrokerException exception)
        {
            ValidationMessage = exception.Message;
            return;
        }

        var pasted = Pasted;
        if (SaveConnection)
            await SaveAsync(pasted).ConfigureAwait(true);
        if (_usingLoginId is { } used)
            MarkUsed(used);

        ConnectionString = string.Empty;
        ConnectedTo = Describe(provider, connection, pasted);
        await _onConnected(inventory).ConfigureAwait(true);
    }

    private string Describe(IBrokerProvider provider, BrokerConnection connection, ServiceBusConnectionInput? pasted)
    {
        var who = connection.Credential is not null && SelectedAccount is { } account ? $" as {account.Username}" : string.Empty;
        if (connection.Endpoint is { } endpoint)
            return $"{provider.Descriptor.DisplayName}: {endpoint} (Entra{who})";
        if (pasted is { Flavor: ConnectionFlavor.EndpointOnly })
            return $"{provider.Descriptor.DisplayName}: {pasted.FullyQualifiedNamespace} (Entra{who})";
        return pasted is not null
            ? $"{provider.Descriptor.DisplayName}: {pasted.Description}"
            : $"{provider.Descriptor.DisplayName} (connection string, held in memory)";
    }

    private async Task SaveAsync(ServiceBusConnectionInput? pasted)
    {
        if (_vault is null)
            return;
        var account = SelectedAccount;
        LoginDraft draft;
        RuntimeSecret? secret = null;
        if (UseEntra)
        {
            if (account is null)
            {
                ValidationMessage = "Connected. To save this namespace, sign in on the Accounts tab so the login can name its account.";
                return;
            }

            var fields = new Dictionary<string, string> { ["namespace"] = NamespaceText.Trim(), ["accountName"] = account.Username };
            draft = new LoginDraft(Named(NamespaceText.Trim()), LoginKind.ServiceBusEntra, fields, account.Id);
        }
        else if (pasted is { Error: null })
        {
            var endpointOnly = pasted.Flavor == ConnectionFlavor.EndpointOnly;
            draft = LoginMigration.DraftFor(Named(pasted.Description), pasted, endpointOnly ? account?.Id : null, endpointOnly ? account?.Username : null);
            secret = new RuntimeSecret(_connectionString.Trim());
        }
        else
        {
            return;
        }

        try
        {
            var saved = await _vault.AddAsync(draft, secret).ConfigureAwait(true);
            SaveConnection = false;
            SaveName = string.Empty;
            ReloadSaved();
            SelectedSaved = SavedConnections.FirstOrDefault(e => e.Id == saved.Id);
        }
        catch (Exception exception) when (exception is LoginRejectedException or SecretStoreException)
        {
            ValidationMessage = $"Connected, but the login was not saved: {SecretRedactor.Redact(exception.Message, secret)}";
        }
    }

    private string Named(string fallback) => string.IsNullOrWhiteSpace(SaveName) ? fallback : SaveName.Trim();

    private async Task ConnectSavedAsync()
    {
        if (SelectedSaved is not { } login || _vault is null)
            return;
        ValidationMessage = null;
        AccountChoice? account = null;
        if (login.AccountId is { } accountId)
        {
            account = AccountChoices.FirstOrDefault(a => a.Id == accountId);
            if (account is null)
            {
                var who = login.Field("accountName") is { Length: > 0 } name ? name : "its Entra account";
                ValidationMessage = $"'{login.DisplayName}' uses {who}. Sign in as that account on the Accounts tab first.";
                return;
            }
        }

        if (login.Kind == LoginKind.ServiceBusEntra)
        {
            UseEntra = true;
            NamespaceText = login.Field("namespace");
            SelectedAccount = account;
        }
        else
        {
            RuntimeSecret? value;
            try
            {
                value = await _vault.ReadSecretAsync(login.Id).ConfigureAwait(true);
            }
            catch (SecretStoreException exception)
            {
                ValidationMessage = exception.Message;
                return;
            }

            if (value is null)
            {
                ValidationMessage = $"The secret for '{login.DisplayName}' is missing from your {SecureStoreName}. Edit it on the Saved logins tab.";
                return;
            }

            UseConnectionString = true;
            ConnectionString = value.Reveal();
            if (account is not null)
                SelectedAccount = account;
        }

        SaveConnection = false;
        _usingLoginId = login.Id;
        try
        {
            await ConnectAsync().ConfigureAwait(true);
        }
        finally
        {
            _usingLoginId = null;
        }
    }

    private void MarkUsed(string id)
    {
        try
        {
            _vault?.MarkUsed(id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // "Last used" is a convenience; connecting still worked.
        }
    }

    private void ReloadSaved()
    {
        var selected = SelectedSaved?.Id;
        SavedConnections.Clear();
        foreach (var login in _vault?.List().Where(e => ConnectKinds.Contains(e.Kind)) ?? [])
            SavedConnections.Add(login);
        _selectedSaved = SavedConnections.FirstOrDefault(e => e.Id == selected);
        OnPropertyChanged(nameof(SelectedSaved));
        OnPropertyChanged(nameof(HasSavedConnections));
        ConnectSavedCommand?.RaiseCanExecuteChanged();
    }

    public override string ToString() => $"Connect: {(IsConnected ? ConnectedTo : "not connected")}, {SavedConnections.Count} saved logins";

    /// <summary>Call when the signed-in accounts change: refreshes the picker and keeps the choice when it is still signed in.</summary>
    public void RefreshAccountText()
    {
        var keep = SelectedAccount?.Id;
        AccountChoices.Clear();
        foreach (var account in _accounts?.SignedInAccounts ?? [])
            AccountChoices.Add(account);
        SelectedAccount = AccountChoices.FirstOrDefault(a => a.Id == keep) ?? AccountChoices.FirstOrDefault();
        OnPropertyChanged(nameof(HasAccountChoices));
        OnPropertyChanged(nameof(ShowAccountPicker));
        OnPropertyChanged(nameof(EntraHint));
        OnPropertyChanged(nameof(DetectedText));
    }

    private void Disconnect()
    {
        ConnectionString = string.Empty;
        ConnectedTo = null;
        _onDisconnected();
    }

    private void OnProviderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProviderOption.IsSelected) || sender is not ProviderOption { IsSelected: true } chosen)
            return;
        foreach (var option in Providers)
        {
            if (!ReferenceEquals(option, chosen))
                option.IsSelected = false;
        }

        OnPropertyChanged(nameof(SelectedProvider));
        OnPropertyChanged(nameof(DetectedText));
        OnPropertyChanged(nameof(HasDetectedText));
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanConnect));
        ConnectCommand?.RaiseCanExecuteChanged();
        DisconnectCommand?.RaiseCanExecuteChanged();
        ConnectSavedCommand?.RaiseCanExecuteChanged();
    }
}
