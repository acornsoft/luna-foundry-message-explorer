// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Redaction;
using Acornsoft.MessageExplorer.Shell.QuickView;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// The middle and right of the quick view for one queue, topic or subscription: the lane cards strip, the
/// sections that cascade open, and the message detail drawer. Opening it peeks nothing; a section peeks
/// when it is opened (or its card is clicked).
/// </summary>
public sealed class QuickEntityViewModel : ViewModelBase
{
    private QuickMessageRow? _selected;
    private QuickLaneViewModel? _focused;

    public QuickEntityViewModel(
        EntitySnapshot entity,
        EntitySnapshot? topic,
        IMessageBrowser? browser,
        string? unavailable,
        RedactionPipeline redaction,
        Func<int> pageSize,
        Func<string, Task> copy,
        IUiScheduler? scheduler = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(redaction);
        Snapshot = entity;
        Name = entity.Name;
        CloseDrawerCommand = new RelayCommand(() => Selected = null);

        var counts = entity.Counts;
        switch (entity.Kind)
        {
            case EntityKind.Queue:
            {
                var target = BrowseEntity.Queue(entity.Name);
                var session = browser is null ? null : new QuickPeekSession(browser, target, redaction);
                Breadcrumb = $"queues / {entity.Name}";
                Lanes =
                [
                    Lane(MessageLane.Active, counts.Active, target.Path, session, Unavailable(session, unavailable)),
                    Lane(MessageLane.DeadLetter, counts.DeadLetter, target.PathOf(PeekSource.DeadLetter), session, Unavailable(session, unavailable)),
                    Lane(MessageLane.Scheduled, counts.Scheduled, target.Path, session, Unavailable(session, unavailable)),
                    Lane(MessageLane.TransferDeadLetter, counts.TransferDeadLetter, target.PathOf(PeekSource.TransferDeadLetter), session, Unavailable(session, unavailable)),
                ];
                break;
            }

            case EntityKind.Subscription:
            {
                var target = BrowseEntity.Subscription(entity.Parent!, entity.Name);
                var session = browser is null ? null : new QuickPeekSession(browser, target, redaction);
                Breadcrumb = $"topics / {entity.Parent} / {entity.Name}";
                var topicName = entity.Parent!;
                Lanes =
                [
                    Lane(MessageLane.Active, counts.Active, target.Path, session, Unavailable(session, unavailable)),
                    Lane(MessageLane.DeadLetter, counts.DeadLetter, target.PathOf(PeekSource.DeadLetter), session, Unavailable(session, unavailable)),
                    // One order for every resource: Active, Dead-letter, Scheduled, Transfer DL. A subscription has
                    // no Scheduled count of its own, and its peek doesn't return scheduled messages: they wait on the
                    // topic until they're due.
                    Lane(MessageLane.Scheduled, topic?.Counts.Scheduled, topicName, null,
                        $"On topic {topicName}: count only. A subscription peek doesn't return scheduled messages; they wait on the topic until they're due."),
                    Lane(MessageLane.TransferDeadLetter, counts.TransferDeadLetter, target.PathOf(PeekSource.TransferDeadLetter), session, Unavailable(session, unavailable)),
                ];
                break;
            }

            default:
                Breadcrumb = $"topics / {entity.Name}";
                Lanes =
                [
                    Lane(MessageLane.Scheduled, counts.Scheduled, entity.Name, null,
                        "Count only. A topic can't be peeked; open one of its subscriptions to see messages."),
                ];
                break;
        }

        foreach (var lane in Lanes)
        {
            var captured = lane;
            lane.JumpCommand = new RelayCommand(() => JumpTo(captured));
        }

        QuickLaneViewModel Lane(MessageLane lane, long? count, string path, QuickPeekSession? session, string? note) =>
            new(lane, count, path, session, note, pageSize, Open, copy, scheduler, Reset);
    }

    public EntitySnapshot Snapshot { get; }

    public string Name { get; }

    public string Breadcrumb { get; }

    public string Icon => EntityKindStyle.Icon(Snapshot.Kind);

    /// <summary>QUEUE, TOPIC or SUB beside the title, in the kind's colour.</summary>
    public string KindTag => EntityKindStyle.Tag(Snapshot.Kind);

    public bool IsQueueFamily => EntityKindStyle.IsQueueFamily(Snapshot.Kind);

    public bool IsTopicFamily => !IsQueueFamily;

    public string? Status => Snapshot.Status;

    public bool HasStatus => Snapshot.Status is not null;

    /// <summary>The sections, in order. The cards strip shows the same lanes.</summary>
    public IReadOnlyList<QuickLaneViewModel> Lanes { get; }

    /// <summary>The message open in the detail drawer, or null.</summary>
    public QuickMessageRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            if (value is null)
            {
                foreach (var lane in Lanes)
                    lane.SelectedRow = null;
            }
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>The lane whose card was clicked last; the view scrolls its section into view.</summary>
    public QuickLaneViewModel? Focused
    {
        get => _focused;
        private set => Set(ref _focused, value);
    }

    public RelayCommand CloseDrawerCommand { get; }

    /// <summary>A card click: open the lane's section (it peeks if it hasn't yet) and bring it into view.</summary>
    public void JumpTo(QuickLaneViewModel? lane)
    {
        if (lane is null)
            return;
        lane.IsExpanded = true;
        Focused = null;
        Focused = lane;
    }

    public QuickLaneViewModel? LaneOf(MessageLane lane) => Lanes.FirstOrDefault(l => l.Lane == lane);

    private void Open(QuickMessageRow row) => Selected = row;

    private void Reset(QuickLaneViewModel from)
    {
        var source = QuickPeekSession.SourceOf(from.Lane);
        foreach (var other in Lanes.Where(l => l != from && QuickPeekSession.SourceOf(l.Lane) == source && l.IsPeekable))
            other.Forget();
        if (Selected is { } open && QuickPeekSession.SourceOf(open.Message.Lane) == source)
            Selected = null;
    }

    private static string? Unavailable(QuickPeekSession? session, string? unavailable) => session is null ? unavailable : null;
}
