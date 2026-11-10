// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Model;

public sealed record AdapterCapabilities(
    string Provider,
    bool TruePeek,
    bool PeekSideEffectFree,
    string SafeReadMethod,
    string SideEffectsToAvoid,
    string RequiredRole,
    bool WriteNeededToSetUp,
    string DocUrl,
    DateOnly DocsCheckedOn,
    string Verdict,
    bool AdapterInThisSkeleton);

public static class ProviderMatrix
{
    public static AdapterCapabilities ServiceBus { get; } = new(
        Provider: "Azure Service Bus",
        TruePeek: true,
        PeekSideEffectFree: true,
        SafeReadMethod: "Peek on a queue, subscription, or dead-letter subqueue. Pass fromSequenceNumber. Cap 100.",
        SideEffectsToAvoid: "Receive, complete, abandon, dead-letter, defer, lock renewal, deferred receive, schedule, and cancel. Accepting a session takes a lock and is outside observe mode.",
        RequiredRole: "Azure Service Bus Data Receiver",
        WriteNeededToSetUp: false,
        DocUrl: "https://learn.microsoft.com/azure/service-bus-messaging/message-browsing",
        DocsCheckedOn: new DateOnly(2026, 10, 20),
        Verdict: "Peek. Observe mode is enforced in code. Data Receiver can also settle, so the role is not a read-only guarantee.",
        AdapterInThisSkeleton: true);

    public static AdapterCapabilities EventHubs { get; } = new(
        Provider: "Azure Event Hubs",
        TruePeek: true,
        PeekSideEffectFree: true,
        SafeReadMethod: "Partition read on the shared group cg-message-explorer. Position stays in memory. Default start is the tail.",
        SideEffectsToAvoid: "Checkpoint writes, owner epochs, and reads on a consumer group that another worker uses.",
        RequiredRole: "Azure Event Hubs Data Receiver",
        WriteNeededToSetUp: false,
        DocUrl: "https://learn.microsoft.com/azure/event-hubs/event-hubs-features",
        DocsCheckedOn: new DateOnly(2026, 10, 20),
        Verdict: "Own consumer group on Standard or higher. Basic has only the default group, which is not a safe tap.",
        AdapterInThisSkeleton: true);

    public static AdapterCapabilities EventGrid { get; } = new(
        Provider: "Azure Event Grid",
        TruePeek: false,
        PeekSideEffectFree: false,
        SafeReadMethod: "No read API. The owner may apply a diagnostic subscription that copies events to an explorer-owned queue.",
        SideEffectsToAvoid: "Creating the subscription from observe mode. Replay is not a way to view events.",
        RequiredRole: "EventGrid EventSubscription Contributor on the topic, for the owner who applies the template.",
        WriteNeededToSetUp: true,
        DocUrl: "https://learn.microsoft.com/azure/event-grid/handler-service-bus",
        DocsCheckedOn: new DateOnly(2026, 10, 20),
        Verdict: "Tap setup is a write. Observe mode refuses. The admin module ships a template and does not create the subscription.",
        AdapterInThisSkeleton: true);

    public static AdapterCapabilities StorageQueues { get; } = new(
        Provider: "Azure Storage Queues",
        TruePeek: true,
        PeekSideEffectFree: true,
        SafeReadMethod: "Peek Messages, front 32 only. Also peek the poison queue.",
        SideEffectsToAvoid: "Get Messages. That hides the message and raises the dequeue count.",
        RequiredRole: "Storage Queue Data Reader",
        WriteNeededToSetUp: false,
        DocUrl: "https://learn.microsoft.com/rest/api/storageservices/peek-messages",
        DocsCheckedOn: new DateOnly(2026, 10, 20),
        Verdict: "Peek. Recorded here so the matrix is not missing the row. No adapter in this skeleton.",
        AdapterInThisSkeleton: false);

    public static IReadOnlyList<AdapterCapabilities> Rows { get; } =
    [
        ServiceBus,
        EventHubs,
        EventGrid,
        StorageQueues,
    ];
}
