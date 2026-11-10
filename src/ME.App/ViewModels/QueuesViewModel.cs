// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum QueueListState
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
    private List<QueueRowViewModel> _all = [];
    private QueueListState _state = QueueListState.NotConnected;
    private string? _errorMessage;
    private bool _errorIsRetryable;
    private QueueSortColumn _sortColumn = QueueSortColumn.Name;
    private bool _sortDescending;
    private int _pagesRead;

    public QueuesViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _inventory is not null);
    }

    public ObservableCollection<QueueRowViewModel> Rows { get; } = [];

    public AsyncCommand RefreshCommand { get; }

    public QueueListState State
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

    public bool IsNotConnected => State == QueueListState.NotConnected;

    public bool IsLoading => State == QueueListState.Loading;

    public bool IsEmpty => State == QueueListState.Empty;

    public bool IsError => State == QueueListState.Error;

    public bool HasRows => State == QueueListState.Loaded;

    public string NotConnectedText => "Not connected. Choose a provider and connect to list queues.";

    public string LoadingText => "Loading queues...";

    public string EmptyText => "This namespace has no queues.";

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
        QueueListState.Loaded => $"{_all.Count} queue{(_all.Count == 1 ? string.Empty : "s")}, {DeadLetterQueueCount} with dead letters. Read-only.",
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
        _all = [];
        Rows.Clear();
        ErrorMessage = null;
        PagesRead = 0;
        State = QueueListState.NotConnected;
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

        State = QueueListState.Loading;
        ErrorMessage = null;
        try
        {
            var result = await _inventory.ListQueuesAsync(cancellationToken).ConfigureAwait(true);
            _all = result.Entities.Where(entity => entity.Kind == EntityKind.Queue).Select(entity => new QueueRowViewModel(entity)).ToList();
            PagesRead = result.PagesRead;
            ApplySort();
            OnPropertyChanged(nameof(DeadLetterQueueCount));
            State = _all.Count == 0 ? QueueListState.Empty : QueueListState.Loaded;
            OnPropertyChanged(nameof(Summary));
        }
        catch (OperationCanceledException)
        {
            State = _all.Count == 0 ? QueueListState.Empty : QueueListState.Loaded;
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
        State = QueueListState.Error;
    }

    private void ApplySort()
    {
        IEnumerable<QueueRowViewModel> ordered = _sortColumn switch
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

    private IOrderedEnumerable<QueueRowViewModel> Order<TKey>(Func<QueueRowViewModel, TKey> key)
    {
        var comparer = typeof(TKey) == typeof(string)
            ? (IComparer<TKey>)StringComparer.OrdinalIgnoreCase
            : Comparer<TKey>.Default;
        var first = _sortDescending ? _all.OrderByDescending(key, comparer) : _all.OrderBy(key, comparer);
        return first.ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase);
    }
}
