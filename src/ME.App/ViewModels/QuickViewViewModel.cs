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
    /// <summary>A queue or a topic: what you send to.</summary>
    Resource,

    /// <summary>A topic's subscription: what you read from. (A queue's receiver is the queue row itself.)</summary>
    Receiver,

    Placeholder,
}

/// <summary>
/// A row of the Resources list. A queue row is its own receiver and opens straight to its messages; a topic row
/// expands to its receivers, read the first time it is expanded (or searched).
/// </summary>
public sealed class QuickTreeNode : ViewModelBase
{
    private readonly Func<QuickTreeNode, Task>? _expand;
    private bool _isExpanded;
    private bool _isVisible = true;
    private bool _isPinned;
    private bool _showKindTag;

    public QuickTreeNode(Resource resource, Func<QuickTreeNode, Task>? expand = null)
        : this(QuickNodeKind.Resource, resource?.Name ?? throw new ArgumentNullException(nameof(resource)))
    {
        Resource = resource;
        Snapshot = resource.ToSnapshot();
        if (!resource.HasImplicitReceiver && expand is not null)
        {
            _expand = expand;
            Children.Add(Placeholder("Loading receivers…"));
        }
    }

    public QuickTreeNode(Receiver receiver)
        : this(QuickNodeKind.Receiver, receiver?.Name ?? throw new ArgumentNullException(nameof(receiver)))
    {
        Receiver = receiver;
        Snapshot = receiver.ToSnapshot();
    }

    private QuickTreeNode(QuickNodeKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public static QuickTreeNode Placeholder(string text) => new(QuickNodeKind.Placeholder, text);

    public QuickNodeKind Kind { get; }

    public string Name { get; }

    public Resource? Resource { get; }

    public Receiver? Receiver { get; }

    /// <summary>The inventory row the lanes read (a queue, a topic, or a subscription).</summary>
    public EntitySnapshot? Snapshot { get; }

    public ObservableCollection<QuickTreeNode> Children { get; } = [];

    public bool IsEntity => Snapshot is not null;

    public bool IsPlaceholder => Kind == QuickNodeKind.Placeholder;

    /// <summary>Topics expand to their receivers; queues have nothing to expand.</summary>
    public bool IsExpandable => _expand is not null;

    public string Icon => Kind switch
    {
        QuickNodeKind.Resource => EntityKindStyle.Icon(Resource!.Kind),
        QuickNodeKind.Receiver => EntityKindStyle.Icon(Receiver!.Kind),
        _ => "fa-solid fa-spinner",
    };

    /// <summary>QUEUE, TOPIC or SUB next to the name; null for placeholders.</summary>
    public string? KindTag => Kind switch
    {
        QuickNodeKind.Resource => EntityKindStyle.Tag(Resource!.Kind),
        QuickNodeKind.Receiver => EntityKindStyle.Tag(Receiver!.Kind),
        _ => null,
    };

    public bool HasKindTag => KindTag is not null;

    /// <summary>
    /// Whether the row shows its kind badge. Under All only topics and their receivers carry one (a queue is told by
    /// its icon and the icon's tooltip); under the Queues or Topics filter the kind is already known, so none do.
    /// </summary>
    public bool ShowKindTag
    {
        get => _showKindTag;
        internal set => Set(ref _showKindTag, value && HasKindTag);
    }

    /// <summary>The icon's tooltip and spoken name: Queue, Topic or Subscription.</summary>
    public string? KindName => Kind switch
    {
        QuickNodeKind.Resource => Resource!.Kind switch
        {
            ResourceKind.Queue => "Queue",
            ResourceKind.Topic => "Topic",
            ResourceKind.EventHub => "Event hub",
            _ => "Kafka topic",
        },
        QuickNodeKind.Receiver => Receiver!.Kind == ReceiverKind.ConsumerGroup ? "Consumer group" : "Subscription",
        _ => null,
    };

    /// <summary>Queue colour (teal) for queues; topic colour (purple) for topics and their receivers.</summary>
    public bool IsQueueFamily => Resource is { HasImplicitReceiver: true };

    public bool IsTopicFamily => Resource is { HasImplicitReceiver: false } || Receiver is not null;

    /// <summary>The resource a row belongs to: itself, or a receiver's topic.</summary>
    public string? ResourceName => Resource?.Name ?? Receiver?.ResourceName;

    /// <summary>
    /// The first badge: the active count. A topic has none of its own (its receivers carry the counts), so a
    /// topic row shows only a dead-letter badge, if any. Zero counts are not shown.
    /// </summary>
    public string? PrimaryBadge => Resource is { HasImplicitReceiver: false } ? null
        : Snapshot?.Counts.Active is > 0 and var active ? Short(active) : null;

    public bool HasPrimaryBadge => PrimaryBadge is not null;

    /// <summary>The first badge's spoken name and tooltip, e.g. "12 active".</summary>
    public string? PrimaryBadgeName => PrimaryBadge is null ? null
        : string.Create(CultureInfo.InvariantCulture, $"{Snapshot!.Counts.Active} active");

    public string? DeadLetterBadge => Snapshot?.Counts.DeadLetter is > 0 and var dead ? Short(dead) : null;

    public bool HasDeadLetterBadge => DeadLetterBadge is not null;

    /// <summary>The red badge's spoken name and tooltip, e.g. "3 dead-lettered".</summary>
    public string? DeadLetterName => Snapshot?.Counts.DeadLetter is > 0 and var dead
        ? string.Create(CultureInfo.InvariantCulture, $"{dead} dead-lettered")
        : null;

    /// <summary>How many count badges the row shows: at most two (active, and dead-lettered above 0).</summary>
    public int BadgeCount => (HasPrimaryBadge ? 1 : 0) + (HasDeadLetterBadge ? 1 : 0);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && value)
                LoadChildren();
        }
    }

    /// <summary>The receivers read, started by the first expand or search (tests await it).</summary>
    public Task ExpandTask { get; private set; } = Task.CompletedTask;

    /// <summary>Reads a topic's receivers once; later calls return the same read.</summary>
    public Task LoadChildren()
    {
        if (_expand is not null && !_loadStarted)
        {
            _loadStarted = true;
            ExpandTask = _expand(this);
        }

        return ExpandTask;
    }

    private bool _loadStarted;

    /// <summary>False when the kind filter or the search hides this row.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    /// <summary>The selected row, kept in view (dimmed) although the search or a filter would hide it.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        internal set => Set(ref _isPinned, value);
    }

    /// <summary>The selected row itself (not its topic) while it is pinned: it gets the pin icon.</summary>
    public bool IsPinnedSelection
    {
        get => _isPinnedSelection;
        internal set => Set(ref _isPinnedSelection, value);
    }

    private bool _isPinnedSelection;

    /// <summary>The pin icon's tooltip and spoken name.</summary>
    public const string PinTip = "Selected, doesn't match";

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
/// The Resources view: one list of resources (queues and topics) with a kind badge on each row, one kind filter,
/// a lane cards strip, sections per message type that cascade open as rows, and a detail drawer. Counts come
/// from runtime properties (one list read per connect). Rows come from a peek-only <see cref="IMessageBrowser"/>,
/// top N at a time, redacted before they are kept. A topic's receivers are read when it is expanded or searched.
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
    private ResourceKindFilter _kindFilter = ResourceKindFilter.All;
    private int _queueCount;
    private int _topicCount;
    private int _deadLetterQueues;
    private bool _loaded;
    private string? _notice;
    private bool _onlyDeadLetters;
    private int _shownCount;

    /// <param name="settings">Remembers the kind filter between launches; null keeps it for this session.</param>
    public QuickViewViewModel(IUiScheduler? scheduler = null, RedactionPipeline? redaction = null, Settings.IUiSettingsStore? settings = null)
    {
        _settings = settings;
        if (settings is not null)
        {
            try
            {
                _kindFilter = settings.Load().KindFilter;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // All: the default.
            }
        }

        _scheduler = scheduler ?? InlineUiScheduler.Instance;
        _redaction = redaction ?? QuickViewRedactionProfile.ForThisSession();
        RefreshCommand = new AsyncCommand(RefreshAsync, () => _inventory is not null);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    /// <summary>The Resources list: one row per queue or topic, sorted by name.</summary>
    public ObservableCollection<QuickTreeNode> Tree { get; } = [];

    /// <summary>All, Queues or Topics. A selection the filter hides is cleared. Remembered between launches.</summary>
    public ResourceKindFilter KindFilter
    {
        get => _kindFilter;
        set
        {
            if (!Set(ref _kindFilter, value))
                return;
            OnPropertyChanged(nameof(IsAllKinds));
            OnPropertyChanged(nameof(IsQueuesOnly));
            OnPropertyChanged(nameof(IsTopicsOnly));
            ApplyFilter();
            if (_selectedNode is not null && !Shows(_selectedNode))
                SelectedNode = null;
            try
            {
                _settings?.Save(_settings.Load() with { KindFilter = value });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The filter still works this session.
            }
        }
    }

    // The segmented control's three buttons. Only a true picks; a button clearing itself never does.
    public bool IsAllKinds
    {
        get => _kindFilter == ResourceKindFilter.All;
        set
        {
            if (value)
                KindFilter = ResourceKindFilter.All;
        }
    }

    public bool IsQueuesOnly
    {
        get => _kindFilter == ResourceKindFilter.Queues;
        set
        {
            if (value)
                KindFilter = ResourceKindFilter.Queues;
        }
    }

    public bool IsTopicsOnly
    {
        get => _kindFilter == ResourceKindFilter.Topics;
        set
        {
            if (value)
                KindFilter = ResourceKindFilter.Topics;
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

    /// <summary>The namespace was read and holds no resources this connection can see.</summary>
    public bool IsEmpty => _loaded && _queueCount == 0 && _topicCount == 0 && _error is null;

    public string EmptyText => HasNotice ? "No resources to show with this connection." : "This namespace has no queues or topics.";

    /// <summary>Only resources with dead letters (or receivers with them). Narrows like the search does.</summary>
    public bool OnlyDeadLetters
    {
        get => _onlyDeadLetters;
        set
        {
            if (!Set(ref _onlyDeadLetters, value))
                return;
            ApplyFilter();
            if (value)
                SearchLoad = LoadReceiversForSearchAsync();
        }
    }

    /// <summary>True while the kind filter, the dead-letters filter or the search hides part of the list.</summary>
    public bool IsNarrowed => _loaded && (_kindFilter != ResourceKindFilter.All || _onlyDeadLetters || _filter.Trim().Length > 0);

    /// <summary>
    /// While the list is narrowed: "6 of 9: Queues", "1 of 9 match 'audit'", "2 of 9: Queues, Has dead letters".
    /// The search is quoted after "match"; filters follow a colon. Empty otherwise (there is no summary line).
    /// </summary>
    public string MatchText
    {
        get
        {
            if (!IsNarrowed)
                return string.Empty;
            var filters = new List<string>();
            if (_kindFilter == ResourceKindFilter.Queues)
                filters.Add("Queues");
            else if (_kindFilter == ResourceKindFilter.Topics)
                filters.Add("Topics");
            if (_onlyDeadLetters)
                filters.Add("Has dead letters");
            var text = _filter.Trim();
            return string.Create(CultureInfo.InvariantCulture, $"{_shownCount} of {AllCount}")
                + (text.Length > 0 ? $" match '{text}'" : string.Empty)
                + (filters.Count > 0 ? ": " + string.Join(", ", filters) : string.Empty);
        }
    }

    /// <summary>Clears the search, the kind filter and the dead-letters filter.</summary>
    public RelayCommand ClearFiltersCommand { get; }

    private void ClearFilters()
    {
        _filter = string.Empty;
        OnPropertyChanged(nameof(Filter));
        _onlyDeadLetters = false;
        OnPropertyChanged(nameof(OnlyDeadLetters));
        KindFilter = ResourceKindFilter.All;
        ApplyFilter();
    }

    public int AllCount => _queueCount + _topicCount;

    public int QueueCount
    {
        get => _queueCount;
        private set
        {
            if (Set(ref _queueCount, value))
                OnPropertyChanged(nameof(AllCount));
        }
    }

    public int TopicCount
    {
        get => _topicCount;
        private set
        {
            if (Set(ref _topicCount, value))
                OnPropertyChanged(nameof(AllCount));
        }
    }

    private bool Shows(QuickTreeNode node)
    {
        var resource = node.Resource ?? (node.ResourceName is { } name ? Tree.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))?.Resource : null);
        return resource is null || resource.Matches(_kindFilter);
    }

    /// <summary>The clipboard. The view sets the real one; tests capture the text.</summary>
    public Func<string, Task> CopyText { get; set; } = _ => Task.CompletedTask;

    public bool IsConnected => _inventory is not null;

    public bool IsNotConnected => _inventory is null;

    public string NotConnectedText => "Choose a namespace (top left) to see its resources.";

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

    /// <summary>Top N: how many messages each section peeks per page (1 to 100). Set in Settings.</summary>
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

    /// <summary>The search box in the top bar: finds resources and receivers by name.</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value ?? string.Empty))
                return;
            ApplyFilter();
            if (_filter.Trim().Length > 0)
                SearchLoad = LoadReceiversForSearchAsync();
        }
    }

    /// <summary>Reading topics' receivers so the search can find them (tests await it).</summary>
    public Task SearchLoad { get; private set; } = Task.CompletedTask;

    public string NamespaceName
    {
        get => _namespaceName;
        private set => Set(ref _namespaceName, value);
    }

    /// <summary>Peek is available for this connection; otherwise every section is count only.</summary>
    public bool CanPeek => _openBrowser is not null;

    public string PeekText => CanPeek ? "Read-only · peek" : "Read-only · counts only";


    public QuickTreeNode? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (!Set(ref _selectedNode, value))
                return;
            Entity = value?.Snapshot is { } snapshot ? Build(snapshot) : null;
            ApplyFilter();
        }
    }

    /// <summary>The selected resource or receiver: its cards, sections and drawer.</summary>
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

    public string NoEntityText => "Pick a resource on the left. A queue opens its messages; a topic opens to its receivers.";

    public AsyncCommand RefreshCommand { get; }

    /// <summary>A new connection. Nothing is read until <see cref="EnsureLoadedAsync"/>.</summary>
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

    /// <summary>Reads the resources once after a connect.</summary>
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

    /// <summary>Finds the row for an entity (after a refresh, or from a test).</summary>
    public QuickTreeNode? Find(EntitySnapshot entity) => Walk(Tree).FirstOrDefault(n =>
        n.Snapshot is { } s && s.Kind == entity.Kind && string.Equals(s.Name, entity.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(s.Parent, entity.Parent, StringComparison.OrdinalIgnoreCase));

    /// <summary>A resource by name, or a receiver by name and its topic.</summary>
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
            var list = await ResourceReader.ReadAsync(inventory).ConfigureAwait(true);
            _scheduler.Post(() =>
            {
                Tree.Clear();
                _topics.Clear();
                foreach (var resource in list.Resources)
                {
                    if (resource.Kind == ResourceKind.Topic)
                        _topics[resource.Name] = resource.ToSnapshot();
                    Tree.Add(new QuickTreeNode(resource, LoadReceiversAsync));
                }

                QueueCount = list.Count(ResourceKind.Queue);
                TopicCount = list.Resources.Count - QueueCount;
                _deadLetterQueues = list.Resources.Count(r => r.HasImplicitReceiver && r.Counts.HasDeadLetters);
                Notice = list.Notice;
                _loaded = true;
                Loaded();
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
        Narrowed();
    }

    private void Narrowed()
    {
        OnPropertyChanged(nameof(IsNarrowed));
        OnPropertyChanged(nameof(MatchText));
    }

    private async Task LoadReceiversAsync(QuickTreeNode topic)
    {
        var inventory = _inventory;
        if (inventory is null || topic.Resource is not { } resource)
            return;
        try
        {
            var receivers = await ResourceReader.ReceiversAsync(inventory, resource).ConfigureAwait(true);
            _scheduler.Post(() =>
            {
                topic.Children.Clear();
                foreach (var receiver in receivers)
                    topic.Children.Add(new QuickTreeNode(receiver));
                if (topic.Children.Count == 0)
                    topic.Children.Add(QuickTreeNode.Placeholder("No receivers"));
                ApplyFilter();
            });
        }
        catch (BrokerException exception)
        {
            _scheduler.Post(() =>
            {
                topic.Children.Clear();
                topic.Children.Add(QuickTreeNode.Placeholder("Couldn't read receivers: " + exception.Message));
            });
        }
    }

    private async Task LoadReceiversForSearchAsync()
    {
        var reads = Tree.Where(n => n.IsExpandable).Select(n => n.LoadChildren()).ToList();
        await Task.WhenAll(reads).ConfigureAwait(true);
        _scheduler.Post(ApplyFilter);
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

    /// <summary>
    /// The kind filter, the dead-letters filter and the search together. A topic stays visible when one of its
    /// receivers matches, and opens. A selected row the search or the dead-letters filter would hide stays, pinned.
    /// </summary>
    private void ApplyFilter()
    {
        var text = _filter.Trim();
        var searching = text.Length > 0;
        bool Named(QuickTreeNode n) => n.Name.Contains(text, StringComparison.OrdinalIgnoreCase);
        bool Dead(QuickTreeNode n) => n.Snapshot?.Counts.HasDeadLetters == true;
        var shown = 0;
        foreach (var resource in Tree)
        {
            var kindShown = resource.Resource is not { } r || r.Matches(_kindFilter);
            var selfText = !searching || Named(resource);
            var anyReceiver = false;
            foreach (var child in resource.Children)
            {
                var childText = !searching || (child.IsEntity && (Named(child) || selfText));
                var childDead = !_onlyDeadLetters || (child.IsEntity && Dead(child));
                child.IsVisible = kindShown && childText && childDead;
                child.IsPinned = false;
                child.IsPinnedSelection = false;
                child.ShowKindTag = _kindFilter == ResourceKindFilter.All && child.IsTopicFamily;

                // A receiver that matches on its own (by name, or by dead letters) keeps its topic in the list.
                var ownMatch = child.IsEntity && (searching || _onlyDeadLetters)
                    && (!searching || Named(child)) && (!_onlyDeadLetters || Dead(child));
                anyReceiver |= kindShown && ownMatch;
            }

            var selfDead = !_onlyDeadLetters || Dead(resource);
            resource.IsVisible = kindShown && ((selfText && selfDead) || anyReceiver);
            resource.IsPinned = false;
            resource.IsPinnedSelection = false;
            resource.ShowKindTag = _kindFilter == ResourceKindFilter.All && resource.IsTopicFamily;
            if (resource.IsVisible)
                shown++;
            if (anyReceiver && !resource.IsExpanded)
                resource.IsExpanded = true;
        }

        _shownCount = shown;
        if (_selectedNode is { } selected && !selected.IsVisible && Shows(selected))
        {
            selected.IsVisible = true;
            selected.IsPinned = true;
            selected.IsPinnedSelection = true;
            if (selected.Receiver is not null && Tree.FirstOrDefault(t => t.Children.Contains(selected)) is { } parent)
            {
                if (!parent.IsVisible)
                {
                    parent.IsVisible = true;
                    parent.IsPinned = true;
                }

                parent.IsExpanded = true;
            }
        }

        Narrowed();
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
