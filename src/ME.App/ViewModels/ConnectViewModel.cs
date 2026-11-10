// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.ComponentModel;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Azure.Core;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>The signed-in account that a typed namespace or an endpoint-only string uses.</summary>
public interface IAccountCredentialSource
{
    TokenCredential? DefaultCredential { get; }

    string? DefaultUsername { get; }
}

/// <summary>
/// The Connect panel. A pasted connection string lives only in this object's memory, is shown masked,
/// and is cleared as soon as a connection opens or the user disconnects. One paste box takes every flavor
/// (namespace key, entity key, SAS token, endpoint only, emulator) and says which one it found. A string
/// is saved only when asked, and only to the OS secure store.
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
    private readonly ISecretVault? _vault;
    private bool _saveConnection;
    private string _saveName = string.Empty;
    private string? _selectedSaved;

    public ConnectViewModel(ProviderCatalog catalog, Func<IEntityInventory, Task> onConnected, Action onDisconnected, ISecretVault? vault = null)
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
        DeleteSavedCommand = new RelayCommand(DeleteSaved, () => SelectedSaved is not null);
        ReloadSaved();
    }

    /// <summary>Set by the main view model once accounts exist.</summary>
    public IAccountCredentialSource? Accounts { get; set; }

    public string EntraHint => Accounts?.DefaultUsername is { } user
        ? $"Uses your signed-in account {user}. No secret is stored."
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
        { Flavor: ConnectionFlavor.EndpointOnly } input => Accounts?.DefaultUsername is { } user
            ? $"{input.FullyQualifiedNamespace} with your Entra account {user}"
            : input.Description,
        var input => input.Description,
    };

    public bool HasDetectedText => DetectedText is not null;

    public bool DetectedIsError => Pasted is { Error: not null };

    /// <summary>The OS secure store's name on this computer.</summary>
    public static string SecureStoreName =>
        OperatingSystem.IsMacOS() ? "Keychain" : OperatingSystem.IsWindows() ? "Windows protected storage" : "system keyring";

    public bool CanSave => _vault is { UnavailableReason: null };

    public string SaveLabel => $"Save to {SecureStoreName}";

    public string SaveNote => _vault?.UnavailableReason
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

    /// <summary>Names of saved connection strings. The values stay in the secure store until used.</summary>
    public ObservableCollection<string> SavedConnections { get; } = [];

    public bool HasSavedConnections => SavedConnections.Count > 0;

    public string? SelectedSaved
    {
        get => _selectedSaved;
        set
        {
            if (Set(ref _selectedSaved, value))
            {
                ConnectSavedCommand?.RaiseCanExecuteChanged();
                DeleteSavedCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand ConnectSavedCommand { get; }

    public RelayCommand DeleteSavedCommand { get; }

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
        var credential = Accounts?.DefaultCredential;
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
        if (SaveConnection && pasted is { Error: null } && UseConnectionString)
            Save(pasted);

        ConnectionString = string.Empty;
        ConnectedTo = Describe(provider, connection, pasted);
        await _onConnected(inventory).ConfigureAwait(true);
    }

    private string Describe(IBrokerProvider provider, BrokerConnection connection, ServiceBusConnectionInput? pasted)
    {
        var who = connection.Credential is not null && Accounts?.DefaultUsername is { } user ? $" as {user}" : string.Empty;
        if (connection.Endpoint is { } endpoint)
            return $"{provider.Descriptor.DisplayName}: {endpoint} (Entra{who})";
        if (pasted is { Flavor: ConnectionFlavor.EndpointOnly })
            return $"{provider.Descriptor.DisplayName}: {pasted.FullyQualifiedNamespace} (Entra{who})";
        return pasted is not null
            ? $"{provider.Descriptor.DisplayName}: {pasted.Description}"
            : $"{provider.Descriptor.DisplayName} (connection string, held in memory)";
    }

    private void Save(ServiceBusConnectionInput pasted)
    {
        var name = string.IsNullOrWhiteSpace(SaveName) ? pasted.Description : SaveName.Trim();
        try
        {
            _vault!.Write(name, _connectionString.Trim());
        }
        catch (Exception exception) when (IsVaultFailure(exception))
        {
            ValidationMessage = $"Connected, but the connection string could not be saved to your {SecureStoreName}.";
            return;
        }

        SaveConnection = false;
        SaveName = string.Empty;
        ReloadSaved();
        SelectedSaved = name;
    }

    private async Task ConnectSavedAsync()
    {
        if (SelectedSaved is not { } name || _vault is null)
            return;
        string? value;
        try
        {
            value = _vault.Read(name);
        }
        catch (Exception exception) when (IsVaultFailure(exception))
        {
            value = null;
        }

        if (value is null)
        {
            ValidationMessage = $"'{name}' could not be read from your {SecureStoreName}.";
            ReloadSaved();
            return;
        }

        UseConnectionString = true;
        SaveConnection = false;
        ConnectionString = value;
        await ConnectAsync().ConfigureAwait(true);
    }

    private void DeleteSaved()
    {
        if (SelectedSaved is not { } name || _vault is null)
            return;
        _vault.Delete(name);
        SelectedSaved = null;
        ReloadSaved();
    }

    private void ReloadSaved()
    {
        SavedConnections.Clear();
        if (CanSave)
        {
            try
            {
                foreach (var name in _vault!.Names())
                    SavedConnections.Add(name);
            }
            catch (Exception exception) when (IsVaultFailure(exception))
            {
                // The list stays empty; connecting by paste still works.
            }
        }

        OnPropertyChanged(nameof(HasSavedConnections));
    }

    private static bool IsVaultFailure(Exception exception) =>
        exception is InvalidOperationException or IOException or UnauthorizedAccessException
            or Microsoft.Identity.Client.Extensions.Msal.MsalCachePersistenceException;

    /// <summary>Call when the signed-in accounts change, so hints name the right account.</summary>
    public void RefreshAccountText()
    {
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
