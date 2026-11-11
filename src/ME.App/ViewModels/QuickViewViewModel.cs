// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Globalization;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum QuickNodeKind
{
    Namespace,
    Group,
    Queue,
    Topic,
    Subscription,
    Placeholder,
}

/// <summary>A node of the entity tree. A topic's subscriptions are read the first time it is expanded.</summary>
public sealed class QuickTreeNode : ViewModelBase
{
    private readonly Func<QuickTreeNode, Task>? _expand;
    private bool _isExpanded;
    private bool _expandedOnce;
    private bool _isVisible = true;

    public QuickTreeNode(QuickNodeKind kind, string name, EntitySnapshot? snapshot = null, Func<QuickTreeNode, Task>? expand = null)
    {
        Kind = kind;
        Name = name;
        Snapshot = snapshot;
        _expand = expand;
        if (expand is not null)
            Children.Add(new QuickTreeNode(QuickNodeKind.Placeholder, "Loading subscriptions…"));
    }

    public QuickNodeKind Kind { get; }

    public string Name { get; }

    public EntitySnapshot? Snapshot { get; }

    public ObservableCollection<QuickTreeNode> Children { get; } = [];

    public bool IsEntity => Snapshot is not null;

    public bool IsGroup => Kind == QuickNodeKind.Group;

    public bool IsPlaceholder => Kind == QuickNodeKind.Placeholder;

    public string Icon => Kind switch
    {
        QuickNodeKind.Namespace => "fa-solid fa-layer-group",
        QuickNodeKind.Queue => EntityKindStyle.QueueIcon,
        QuickNodeKind.Topic => EntityKindStyle.TopicIcon,
        QuickNodeKind.Subscription => EntityKindStyle.SubscriptionIcon,
        QuickNodeKind.Placeholder => "fa-solid fa-spinner",
        _ => "fa-solid fa-folder",
    };

    /// <summary>QUEUE, TOPIC or SUB, next to the name; null for the namespace, groups and placeholders.</summary>
    public string? KindTag => Snapshot is { } entity ? EntityKindStyle.Tag(entity.Kind) : null;

    public bool HasKindTag => KindTag is not null;

    /// <summary>Queue colour (teal) for queues; topic colour (purple) for topics and their subscriptions.</summary>
    public bool IsQueueFamily => Kind == QuickNodeKind.Queue;

    public bool IsTopicFamily => Kind is QuickNodeKind.Topic or QuickNodeKind.Subscription;

    /// <summary>The first badge: active count (or a topic's subscription count).</summary>
    public string? PrimaryBadge => Snapshot switch
    {
        { Kind: EntityKind.Topic } topic => topic.SubscriptionCount?.ToString(CultureInfo.InvariantCulture),
        { } entity => Short(entity.Counts.Active),
        _ => null,
    };

    public bool HasPrimaryBadge => PrimaryBadge is not null;

    public string? DeadLetterBadge => Snapshot?.Counts.DeadLetter is > 0 and var dead ? Short(dead) : null;

    public bool HasDeadLetterBadge => DeadLetterBadge is not null;

    public string? ScheduledBadge => Snapshot is { Kind: not EntityKind.Topic } && Snapshot.Counts.Scheduled is > 0 and var due ? Short(due) : null;

    public bool HasScheduledBadge => ScheduledBadge is not null;

    public string? TransferBadge => Snapshot?.Counts.TransferDeadLetter is > 0 and var transfer ? Short(transfer) : null;

    public bool HasTransferBadge => TransferBadge is not null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value) || !value || _expandedOnce || _expand is null)
                return;
            _expandedOnce = true;
            ExpandTask = _expand(this);
        }
    }

    /// <summary>The subscription read started by the first expand (tests await it).</summary>
    public Task ExpandTask { get; private set; } = Task.CompletedTask;

    /// <summary>False when the find box hides this node.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    internal static string? Short(long? value) => value switch
    {
        null => null,
        >= 1_000_000 => (value.Value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "m",
        >= 10_000 => (value.Value / 1_000.0).ToString("0", CultureInfo.InvariantCulture) + "k",
        >= 1_000 => (value.Value / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.Value.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// The Quick view tab: an entity tree, a lane cards strip, sections per message type that cascade open as
/// rows, and a detail drawer. Counts come from runtime properties (the inventory). Rows come from a
/// peek-only <see cref="IMessageBrowser"/>, top N at a time, redacted before they are kept. Nothing is read
/// until the tab is first shown; a topic's subscriptions are read when it is expanded.
/// </summary>
public sealed class QuickViewViewModel : ViewModelBase
{
    public const int DefaultPageSize = 25;

    private readonly IUiScheduler _scheduler;
    private readonly RedactionPipeline _redaction;
    private IEntityInventory? _inventory;
    private Func<IMessageBrowser?>? _openBrowser;
    private IMessageBrowser? _browser;
    private bool _browserOpened;
    private Task? _load;
    private QuickTreeNode? _selectedNode;
    private QuickEntityViewModel? _entity;
    private int _pageSize = DefaultPageSize;
    private string _filter = string.Empty;
    private string? _error;
    private bool _isLoading;
    private string _namespaceName = "namespace";
    private readonly Dictionary<string, EntitySnapshot> _topics = new(StringComparer.OrdinalIgnoreCase);
    private readonly Settings.IUiSettingsStore? _settings;
    private bool _showQueues = true;
    private bool _showTopics = true;
    private int _queueCount;
    private int _topicCount;
    private int _deadLetterQueues;
    private bool _loaded;
    private string? _notice;

    /// <param name="settings">Remembers the Queues and Topics toggles between launches; null keeps them for this session.</param>
    public QuickViewViewModel(IUiScheduler? scheduler = null, RedactionPipeline? redaction = null, Settings.IUiSettingsStore? settings = null)
    {
        _settings = settings;
        if (settings is not null)
        {
            try
            {
                var saved = settings.Load();
                _showQueues = saved.ShowQueues;
                _showTopics = saved.ShowTopics;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Both on: the defaults.
            }
        }

        _scheduler = scheduler ?? InlineUiScheduler.Instance;
        _redaction = redaction ?? QuickViewRedactionProfile.ForThisSession();
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _inventory is not null);
    }

    public ObservableCollection<QuickTreeNode> Tree { get; } = [];

    /// <summary>The Queues toggle above the tree. Off hides every queue; a hidden selection is cleared.</summary>
    public bool ShowQueues
    {
        get => _showQueues;
        set
        {
            if (Set(ref _showQueues, value))
                KindsChanged();
        }
    }

    /// <summary>The Topics toggle above the tree. Off hides every topic and its subscriptions; a hidden selection is cleared.</summary>
    public bool ShowTopics
    {
        get => _showTopics;
        set
        {
            if (Set(ref _showTopics, value))
                KindsChanged();
        }
    }

    /// <summary>Why some counts are missing (for example a key scoped to one entity). Null when nothing to say.</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (Set(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrEmpty(_notice);

    /// <summary>The namespace was read and holds no queues or topics this connection can see.</summary>
    public bool IsEmpty => _loaded && _queueCount == 0 && _topicCount == 0 && _error is null;

    public string EmptyText => HasNotice ? "No queues or topics to show with this connection." : "This namespace has no queues or topics.";

    /// <summary>One line over the tree, e.g. "4 queues (2 with dead letters), 2 topics. Read-only."</summary>
    public string Summary => !_loaded || _error is not null
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture,
            $"{Plural(_queueCount, "queue")}{(_deadLetterQueues > 0 ? $" ({_deadLetterQueues} with dead letters)" : string.Empty)}, {Plural(_topicCount, "topic")}. Read-only.");

    public bool HasSummary => Summary.Length > 0;

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? string.Empty : "s")}");

    public int QueueCount
    {
        get => _queueCount;
        private set => Set(ref _queueCount, value);
    }

    public int TopicCount
    {
        get => _topicCount;
        private set => Set(ref _topicCount, value);
    }

    /// <summary>Both toggles off: say why the tree is empty.</summary>
    public bool NothingShown => !_showQueues && !_showTopics;

    private void KindsChanged()
    {
        OnPropertyChanged(nameof(NothingShown));
        OnPropertyChanged(nameof(NoEntityText));
        ApplyFilter();
        if (_selectedNode is { Snapshot: { } selected } && !Shows(selected.Kind))
            SelectedNode = null;
        try
        {
            _settings?.Save(_settings.Load() with { ShowQueues = _showQueues, ShowTopics = _showTopics });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The toggles still work this session.
        }
    }

    private bool Shows(EntityKind kind) => kind == EntityKind.Queue ? _showQueues : _showTopics;

    /// <summary>The clipboard. The view sets the real one; tests capture the text.</summary>
    public Func<string, Task> CopyText { get; set; } = _ => Task.CompletedTask;

    public bool IsConnected => _inventory is not null;

    public bool IsNotConnected => _inventory is null;

    public string NotConnectedText => "Connect to a namespace to see its queues and topics.";

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
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

    public bool HasError => _error is not null;

    /// <summary>Top N: how many messages each section peeks per page (1 to 100).</summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (Set(ref _pageSize, Math.Clamp(value, 1, QuickView.QuickPeekSession.MaxBatch)))
                OnPropertyChanged(nameof(PageSizeText));
        }
    }

    public string PageSizeText => $"Top {_pageSize.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>The find box: hides entities whose names don't contain the text.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? string.Empty))
                ApplyFilter();
        }
    }

    public string NamespaceName
    {
        get => _namespaceName;
        private set => Set(ref _namespaceName, value);
    }

    /// <summary>Peek is available for this connection; otherwise every section is count only.</summary>
    public bool CanPeek => _openBrowser is not null;

    public string PeekText => CanPeek ? "Read-only · peek" : "Read-only · counts only";

    public string FooterText => "Counts: runtime properties. Each section peeks only when opened. Peek only: nothing is locked, settled or changed. Bodies masked.";

    public QuickTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value))
                return;
            Entity = value?.Snapshot is { } snapshot ? Build(snapshot) : null;
        }
    }

    /// <summary>The selected entity's cards, sections and drawer.</summary>
    public QuickEntityViewModel? Entity
    {
        get => _entity;
        private set
        {
            if (Set(ref _entity, value))
                OnPropertyChanged(nameof(HasEntity));
        }
    }

    public bool HasEntity => _entity is not null;

    public string NoEntityText => NothingShown
        ? "Queues and Topics are both hidden. Turn one on above the tree."
        : "Pick a queue, topic or subscription on the left.";

    public AsyncCommand RefreshCommand { get; }

    /// <summary>A new connection. Nothing is read until <see cref="EnsureLoadedAsync"/> (the tab is shown).</summary>
    /// <param name="openBrowser">Opens the peek-only browser on first use; null or a null result means counts only.</param>
    public void Attach(IEntityInventory inventory, string namespaceName, Func<IMessageBrowser?>? openBrowser)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        _ = ClearAsync();
        _inventory = inventory;
        _openBrowser = openBrowser;
        NamespaceName = string.IsNullOrWhiteSpace(namespaceName) ? "namespace" : namespaceName;
        Changed();
    }

    /// <summary>Reads queues and topics the first time the tab is shown after a connect.</summary>
    public Task EnsureLoadedAsync() => _inventory is null ? Task.CompletedTask : _load ??= LoadTreeAsync();

    public async Task RefreshAsync()
    {
        if (_inventory is null)
            return;
        var selected = _selectedNode?.Snapshot;
        _load = LoadTreeAsync();
        await _load.ConfigureAwait(true);
        if (selected is not null)
            SelectedNode = Find(selected);
    }

    public async Task ClearAsync()
    {
        _inventory = null;
        _openBrowser = null;
        _load = null;
        _topics.Clear();
        Tree.Clear();
        SelectedNode = null;
        Error = null;
        Notice = null;
        _loaded = false;
        QueueCount = 0;
        TopicCount = 0;
        _deadLetterQueues = 0;
        Loaded();
        var browser = _browser;
        _browser = null;
        _browserOpened = false;
        Changed();
        if (browser is not null)
            await browser.DisposeAsync().ConfigureAwait(true);
    }

    /// <summary>Finds the tree node for an entity (after a refresh, or from a test).</summary>
    public QuickTreeNode? Find(EntitySnapshot entity) => Walk(Tree).FirstOrDefault(n =>
        n.Snapshot is { } s && s.Kind == entity.Kind && string.Equals(s.Name, entity.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(s.Parent, entity.Parent, StringComparison.OrdinalIgnoreCase));

    public QuickTreeNode? Find(string name, string? topic = null) => Walk(Tree).FirstOrDefault(n =>
        n.Snapshot is { } s && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(s.Parent, topic, StringComparison.OrdinalIgnoreCase));

    private async Task LoadTreeAsync()
    {
        var inventory = _inventory!;
        IsLoading = true;
        Error = null;
        try
        {
            var queues = await inventory.ListQueuesAsync().ConfigureAwait(true);
            var topics = await inventory.ListTopicsAsync().ConfigureAwait(true);
            _scheduler.Post(() =>
            {
                Tree.Clear();
                _topics.Clear();
                var root = new QuickTreeNode(QuickNodeKind.Namespace, NamespaceName) { IsExpanded = true };
                var queueGroup = new QuickTreeNode(QuickNodeKind.Group, "Queues") { IsExpanded = true };
                foreach (var queue in queues.Entities)
                    queueGroup.Children.Add(new QuickTreeNode(QuickNodeKind.Queue, queue.Name, queue));
                var topicGroup = new QuickTreeNode(QuickNodeKind.Group, "Topics") { IsExpanded = true };
                foreach (var topic in topics.Entities)
                {
                    _topics[topic.Name] = topic;
                    topicGroup.Children.Add(new QuickTreeNode(QuickNodeKind.Topic, topic.Name, topic, LoadSubscriptionsAsync));
                }

                QueueCount = queueGroup.Children.Count;
                TopicCount = topicGroup.Children.Count;
                _deadLetterQueues = queues.Entities.Count(q => q.Counts.DeadLetter > 0 || q.Counts.TransferDeadLetter > 0);
                Notice = queues.Notice ?? topics.Notice;
                _loaded = true;
                Loaded();
                if (queueGroup.Children.Count > 0)
                    root.Children.Add(queueGroup);
                if (topicGroup.Children.Count > 0)
                    root.Children.Add(topicGroup);
                Tree.Add(root);
                ApplyFilter();
            });
        }
        catch (BrokerException exception)
        {
            _scheduler.Post(() =>
            {
                Error = exception.Message;
                Loaded();
            });
        }
        finally
        {
            _scheduler.Post(() => IsLoading = false);
        }
    }

    private void Loaded()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSummary));
    }

    private async Task LoadSubscriptionsAsync(QuickTreeNode topic)
    {
        var inventory = _inventory;
        if (inventory is null)
            return;
        try
        {
            var result = await inventory.ListSubscriptionsAsync(topic.Name).ConfigureAwait(true);
            _scheduler.Post(() =>
            {
                topic.Children.Clear();
                foreach (var subscription in result.Entities)
                    topic.Children.Add(new QuickTreeNode(QuickNodeKind.Subscription, subscription.Name, subscription));
                if (topic.Children.Count == 0)
                    topic.Children.Add(new QuickTreeNode(QuickNodeKind.Placeholder, "No subscriptions"));
                ApplyFilter();
            });
        }
        catch (BrokerException exception)
        {
            _scheduler.Post(() =>
            {
                topic.Children.Clear();
                topic.Children.Add(new QuickTreeNode(QuickNodeKind.Placeholder, "Couldn't read subscriptions: " + exception.Message));
            });
        }
    }

    private QuickEntityViewModel Build(EntitySnapshot snapshot)
    {
        var browser = Browser();
        var unavailable = browser is null
            ? $"Count only. Peek isn't available for {_inventory?.Provider.DisplayName ?? "this provider"} on this connection."
            : null;
        EntitySnapshot? topic = null;
        if (snapshot.Kind == EntityKind.Subscription && snapshot.Parent is { } parent)
            _topics.TryGetValue(parent, out topic);
        return new QuickEntityViewModel(snapshot, topic, browser, unavailable, _redaction, () => PageSize, text => CopyText(text), _scheduler);
    }

    private IMessageBrowser? Browser()
    {
        if (!_browserOpened)
        {
            _browserOpened = true;
            _browser = _openBrowser?.Invoke();
        }

        return _browser;
    }

    private void ApplyFilter()
    {
        var text = _filter.Trim();
        foreach (var root in Tree)
            Visible(root, text, null);
    }

    /// <summary>The find box and the kind toggles together. <paramref name="kind"/> is the queue or topic branch the node is in.</summary>
    private bool Visible(QuickTreeNode node, string text, EntityKind? kind)
    {
        kind ??= node.Kind switch
        {
            QuickNodeKind.Queue => EntityKind.Queue,
            QuickNodeKind.Topic or QuickNodeKind.Subscription => EntityKind.Topic,
            QuickNodeKind.Group when node.Name == "Queues" => EntityKind.Queue,
            QuickNodeKind.Group when node.Name == "Topics" => EntityKind.Topic,
            _ => null,
        };
        if (kind is { } k && !Shows(k))
        {
            node.IsVisible = false;
            foreach (var child in node.Children)
                Visible(child, text, k);
            return false;
        }

        var any = false;
        foreach (var child in node.Children)
            any |= Visible(child, text, kind);
        var self = node.Kind == QuickNodeKind.Namespace
            ? text.Length == 0 && !NothingShown
            : text.Length == 0 || (node.IsEntity && node.Name.Contains(text, StringComparison.OrdinalIgnoreCase));
        node.IsVisible = self || any || node.IsPlaceholder && text.Length == 0;
        return node.IsVisible;
    }

    private static IEnumerable<QuickTreeNode> Walk(IEnumerable<QuickTreeNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Walk(node.Children))
                yield return child;
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsNotConnected));
        OnPropertyChanged(nameof(CanPeek));
        OnPropertyChanged(nameof(PeekText));
        RefreshCommand.RaiseCanExecuteChanged();
    }
}
