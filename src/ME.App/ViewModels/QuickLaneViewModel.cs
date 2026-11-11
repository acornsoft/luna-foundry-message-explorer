// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Globalization;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Shell.QuickView;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>One peeked message row with its clickable icons (open in the drawer, copy the redacted text).</summary>
public sealed class QuickMessageRow
{
    public QuickMessageRow(QuickMessage message, Action<QuickMessageRow> open, Func<string, Task> copy)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        OpenCommand = new RelayCommand(() => open(this));
        CopyCommand = new AsyncCommand(() => copy(message.CopyText));
    }

    public QuickMessage Message { get; }

    public RelayCommand OpenCommand { get; }

    public AsyncCommand CopyCommand { get; }
}

/// <summary>
/// One section of the quick view (Active, Dead-letter, Scheduled or Transfer dead-letter) and its lane card.
/// The count comes from runtime properties. Rows are peeked only when the section is first opened, a page
/// (top N) at a time, with "load more" continuing from the sub-queue's cursor.
/// </summary>
public sealed class QuickLaneViewModel : ViewModelBase
{
    private readonly QuickPeekSession? _session;
    private readonly Func<int> _pageSize;
    private readonly IUiScheduler _scheduler;
    private readonly Action<QuickMessageRow> _open;
    private readonly Func<string, Task> _copy;
    private bool _isExpanded;
    private bool _isLoading;
    private bool _loaded;
    private bool _hasMore;
    private string? _error;
    private QuickMessageRow? _selectedRow;
    private readonly Action<QuickLaneViewModel>? _onReset;

    public QuickLaneViewModel(
        MessageLane lane,
        long? count,
        string path,
        QuickPeekSession? session,
        string? note,
        Func<int> pageSize,
        Action<QuickMessageRow> open,
        Func<string, Task> copy,
        IUiScheduler? scheduler = null,
        Action<QuickLaneViewModel>? onReset = null)
    {
        _onReset = onReset;
        Lane = lane;
        Count = count;
        Path = path;
        _session = session is not null && session.CanPeek(lane) ? session : null;
        Note = note;
        _pageSize = pageSize ?? throw new ArgumentNullException(nameof(pageSize));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _copy = copy ?? throw new ArgumentNullException(nameof(copy));
        _scheduler = scheduler ?? InlineUiScheduler.Instance;
        LoadMoreCommand = new AsyncCommand(LoadMoreAsync, () => CanLoadMore);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => IsPeekable && !IsLoading);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded, () => IsPeekable);
    }

    public MessageLane Lane { get; }

    public string Title => Lane switch
    {
        MessageLane.Active => "Active",
        MessageLane.DeadLetter => "Dead-letter",
        MessageLane.Scheduled => "Scheduled",
        _ => "Transfer dead-letter",
    };

    /// <summary>A Font Awesome Free icon name for the lane.</summary>
    public string Icon => Lane switch
    {
        MessageLane.Active => "fa-solid fa-inbox",
        MessageLane.DeadLetter => "fa-solid fa-skull-crossbones",
        MessageLane.Scheduled => "fa-solid fa-clock",
        _ => "fa-solid fa-right-left",
    };

    /// <summary>The style class for the lane colour: active, dead, scheduled or transfer.</summary>
    public string Accent => Lane switch
    {
        MessageLane.Active => "active",
        MessageLane.DeadLetter => "dead",
        MessageLane.Scheduled => "scheduled",
        _ => "transfer",
    };

    public bool IsActiveLane => Lane == MessageLane.Active;

    public bool IsDeadLane => Lane == MessageLane.DeadLetter;

    public bool IsScheduledLane => Lane == MessageLane.Scheduled;

    public bool IsTransferLane => Lane == MessageLane.TransferDeadLetter;

    /// <summary>From runtime properties. Null when the broker doesn't report it.</summary>
    public long? Count { get; }

    public string CountText => Count?.ToString("N0", CultureInfo.InvariantCulture) ?? "—";

    /// <summary>What a screen reader says for the card's number.</summary>
    public string CountName => Count switch
    {
        null => $"{Title}: count not known",
        0 => $"{Title}: empty",
        _ => $"{Title}: {CountText}",
    };

    /// <summary>True when the count is above zero on a dead-letter lane (the card and badge turn red).</summary>
    public bool Alarm => Lane is MessageLane.DeadLetter or MessageLane.TransferDeadLetter && Count > 0;

    /// <summary>The sub-queue path, for example orders/$DeadLetterQueue.</summary>
    public string Path { get; }

    /// <summary>A plain sentence for a count-only lane (for example, a subscription's Scheduled count comes from its topic).</summary>
    public string? Note { get; }

    public bool HasNote => Note is not null;

    /// <summary>False for a count-only lane: there is nothing this connection can peek there.</summary>
    public bool IsPeekable => _session is not null;

    public ObservableCollection<QuickMessageRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            var open = value && IsPeekable;
            if (!Set(ref _isExpanded, open))
                return;
            if (open && !_loaded)
                _ = LoadMoreAsync();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value))
                Changed();
        }
    }

    /// <summary>True after the first page, while more may be waiting.</summary>
    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (Set(ref _hasMore, value))
                Changed();
        }
    }

    public bool CanLoadMore => IsPeekable && !IsLoading && (!_loaded || HasMore);

    public string? Error
    {
        get => _error;
        private set
        {
            if (Set(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The section header's right-hand text: not loaded, peeking, top N, or count only.</summary>
    public string Status => !IsPeekable ? "count only"
        : IsLoading ? "peeking…"
        : !_loaded ? "not loaded"
        : HasMore ? $"top {Rows.Count} · more waiting"
        : $"all {Rows.Count} peeked";

    /// <summary>The card's footer.</summary>
    public string CardFooter => Count == 0 ? "Nothing to peek" : !IsPeekable ? (Note is null ? "count only" : "count only, see note")
        : _loaded ? $"{Rows.Count} peeked" : $"top {_pageSize()} ready to peek";

    /// <summary>The load-more button text, with the cursor it continues from.</summary>
    public string LoadMoreText => _session is null ? string.Empty
        : $"Peek next {_pageSize()} from #{_session.NextSequence(Lane).ToString(CultureInfo.InvariantCulture)}";

    public AsyncCommand LoadMoreCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public RelayCommand ToggleCommand { get; }

    /// <summary>The lane card's click: open this section and bring it into view. Set by the entity.</summary>
    public RelayCommand JumpCommand { get; internal set; } = new(() => { });

    /// <summary>The row picked in the section's list. Picking one opens it in the drawer.</summary>
    public QuickMessageRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value) && value is not null)
                _open(value);
        }
    }

    public async Task LoadMoreAsync()
    {
        if (_session is null || IsLoading || (_loaded && !HasMore))
            return;
        IsLoading = true;
        Error = null;
        try
        {
            var page = await _session.LoadAsync(Lane, Math.Clamp(_pageSize(), 1, QuickPeekSession.MaxBatch)).ConfigureAwait(true);
            _scheduler.Post(() =>
            {
                foreach (var message in page.Messages)
                    Rows.Add(new QuickMessageRow(message, _open, _copy));
                _loaded = true;
                HasMore = page.HasMore;
                OnPropertyChanged(nameof(HasRows));
                Changed();
            });
        }
        catch (Exception exception) when (exception is BrokerException or InvalidOperationException or UnauthorizedAccessException or TimeoutException or IOException)
        {
            _scheduler.Post(() => Error = "Couldn't peek: " + exception.Message);
        }
        finally
        {
            _scheduler.Post(() => IsLoading = false);
        }
    }

    public async Task RefreshAsync()
    {
        if (_session is null)
            return;
        _session.Reset(Lane);
        _onReset?.Invoke(this);
        Forget();
        _isExpanded = true;
        OnPropertyChanged(nameof(IsExpanded));
        await LoadMoreAsync().ConfigureAwait(true);
    }

    /// <summary>Drops the rows after the shared sub-queue cursor started again (Active and Scheduled share the main one).</summary>
    internal void Forget()
    {
        Rows.Clear();
        _loaded = false;
        HasMore = false;
        if (_isExpanded)
        {
            _isExpanded = false;
            OnPropertyChanged(nameof(IsExpanded));
        }

        OnPropertyChanged(nameof(HasRows));
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CardFooter));
        OnPropertyChanged(nameof(CanLoadMore));
        OnPropertyChanged(nameof(LoadMoreText));
        LoadMoreCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }
}
