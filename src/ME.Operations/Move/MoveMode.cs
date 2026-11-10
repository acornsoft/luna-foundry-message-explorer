// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Operations.Move;

/// <summary>
/// The Move mode switch. It is off whenever the app starts: there is no setting, file or argument that turns
/// it on, only <see cref="TurnOn"/> from the toggle. Every move checks it first.
/// </summary>
public sealed class MoveModeSwitch
{
    public const string Banner = "Move mode is on. Dropping messages moves them. Every move asks first and is written to the audit log.";

    public bool IsOn { get; private set; }

    public event EventHandler? Changed;

    public void TurnOn() => Set(true);

    public void TurnOff() => Set(false);

    public void EnsureOn()
    {
        if (!IsOn)
            throw new MoveRefusedException("Move mode is off. Turn it on to move messages; the app stays read-only until you do.");
    }

    private void Set(bool value)
    {
        if (IsOn == value)
            return;
        IsOn = value;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public enum MoveRouteKind
{
    DeadLetterToParent,
    QueueToQueue,
    SubscriptionToQueue,
    SubscriptionDeadLetterToTopic,
}

/// <summary>The four moves Move mode supports. Anything else is refused with a plain reason.</summary>
public static class MoveRoutes
{
    public const string Supported =
        "Move mode supports: a queue's dead-letter queue back to that queue, a queue to another queue, a subscription to a queue, and a subscription's dead-letter queue back to its topic.";

    public static MoveRouteKind Classify(MoveEndpoint source, MoveEndpoint target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (target.DeadLetter)
            throw new MoveRefusedException("A dead-letter queue can't be a target. Messages land there only when the broker dead-letters them.");
        if (source.Kind == MoveEntityKind.Topic)
            throw new MoveRefusedException("A topic doesn't hold messages; its subscriptions do. Pick a subscription as the source.");

        var sameNamespace = string.Equals(source.Namespace, target.Namespace, StringComparison.OrdinalIgnoreCase);
        switch (source.Kind)
        {
            case MoveEntityKind.Queue when source.DeadLetter:
                if (target.Kind == MoveEntityKind.Queue && sameNamespace && string.Equals(target.Name, source.Name, StringComparison.Ordinal))
                    return MoveRouteKind.DeadLetterToParent;
                throw new MoveRefusedException($"Dead-lettered messages go back to their own queue ({source.Name}). " + Supported);

            case MoveEntityKind.Queue:
                if (target.Kind == MoveEntityKind.Queue)
                {
                    if (sameNamespace && string.Equals(target.Name, source.Name, StringComparison.Ordinal))
                        throw new MoveRefusedException("The source and target are the same queue.");
                    return MoveRouteKind.QueueToQueue;
                }

                throw new MoveRefusedException(Supported);

            case MoveEntityKind.Subscription when source.DeadLetter:
                if (target.Kind == MoveEntityKind.Topic && sameNamespace && string.Equals(target.Name, source.Topic, StringComparison.Ordinal))
                    return MoveRouteKind.SubscriptionDeadLetterToTopic;
                throw new MoveRefusedException($"A subscription's dead-lettered messages go back to its own topic ({source.Topic}). " + Supported);

            case MoveEntityKind.Subscription:
                if (target.Kind == MoveEntityKind.Queue)
                    return MoveRouteKind.SubscriptionToQueue;
                throw new MoveRefusedException(Supported);
        }

        throw new MoveRefusedException(Supported);
    }

    /// <summary>True when <see cref="Classify"/> would accept the pair. For building target lists.</summary>
    public static bool IsAllowed(MoveEndpoint source, MoveEndpoint target)
    {
        try
        {
            Classify(source, target);
            return true;
        }
        catch (MoveRefusedException)
        {
            return false;
        }
    }
}
