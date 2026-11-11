// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Operations.InMemory;
using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Shell.Demo;

/// <summary>
/// A peek-only browser over made-up messages held in memory, for the synthetic demo, screenshots and tests.
/// Bodies carry synthetic look-alike values (a made-up reference, amount and example.test address) so the
/// quick view's masking is visible. No network. It can return short batches and fail on cue.
/// </summary>
public sealed class SyntheticMessageBrowser : IMessageBrowser
{
    private static readonly string[] Subjects = ["order.created", "order.invoiced", "order.packed", "order.updated", "payment.captured", "notice.queued"];
    private static readonly string[] Reasons = ["MaxDeliveryCountExceeded", "SchemaValidationFailed", "ProcessingTimeout", "TTLExpiredException"];
    private static readonly string[] TransferReasons = ["TransferDestinationDisabled", "TransferQuotaExceeded"];

    private readonly Dictionary<(string Path, PeekSource Source), List<PeekedFrame>> _frames = new();

    public string ProviderName { get; init; } = "Synthetic demo";

    /// <summary>The most frames one peek returns (a real broker may return fewer than asked).</summary>
    public int MaxPerCall { get; set; } = int.MaxValue;

    /// <summary>Called before each peek with its 1-based number. Return an exception to fail it.</summary>
    public Func<int, Exception?>? Fault { get; set; }

    /// <summary>Holds every peek until set, to test loading states.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public List<(string Path, PeekSource Source, long From, int Max)> Calls { get; } = [];

    public bool Disposed { get; private set; }

    public bool CanPeek(BrowseEntity entity, PeekSource source) =>
        entity.Kind is EntityKind.Queue or EntityKind.Subscription;

    public SyntheticMessageBrowser Seed(BrowseEntity entity, PeekSource source, IEnumerable<PeekedFrame> frames)
    {
        var key = (entity.Path, source);
        if (!_frames.TryGetValue(key, out var list))
            _frames[key] = list = [];
        list.AddRange(frames);
        list.Sort((a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));
        return this;
    }

    /// <summary>Adds made-up messages: active (with a few deferred), scheduled, dead-lettered and transfer dead-lettered.</summary>
    public SyntheticMessageBrowser Generate(BrowseEntity entity, int active, int deadLetter = 0, int scheduled = 0, int transferDeadLetter = 0, long firstSequence = 1)
    {
        var at = new DateTimeOffset(2026, 11, 10, 19, 30, 0, TimeSpan.Zero);
        var main = new List<PeekedFrame>();
        var seq = firstSequence;
        // Scheduled messages sit among the active ones in sequence order, as on a real queue.
        var total = active + scheduled;
        var scheduledEvery = scheduled == 0 ? int.MaxValue : Math.Max(1, total / scheduled);
        var madeScheduled = 0;
        for (var i = 0; i < total; i++)
        {
            var isScheduled = madeScheduled < scheduled && (i % scheduledEvery == scheduledEvery - 1 || total - i <= scheduled - madeScheduled);
            if (isScheduled)
                madeScheduled++;
            main.Add(Frame(entity, seq++, isScheduled ? MessageLane.Scheduled : MessageLane.Active, at.AddSeconds(37 * i), i, deferred: !isScheduled && i % 23 == 22));
        }

        Seed(entity, PeekSource.Main, main);
        Seed(entity, PeekSource.DeadLetter, Enumerable.Range(0, deadLetter).Select(i => Frame(entity, firstSequence + 2 * i + 1, MessageLane.DeadLetter, at.AddSeconds(53 * i), i)));
        Seed(entity, PeekSource.TransferDeadLetter, Enumerable.Range(0, transferDeadLetter).Select(i => Frame(entity, firstSequence + 3 * i + 7, MessageLane.TransferDeadLetter, at.AddSeconds(71 * i), i)));
        return this;
    }

    /// <summary>The demo namespace: active and dead-letter messages from the in-memory broker, plus made-up scheduled and transfer dead-letter ones.</summary>
    public static SyntheticMessageBrowser ForDemo(InMemoryMoveBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        var browser = new SyntheticMessageBrowser();
        foreach (var row in SyntheticDemoProvider.Snapshot(broker).Where(r => r.Kind != EntityKind.Topic))
        {
            var entity = row.Kind == EntityKind.Queue ? BrowseEntity.Queue(row.Name) : BrowseEntity.Subscription(row.Parent!, row.Name);
            var endpoint = row.Kind == EntityKind.Queue ? MoveEndpoint.Queue(broker.Namespace, row.Name) : MoveEndpoint.Subscription(broker.Namespace, row.Parent!, row.Name);
            browser.Seed(entity, PeekSource.Main, broker.Messages(endpoint).Select((m, i) => FromStored(entity, m, MessageLane.Active, i)));
            browser.Seed(entity, PeekSource.DeadLetter, broker.Messages(endpoint with { DeadLetter = true }).Select((m, i) => FromStored(entity, m, MessageLane.DeadLetter, i)));
            var at = broker.Now;
            if (row.Counts.Scheduled is > 0 and var scheduled)
                browser.Seed(entity, PeekSource.Main, Enumerable.Range(0, (int)scheduled).Select(i => Frame(entity, 9_000 + i, MessageLane.Scheduled, at.AddMinutes(i), i)));
            if (row.Counts.TransferDeadLetter is > 0 and var transfer)
                browser.Seed(entity, PeekSource.TransferDeadLetter, Enumerable.Range(0, (int)transfer).Select(i => Frame(entity, 9_500 + i, MessageLane.TransferDeadLetter, at.AddMinutes(i), i)));
        }

        return browser;
    }

    public async Task<IReadOnlyList<PeekedFrame>> PeekAsync(BrowseEntity entity, PeekSource source, long fromSequenceNumber, int max, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Calls.Add((entity.Path, source, fromSequenceNumber, max));
        if (Gate is not null)
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (Fault?.Invoke(Calls.Count) is { } fault)
            throw fault;
        if (!_frames.TryGetValue((entity.Path, source), out var list))
            return [];
        return list.Where(f => f.SequenceNumber >= fromSequenceNumber).Take(Math.Min(max, MaxPerCall)).ToList();
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    private static PeekedFrame FromStored(BrowseEntity entity, StoredMessage stored, MessageLane lane, int index)
    {
        var frame = Frame(entity, stored.SequenceNumber, lane, stored.EnqueuedTime, index);
        var properties = stored.Message.ApplicationProperties.ToDictionary(p => p.Key, p => Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? string.Empty, StringComparer.Ordinal);
        return frame with
        {
            MessageId = stored.Message.MessageId,
            CorrelationId = stored.Message.CorrelationId,
            SessionId = stored.Message.SessionId,
            DeliveryCount = stored.DeliveryCount,
            DeadLetterReason = lane == MessageLane.DeadLetter ? stored.DeadLetterReason : null,
            ApplicationProperties = properties.Count == 0 ? frame.ApplicationProperties : properties,
        };
    }

    private static PeekedFrame Frame(BrowseEntity entity, long sequence, MessageLane lane, DateTimeOffset at, int index, bool deferred = false)
    {
        var hex = Fnv(entity.Path + "#" + sequence.ToString(CultureInfo.InvariantCulture)).ToString("x8", CultureInfo.InvariantCulture);
        var dead = lane is MessageLane.DeadLetter or MessageLane.TransferDeadLetter;
        var body = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ref"] = "REF-" + hex,
            ["amount"] = (12 + index * 7 % 400).ToString(CultureInfo.InvariantCulture) + ".50",
            ["currency"] = "USD",
            ["region"] = "r" + (index % 4 + 1).ToString(CultureInfo.InvariantCulture),
            ["contact"] = "person" + hex[..5] + "@example.test",
        };
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["schemaVersion"] = "2",
            ["source"] = "demo-producer",
            ["tenantRef"] = "TEN-" + hex[..6],
        };
        return new PeekedFrame(
            sequence,
            lane,
            "msg-" + hex,
            at,
            lane == MessageLane.Scheduled ? at.AddHours(2 + index % 5) : null,
            dead ? 10 : index % 3,
            Subjects[index % Subjects.Length],
            "application/json",
            "corr-" + hex,
            null,
            600 + (long)index * 97 % 2400,
            lane switch
            {
                MessageLane.DeadLetter => Reasons[index % Reasons.Length],
                MessageLane.TransferDeadLetter => TransferReasons[index % TransferReasons.Length],
                _ => null,
            },
            dead ? "handler gave up after 10 deliveries" : null,
            deferred,
            body,
            properties);
    }

    /// <summary>A stable hash, so the made-up values are the same on every run.</summary>
    private static uint Fnv(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
            hash = (hash ^ ch) * 16777619u;
        return hash;
    }
}
