// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// Topics and their subscriptions, read-only. The topic list shows subscription counts; picking a topic
/// lists its subscriptions with active, dead-letter, and transfer dead-letter counts. Same states as Queues.
/// </summary>
public sealed class TopicsViewModel : ViewModelBase
{
    private IEntityInventory? _inventory;
    private ListState _state = ListState.NotConnected;
    private ListState _subscriptionState = ListState.NotConnected;
    private string? _errorMessage;
    private string? _subscriptionError;
    private EntityRowViewModel? _selectedTopic;
    private int _subscriptionLoad;

    public TopicsViewModel()
    {
        RefreshCommand = new AsyncCommand(() => LoadCoreAsync(CancellationToken.None), () => _inventory is not null);
    }

    public ObservableCollection<EntityRowViewModel> Topics { get; } = [];

    public ObservableCollection<EntityRowViewModel> Subscriptions { get; } = [];

    public AsyncCommand RefreshCommand { get; }

    public ListState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsNotConnected));
                OnPropertyChanged(nameof(IsLoading));
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(IsError));
                OnPropertyChanged(nameof(HasRows));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public bool IsNotConnected => State == ListState.NotConnected;

    public bool IsLoading => State == ListState.Loading;

    public bool IsEmpty => State == ListState.Empty;

    public bool IsError => State == ListState.Error;

    public bool HasRows => State == ListState.Loaded;

    public string NotConnectedText => "Not connected. Choose a provider and connect to list topics.";

    public string LoadingText => "Loading topics...";

    public string EmptyText => _inventory?.Provider.Supports(BrokerCapabilities.ListTopics) == false
        ? "This provider does not list topics."
        : "This namespace has no topics.";

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public string Summary => State == ListState.Loaded
        ? $"{Topics.Count} topic{(Topics.Count == 1 ? string.Empty : "s")}, {Topics.Sum(t => t.SubscriptionCount ?? 0)} subscriptions. Read-only."
        : string.Empty;

    public EntityRowViewModel? SelectedTopic
    {
        get => _selectedTopic;
        set
        {
            if (Set(ref _selectedTopic, value))
            {
                OnPropertyChanged(nameof(SubscriptionsTitle));
                SubscriptionsLoad = LoadSubscriptionsAsync(value);
            }
        }
    }

    /// <summary>The subscription load started by the last pick. Tests await it.</summary>
    public Task SubscriptionsLoad { get; private set; } = Task.CompletedTask;

    public string SubscriptionsTitle => _selectedTopic is null ? "Subscriptions" : $"Subscriptions of {_selectedTopic.Name}";

    public ListState SubscriptionState
    {
        get => _subscriptionState;
        private set
        {
            if (Set(ref _subscriptionState, value))
            {
                OnPropertyChanged(nameof(NoTopicSelected));
                OnPropertyChanged(nameof(SubscriptionsLoading));
                OnPropertyChanged(nameof(SubscriptionsEmpty));
                OnPropertyChanged(nameof(SubscriptionsError));
                OnPropertyChanged(nameof(HasSubscriptions));
                OnPropertyChanged(nameof(SubscriptionsSummary));
            }
        }
    }

    public bool NoTopicSelected => SubscriptionState == ListState.NotConnected;

    public bool SubscriptionsLoading => SubscriptionState == ListState.Loading;

    public bool SubscriptionsEmpty => SubscriptionState == ListState.Empty;

    public bool SubscriptionsError => SubscriptionState == ListState.Error;

    public bool HasSubscriptions => SubscriptionState == ListState.Loaded;

    public string NoTopicText => "Pick a topic to list its subscriptions.";

    public string SubscriptionsEmptyText => "This topic has no subscriptions.";

    public string? SubscriptionError
    {
        get => _subscriptionError;
        private set => Set(ref _subscriptionError, value);
    }

    public string SubscriptionsSummary => SubscriptionState == ListState.Loaded
        ? $"{Subscriptions.Count} subscription{(Subscriptions.Count == 1 ? string.Empty : "s")}, {Subscriptions.Count(s => s.HasDeadLetters)} with dead letters."
        : string.Empty;

    public async Task LoadAsync(IEntityInventory inventory, CancellationToken cancellationToken = default)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        RefreshCommand.RaiseCanExecuteChanged();
        await LoadCoreAsync(cancellationToken).ConfigureAwait(true);
    }

    public void Clear()
    {
        _inventory = null;
        Topics.Clear();
        ClearSubscriptions();
        _selectedTopic = null;
        OnPropertyChanged(nameof(SelectedTopic));
        OnPropertyChanged(nameof(SubscriptionsTitle));
        ErrorMessage = null;
        State = ListState.NotConnected;
        RefreshCommand.RaiseCanExecuteChanged();
    }

    /// <summary>List the subscriptions of one topic. A newer pick wins over a slower older one.</summary>
    public async Task LoadSubscriptionsAsync(EntityRowViewModel? topic, CancellationToken cancellationToken = default)
    {
        var load = Interlocked.Increment(ref _subscriptionLoad);
        Subscriptions.Clear();
        SubscriptionError = null;
        if (topic is null || _inventory is null)
        {
            SubscriptionState = ListState.NotConnected;
            return;
        }

        SubscriptionState = ListState.Loading;
        try
        {
            var result = await _inventory.ListSubscriptionsAsync(topic.Name, cancellationToken).ConfigureAwait(true);
            if (load != _subscriptionLoad)
                return;
            foreach (var row in result.Entities.Where(e => e.Kind == EntityKind.Subscription).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                Subscriptions.Add(new EntityRowViewModel(row));
            SubscriptionState = Subscriptions.Count == 0 ? ListState.Empty : ListState.Loaded;
            OnPropertyChanged(nameof(SubscriptionsSummary));
        }
        catch (OperationCanceledException)
        {
            SubscriptionState = ListState.NotConnected;
        }
        catch (BrokerException exception)
        {
            if (load == _subscriptionLoad)
                ShowSubscriptionError(exception.Message);
        }
        catch (Exception)
        {
            if (load == _subscriptionLoad)
                ShowSubscriptionError(BrokerException.Explain(BrokerErrorKind.Unknown));
        }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_inventory is null)
            return;
        if (!_inventory.Provider.Supports(BrokerCapabilities.ListTopics))
        {
            Topics.Clear();
            ClearSubscriptions();
            OnPropertyChanged(nameof(EmptyText));
            State = ListState.Empty;
            return;
        }

        var previous = _selectedTopic?.Name;
        State = ListState.Loading;
        ErrorMessage = null;
        try
        {
            var result = await _inventory.ListTopicsAsync(cancellationToken).ConfigureAwait(true);
            Topics.Clear();
            foreach (var row in result.Entities.Where(e => e.Kind == EntityKind.Topic).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
                Topics.Add(new EntityRowViewModel(row));
            State = Topics.Count == 0 ? ListState.Empty : ListState.Loaded;
            OnPropertyChanged(nameof(Summary));
            SelectedTopic = Topics.FirstOrDefault(t => t.Name == previous);
            if (SelectedTopic is null)
                ClearSubscriptions();
        }
        catch (OperationCanceledException)
        {
            State = Topics.Count == 0 ? ListState.Empty : ListState.Loaded;
        }
        catch (BrokerException exception)
        {
            ShowError(exception.Message);
        }
        catch (Exception)
        {
            ShowError(BrokerException.Explain(BrokerErrorKind.Unknown));
        }
    }

    private void ClearSubscriptions()
    {
        Interlocked.Increment(ref _subscriptionLoad);
        Subscriptions.Clear();
        SubscriptionError = null;
        SubscriptionState = ListState.NotConnected;
    }

    private void ShowError(string message)
    {
        Topics.Clear();
        ClearSubscriptions();
        ErrorMessage = message;
        State = ListState.Error;
    }

    private void ShowSubscriptionError(string message)
    {
        Subscriptions.Clear();
        SubscriptionError = message;
        SubscriptionState = ListState.Error;
    }
}
