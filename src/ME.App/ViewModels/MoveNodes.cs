// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Globalization;
using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// One row of the Move tree: a group heading, a queue, a topic, a subscription, or a dead-letter queue.
/// A node with an endpoint can be a source (queues, subscriptions, dead-letter queues) or a drop target
/// (queues and topics).
/// </summary>
public sealed class MoveTreeNode : ViewModelBase
{
    private long? _count;
    private bool _isExpanded = true;
    private bool _isDropTarget;

    public MoveTreeNode(string label, MoveEndpoint? endpoint = null, long? count = null)
    {
        Label = label;
        Endpoint = endpoint;
        _count = count;
    }

    public string Label { get; }

    public MoveEndpoint? Endpoint { get; }

    public ObservableCollection<MoveTreeNode> Children { get; } = [];

    public long? Count
    {
        get => _count;
        set
        {
            if (Set(ref _count, value))
                OnPropertyChanged(nameof(CountText));
        }
    }

    public string CountText => _count is { } count ? count.ToString("N0", CultureInfo.InvariantCulture) : string.Empty;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>True while a drag is over this node and the drop would be allowed. The tree highlights it.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set => Set(ref _isDropTarget, value);
    }

    public bool CanBeSource => Endpoint is { Kind: not MoveEntityKind.Topic };

    public bool CanBeTarget => Endpoint is { DeadLetter: false, Kind: MoveEntityKind.Queue or MoveEntityKind.Topic };

    public bool IsDeadLetter => Endpoint?.DeadLetter == true;

    public IEnumerable<MoveTreeNode> SelfAndDescendants() => Children.SelectMany(c => c.SelfAndDescendants()).Prepend(this);

    public override string ToString() => Endpoint?.Describe() ?? Label;
}

/// <summary>One message in the Move list: IDs, times and reason only. Never a body.</summary>
public sealed class MoveMessageRow(MoveMessageInfo info)
{
    public MoveMessageInfo Info { get; } = info;

    public long SequenceNumber => Info.SequenceNumber;

    public string SequenceText => Info.SequenceNumber.ToString(CultureInfo.InvariantCulture);

    public string MessageId => Info.MessageId ?? "(no ID)";

    public string SessionText => Info.SessionId ?? "-";

    public string EnqueuedText => Info.EnqueuedTime.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    public string DeliveryText => Info.DeliveryCount.ToString(CultureInfo.InvariantCulture);

    public string ReasonText => Info.DeadLetterReason ?? string.Empty;
}

/// <summary>A target in the keyboard "Move to…" list.</summary>
public sealed record MoveTargetChoice(MoveTreeNode Node)
{
    public override string ToString() => Node.Endpoint?.Describe() ?? Node.Label;
}
