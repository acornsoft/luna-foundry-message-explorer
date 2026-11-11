// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Globalization;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// The Move workspace (shown while the mode chip reads Move). Off on every launch. While on, you pick a source in the tree (a dead-letter queue, a queue or
/// a subscription), see its messages (IDs only), and drag them, or the source itself, onto a queue or topic in
/// the tree. "Move to…" does the same from the keyboard. Every drop is checked, then confirmed in a dialog,
/// then run oldest first, and every step is written to the audit log.
/// </summary>
public sealed class MoveViewModel : ViewModelBase
{
    public const int DefaultCount = 10;

    private readonly MoveModeSwitch _moveMode;
    private readonly MoveServices _services;
    private readonly MoveEngine _engine;
    private IEntityInventory? _inventory;
    private IMoveTransport? _transport;
    private string? _refusal;
    private string? _actor;
    private MoveTreeNode? _selectedSource;
    private MoveTargetChoice? _selectedTarget;
    private IReadOnlyList<MoveMessageRow> _selectedMessages = [];
    private string _countText = DefaultCount.ToString(CultureInfo.InvariantCulture);
    private string? _status;
    private bool _statusIsError;
    private bool _isBusy;
    private CancellationTokenSource? _cancel;
    private (MoveTreeNode Source, IReadOnlyList<MoveMessageRow> Messages)? _drag;

    public MoveViewModel(MoveModeSwitch moveMode, MoveServices services, MoveEngine? engine = null)
    {
        _moveMode = moveMode ?? throw new ArgumentNullException(nameof(moveMode));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _engine = engine ?? new MoveEngine(moveMode, services.Audit);
        _moveMode.Changed += (_, _) => OnMoveModeChanged();
        MoveToCommand = new AsyncCommand(() => MoveSelectedAsync(), () => CanMove && SelectedTarget is not null);
        CancelCommand = new RelayCommand(() => _cancel?.Cancel(), () => IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _inventory is not null);
    }

    public MoveServices Services => _services;

    /// <summary>The window supplies the dialog. Without one, nothing can be confirmed, so nothing moves.</summary>
    public IMoveConfirmer? Confirmer { get; set; }

    /// <summary>Raised after a move changed counts, so the Queues and Topics grids can refresh.</summary>
    public event EventHandler? Moved;

    public ObservableCollection<MoveTreeNode> Tree { get; } = [];

    public ObservableCollection<MoveMessageRow> Messages { get; } = [];

    public ObservableCollection<MoveTargetChoice> TargetChoices { get; } = [];

    public AsyncCommand MoveToCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncCommand RefreshCommand { get; }

    public bool IsOn
    {
        get => _moveMode.IsOn;
        set
        {
            if (value)
                _moveMode.TurnOn();
            else
                _moveMode.TurnOff();
        }
    }

    public bool IsOff => !IsOn;

    public string Banner => MoveModeSwitch.Banner;

    public string OffText => "Move mode is off, so the app is read-only. Click the Read-only chip at the top right to switch to Move. It turns off again every time the app starts.";

    public bool HasNamespace => _inventory is not null;

    public string NoNamespaceText => "Choose a namespace first (top left), then pick a source here.";

    public bool IsRefused => _refusal is not null && _inventory is not null;

    public string? RefusalText => _refusal;

    public bool CanWork => IsOn && HasNamespace && !IsRefused;

    public bool CanMove => CanWork && !IsBusy && SelectedSource is { CanBeSource: true };

    public string AuditText => $"Audit log: {_services.AuditLocation}";

    public MoveTreeNode? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!Set(ref _selectedSource, value))
                return;
            OnPropertyChanged(nameof(MessagesTitle));
            OnPropertyChanged(nameof(CanMove));
            RebuildTargets();
            MessagesLoad = LoadMessagesAsync();
        }
    }

    /// <summary>The latest message list load, so tests can wait for it.</summary>
    public Task MessagesLoad { get; private set; } = Task.CompletedTask;

    public string MessagesTitle => SelectedSource is { CanBeSource: true } source
        ? $"Messages in {source.Endpoint!.Describe()}"
        : "Pick a queue, subscription or dead-letter queue in the tree";

    public MoveTargetChoice? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (Set(ref _selectedTarget, value))
                MoveToCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Rows picked in the list. Empty means "the oldest N".</summary>
    public IReadOnlyList<MoveMessageRow> SelectedMessages => _selectedMessages;

    public string CountText
    {
        get => _countText;
        set
        {
            if (Set(ref _countText, value))
                OnPropertyChanged(nameof(SelectionText));
        }
    }

    public string SelectionText => _selectedMessages.Count > 0
        ? $"{_selectedMessages.Count} picked. Dropping moves only these, oldest first."
        : $"Nothing picked. Dropping moves the oldest {CountText} (at most {MoveSelection.MaxBatch}).";

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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value))
                return;
            OnPropertyChanged(nameof(CanMove));
            CancelCommand.RaiseCanExecuteChanged();
            MoveToCommand.RaiseCanExecuteChanged();
        }
    }

    public void SetSelectedMessages(IEnumerable<MoveMessageRow> rows)
    {
        _selectedMessages = rows.ToList();
        OnPropertyChanged(nameof(SelectedMessages));
        OnPropertyChanged(nameof(SelectionText));
    }

    /// <summary>Opens Move mode on a namespace. Nothing connects until Move mode is on and a source is picked.</summary>
    public async Task LoadAsync(IEntityInventory inventory, MoveAccess access, string? actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(access);
        await ClearAsync().ConfigureAwait(true);
        _inventory = inventory;
        _actor = actor;
        _refusal = access.Refusal;
        if (access.Open is { } open)
        {
            try
            {
                _transport = open();
            }
            catch (Exception exception) when (exception is BrokerException or ArgumentException or InvalidOperationException)
            {
                _refusal = "Move mode can't open this connection: " + exception.Message;
            }
        }

        RaiseState();

        // Read nothing extra while Move mode is off; the tree loads when it is turned on.
        if (IsOn)
            await BuildTreeAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>The tree load started when Move mode was turned on, so tests can wait for it.</summary>
    public Task TreeLoad { get; private set; } = Task.CompletedTask;

    public async Task ClearAsync()
    {
        _cancel?.Cancel();
        var transport = _transport;
        _transport = null;
        _inventory = null;
        _refusal = null;
        Tree.Clear();
        Messages.Clear();
        TargetChoices.Clear();
        _selectedSource = null;
        OnPropertyChanged(nameof(SelectedSource));
        OnPropertyChanged(nameof(MessagesTitle));
        SetSelectedMessages([]);
        Status = null;
        RaiseState();
        if (transport is not null)
            await transport.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Remembers what is being dragged: picked messages, or (with none) the source node itself.</summary>
    public bool BeginDrag(MoveTreeNode? source, IReadOnlyList<MoveMessageRow>? messages = null)
    {
        source ??= SelectedSource;
        if (!CanWork || IsBusy || source is not { CanBeSource: true })
        {
            _drag = null;
            return false;
        }

        _drag = (source, messages ?? []);
        return true;
    }

    /// <summary>Whether the current drag may drop on <paramref name="target"/>. Used to highlight the tree.</summary>
    public bool CanDropOn(MoveTreeNode? target) =>
        _drag is { } drag && target is { CanBeTarget: true } && MoveRoutes.IsAllowed(drag.Source.Endpoint!, target.Endpoint!);

    public void EndDrag() => _drag = null;

    /// <summary>The drop. Checks, asks, then moves.</summary>
    public async Task<MoveResult?> DropAsync(MoveTreeNode target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_drag is not { } drag)
            return null;
        _drag = null;
        return await MoveAsync(drag.Source, drag.Messages, target).ConfigureAwait(true);
    }

    /// <summary>"Move to…" from the keyboard: the picked messages (or the oldest N) of the selected source to the chosen target.</summary>
    public Task<MoveResult?> MoveSelectedAsync() =>
        SelectedSource is { } source && SelectedTarget is { } target
            ? MoveAsync(source, SelectedMessages, target.Node)
            : Task.FromResult<MoveResult?>(null);

    private async Task<MoveResult?> MoveAsync(MoveTreeNode source, IReadOnlyList<MoveMessageRow> picked, MoveTreeNode target)
    {
        if (!IsOn)
        {
            ShowError("Move mode is off. Turn it on to move messages.");
            return null;
        }

        if (_transport is null || _inventory is null)
        {
            ShowError(_refusal ?? NoNamespaceText);
            return null;
        }

        if (source.Endpoint is null || target.Endpoint is null)
            return null;

        MoveSelection selection;
        if (picked.Count > 0)
        {
            selection = MoveSelection.Of(picked.Select(p => p.SequenceNumber));
        }
        else if (int.TryParse(CountText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
        {
            selection = MoveSelection.OldestFirst(count);
        }
        else
        {
            ShowError($"Enter how many messages to move (1 to {MoveSelection.MaxBatch}).");
            return null;
        }

        IsBusy = true;
        try
        {
            MovePlan plan;
            try
            {
                plan = await _engine.PrepareAsync(new MoveRequest(source.Endpoint, target.Endpoint, selection, _actor), _transport).ConfigureAwait(true);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ShowError(MoveEngine.Describe(exception));
                return null;
            }

            if (Confirmer is null || !await Confirmer.ConfirmAsync(plan).ConfigureAwait(true))
            {
                ShowInfo("Not moved. Nothing changed.");
                return null;
            }

            plan.Confirm();
            _cancel = new CancellationTokenSource();
            Status = $"Moving {plan.Count}…";
            StatusIsError = false;
            var finished = false;
            var progress = new Progress<MoveProgress>(p =>
            {
                // Progress reports are posted, so a late one can arrive after the result. Ignore those.
                if (!finished)
                    Status = $"Moved {p.Moved} of {p.Requested}…";
            });
            var result = await _engine.ExecuteAsync(plan, progress, _cancel.Token).ConfigureAwait(true);
            finished = true;
            if (result.Outcome is MoveOutcome.Completed or MoveOutcome.Partial)
                ShowInfo(result.Message);
            else
                ShowError(result.Message);
            await RefreshAsync().ConfigureAwait(true);
            Moved?.Invoke(this, EventArgs.Empty);
            return result;
        }
        finally
        {
            _cancel?.Dispose();
            _cancel = null;
            IsBusy = false;
        }
    }

    private async Task RefreshAsync()
    {
        await UpdateCountsAsync().ConfigureAwait(true);
        await LoadMessagesAsync().ConfigureAwait(true);
    }

    private async Task BuildTreeAsync(CancellationToken cancellationToken)
    {
        if (_inventory is null)
            return;
        var ns = _transport?.Namespace ?? "namespace";
        try
        {
            var queues = new MoveTreeNode("Queues");
            foreach (var row in (await _inventory.ListQueuesAsync(cancellationToken).ConfigureAwait(true)).Entities.Where(e => e.Kind == EntityKind.Queue))
            {
                var queue = new MoveTreeNode(row.Name, MoveEndpoint.Queue(ns, row.Name), row.Counts.Active);
                queue.Children.Add(new MoveTreeNode("Dead-letter", MoveEndpoint.Queue(ns, row.Name, deadLetter: true), row.Counts.DeadLetter));
                queues.Children.Add(queue);
            }

            var topics = new MoveTreeNode("Topics");
            if (_inventory.Provider.Supports(BrokerCapabilities.ListTopics))
            {
                foreach (var row in (await _inventory.ListTopicsAsync(cancellationToken).ConfigureAwait(true)).Entities.Where(e => e.Kind == EntityKind.Topic))
                {
                    var topic = new MoveTreeNode(row.Name, MoveEndpoint.TopicOf(ns, row.Name));
                    foreach (var sub in (await _inventory.ListSubscriptionsAsync(row.Name, cancellationToken).ConfigureAwait(true)).Entities.Where(e => e.Kind == EntityKind.Subscription))
                    {
                        var node = new MoveTreeNode(sub.Name, MoveEndpoint.Subscription(ns, row.Name, sub.Name), sub.Counts.Active);
                        node.Children.Add(new MoveTreeNode("Dead-letter", MoveEndpoint.Subscription(ns, row.Name, sub.Name, deadLetter: true), sub.Counts.DeadLetter));
                        topic.Children.Add(node);
                    }

                    topics.Children.Add(topic);
                }
            }

            var fresh = new[] { queues, topics }.SelectMany(n => n.SelfAndDescendants()).ToList();
            var current = Tree.SelectMany(n => n.SelfAndDescendants()).ToList();
            if (current.Count == fresh.Count && current.Zip(fresh).All(p => p.First.Label == p.Second.Label && p.First.Endpoint == p.Second.Endpoint))
            {
                // Same entities as before: update the counts in place so the tree keeps its selection and scroll.
                foreach (var (old, now) in current.Zip(fresh))
                    old.Count = now.Count;
                return;
            }

            Tree.Clear();
            Tree.Add(queues);
            Tree.Add(topics);
        }
        catch (Exception exception) when (exception is BrokerException or MoveTransportException)
        {
            ShowError(exception.Message);
        }
    }

    private async Task UpdateCountsAsync()
    {
        if (_inventory is null)
            return;
        var selected = SelectedSource?.Endpoint;
        var target = SelectedTarget?.Node.Endpoint;
        await BuildTreeAsync(CancellationToken.None).ConfigureAwait(true);
        var nodes = Tree.SelectMany(n => n.SelfAndDescendants()).ToList();
        _selectedSource = nodes.FirstOrDefault(n => n.Endpoint == selected);
        OnPropertyChanged(nameof(SelectedSource));
        RebuildTargets();
        SelectedTarget = TargetChoices.FirstOrDefault(c => c.Node.Endpoint == target);
    }

    private void RebuildTargets()
    {
        TargetChoices.Clear();
        if (SelectedSource is { CanBeSource: true, Endpoint: { } source })
        {
            foreach (var node in Tree.SelectMany(n => n.SelfAndDescendants()).Where(n => n.CanBeTarget && MoveRoutes.IsAllowed(source, n.Endpoint!)))
                TargetChoices.Add(new MoveTargetChoice(node));
        }

        SelectedTarget = TargetChoices.Count == 1 ? TargetChoices[0] : null;
        MoveToCommand.RaiseCanExecuteChanged();
    }

    private async Task LoadMessagesAsync()
    {
        Messages.Clear();
        SetSelectedMessages([]);
        if (!CanWork || _transport is null || SelectedSource is not { CanBeSource: true, Endpoint: { } source })
            return;
        try
        {
            foreach (var info in await _transport.PeekAsync(source, MoveSelection.MaxBatch, CancellationToken.None).ConfigureAwait(true))
                Messages.Add(new MoveMessageRow(info));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError("Couldn't list messages: " + MoveEngine.Describe(exception));
        }
    }

    private void OnMoveModeChanged()
    {
        RaiseState();
        if (IsOn)
        {
            ShowInfo(null);
            if (Tree.Count == 0 && _inventory is not null)
                TreeLoad = BuildTreeAsync(CancellationToken.None);
            MessagesLoad = LoadMessagesAsync();
        }
        else
        {
            _cancel?.Cancel();
            Messages.Clear();
            SetSelectedMessages([]);
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsOff));
        OnPropertyChanged(nameof(HasNamespace));
        OnPropertyChanged(nameof(IsRefused));
        OnPropertyChanged(nameof(RefusalText));
        OnPropertyChanged(nameof(CanWork));
        OnPropertyChanged(nameof(CanMove));
        MoveToCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
    }

    private void ShowError(string message)
    {
        Status = message;
        StatusIsError = true;
    }

    private void ShowInfo(string? message)
    {
        Status = message;
        StatusIsError = false;
    }
}
