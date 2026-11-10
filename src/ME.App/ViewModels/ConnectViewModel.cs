// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.ComponentModel;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// The Connect panel. The connection string lives only in this object's memory, is shown masked,
/// and is cleared as soon as a connection opens or the user disconnects.
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

    public ConnectViewModel(ProviderCatalog catalog, Func<IEntityInventory, Task> onConnected, Action onDisconnected)
    {
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
    }

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

    /// <summary>Bound to a masked field. Never persisted, never logged.</summary>
    public string ConnectionString
    {
        get => _connectionString;
        set
        {
            if (Set(ref _connectionString, value ?? string.Empty))
                Refresh();
        }
    }

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
        return UseConnectionString
            ? BrokerConnection.WithConnectionString(provider.Id, new RuntimeSecret(ConnectionString))
            : BrokerConnection.WithEntra(provider.Id, NamespaceText);
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

        ConnectionString = string.Empty;
        ConnectedTo = connection.Endpoint is { } endpoint
            ? $"{provider.Descriptor.DisplayName}: {endpoint} (Entra)"
            : $"{provider.Descriptor.DisplayName} (connection string, held in memory)";
        await _onConnected(inventory).ConfigureAwait(true);
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
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(CanConnect));
        ConnectCommand?.RaiseCanExecuteChanged();
        DisconnectCommand?.RaiseCanExecuteChanged();
    }
}
