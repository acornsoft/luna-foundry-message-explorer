// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum ListState
{
    NotConnected,
    Loading,
    Empty,
    Loaded,
    Error,
}

public enum QueueSortColumn
{
    Name,
    Active,
    DeadLetter,
    Scheduled,
    TransferDeadLetter,
    Status,
    Size,
}

/// <summary>The Queues grid: load, refresh, sort, and the empty, loading, and error states.</summary>
public sealed class QueuesViewModel : ViewModelBase
{
    private IEntityInventory? _inventory;
    private List<EntityRowViewModel> _all = [];
    private ListState _state = ListState.NotConnected;
    private string? _errorMessage;
    private bool _errorIsRetryable;
    private QueueSortColumn _sortColumn = QueueSortColumn.Name;
    private bool _sortDescending;
    private int _pagesRead;

    public QueuesViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _inventory is not null);
    }

    public ObservableCollection<EntityRowViewModel> Rows { get; } = [];

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

    public string NotConnectedText => "Not connected. Choose a provider and connect to list queues.";

    public string LoadingText => "Loading queues...";

    private string? _notice;

    /// <summary>A plain note from the connection, for example that an entity-level key can't read counts.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
            {
                OnPropertyChanged(nameof(HasNotice));
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    public string EmptyText => HasNotice ? "No queues to show with this connection." : "This namespace has no queues.";

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    public bool ErrorIsRetryable
    {
        get => _errorIsRetryable;
        private set => Set(ref _errorIsRetryable, value);
    }

    public int PagesRead
    {
        get => _pagesRead;
        private set => Set(ref _pagesRead, value);
    }

    public int DeadLetterQueueCount => _all.Count(row => row.HasDeadLetters);

    public string Summary => State switch
    {
        ListState.Loaded => $"{_all.Count} queue{(_all.Count == 1 ? string.Empty : "s")}, {DeadLetterQueueCount} with dead letters. Read-only.",
        _ => string.Empty,
    };

    public QueueSortColumn SortColumn => _sortColumn;

    public bool SortDescending => _sortDescending;

    public string SortText => $"Sorted by {_sortColumn}, {(_sortDescending ? "descending" : "ascending")}";

    public async Task LoadAsync(IEntityInventory inventory, CancellationToken cancellationToken = default)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        RefreshCommand.RaiseCanExecuteChanged();
        await LoadCoreAsync(cancellationToken).ConfigureAwait(true);
    }

    public void Clear()
    {
        _inventory = null;
        Notice = null;
        _all = [];
        Rows.Clear();
        ErrorMessage = null;
        PagesRead = 0;
        State = ListState.NotConnected;
        RefreshCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Sort by a column. The same column again flips the direction. A new count column starts descending,
    /// so the busiest queues come first. Name and status start ascending. Ties break by name.
    /// </summary>
    public void Sort(QueueSortColumn column)
    {
        if (column == _sortColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = column is not (QueueSortColumn.Name or QueueSortColumn.Status);
        }

        ApplySort();
        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortDescending));
        OnPropertyChanged(nameof(SortText));
    }

    private Task RefreshAsync() => LoadCoreAsync(CancellationToken.None);

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_inventory is null)
            return;

        State = ListState.Loading;
        ErrorMessage = null;
        try
        {
            var result = await _inventory.ListQueuesAsync(cancellationToken).ConfigureAwait(true);
            Notice = result.Notice;
            _all = result.Entities.Where(entity => entity.Kind == EntityKind.Queue).Select(entity => new EntityRowViewModel(entity)).ToList();
            PagesRead = result.PagesRead;
            ApplySort();
            OnPropertyChanged(nameof(DeadLetterQueueCount));
            State = _all.Count == 0 ? ListState.Empty : ListState.Loaded;
            OnPropertyChanged(nameof(Summary));
        }
        catch (OperationCanceledException)
        {
            State = _all.Count == 0 ? ListState.Empty : ListState.Loaded;
        }
        catch (BrokerException exception)
        {
            ShowError(exception.Message, exception.IsRetryable);
        }
        catch (Exception)
        {
            ShowError(BrokerException.Explain(BrokerErrorKind.Unknown), retryable: false);
        }
    }

    private void ShowError(string message, bool retryable)
    {
        _all = [];
        Rows.Clear();
        ErrorMessage = message;
        ErrorIsRetryable = retryable;
        State = ListState.Error;
    }

    private void ApplySort()
    {
        IEnumerable<EntityRowViewModel> ordered = _sortColumn switch
        {
            QueueSortColumn.Name => Order(row => row.Name),
            QueueSortColumn.Active => Order(row => row.Active ?? -1),
            QueueSortColumn.DeadLetter => Order(row => row.DeadLetter ?? -1),
            QueueSortColumn.Scheduled => Order(row => row.Scheduled ?? -1),
            QueueSortColumn.TransferDeadLetter => Order(row => row.TransferDeadLetter ?? -1),
            QueueSortColumn.Status => Order(row => row.Status),
            QueueSortColumn.Size => Order(row => row.SizeInBytes ?? -1),
            _ => _all,
        };

        Rows.Clear();
        foreach (var row in ordered)
            Rows.Add(row);
    }

    private IOrderedEnumerable<EntityRowViewModel> Order<TKey>(Func<EntityRowViewModel, TKey> key)
    {
        var comparer = typeof(TKey) == typeof(string)
            ? (IComparer<TKey>)StringComparer.OrdinalIgnoreCase
            : Comparer<TKey>.Default;
        var first = _sortDescending ? _all.OrderByDescending(key, comparer) : _all.OrderBy(key, comparer);
        return first.ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase);
    }
}
