// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// How a queue, topic or subscription looks, the same in the tree and the entity header: a Font Awesome icon, a
/// short tag, and a colour family. Queues are channels (an inbox, teal); topics broadcast (a tower, purple) and
/// their subscriptions branch off them (purple too, so they read as part of the topic).
/// </summary>
public static class EntityKindStyle
{
    public const string QueueIcon = "fa-solid fa-inbox";
    public const string TopicIcon = "fa-solid fa-tower-broadcast";
    public const string SubscriptionIcon = "fa-solid fa-code-branch";

    public static string Icon(EntityKind kind) => kind switch
    {
        EntityKind.Queue => QueueIcon,
        EntityKind.Topic => TopicIcon,
        _ => SubscriptionIcon,
    };

    public static string Tag(EntityKind kind) => kind switch
    {
        EntityKind.Queue => "QUEUE",
        EntityKind.Topic => "TOPIC",
        _ => "SUB",
    };

    /// <summary>True for queues: the channel colour. Topics and subscriptions use the topic colour.</summary>
    public static bool IsQueueFamily(EntityKind kind) => kind == EntityKind.Queue;
}
