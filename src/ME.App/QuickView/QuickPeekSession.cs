// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Shell.QuickView;

/// <summary>One page of a lane: the new rows, whether more may be waiting, and how many peek calls it took.</summary>
public sealed record QuickPage(IReadOnlyList<QuickMessage> Messages, bool HasMore, int PeekCalls);

/// <summary>
/// Peeks one entity, lane by lane. There is one sequence cursor per sub-queue (main, dead-letter, transfer
/// dead-letter); the Active and Scheduled lanes share the main cursor, and a peek of one keeps the other
/// lane's messages for later. A peek may return fewer messages than asked, so a page keeps peeking until it
/// has <c>count</c> rows, the sub-queue comes back empty, or <see cref="MaxCallsPerPage"/> calls are used.
/// Every frame is redacted before it is kept. Peek only: nothing here locks, settles or changes a message.
/// </summary>
public sealed class QuickPeekSession
{
    public const int MaxBatch = 100;
    public const int MaxCallsPerPage = 20;

    private readonly IMessageBrowser _browser;
    private readonly RedactionPipeline _redaction;
    private readonly Dictionary<PeekSource, Cursor> _cursors = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QuickPeekSession(IMessageBrowser browser, BrowseEntity entity, RedactionPipeline redaction)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        _redaction = redaction ?? throw new ArgumentNullException(nameof(redaction));
        foreach (var source in Enum.GetValues<PeekSource>())
            _cursors[source] = new Cursor();
    }

    public BrowseEntity Entity { get; }

    public static PeekSource SourceOf(MessageLane lane) => lane switch
    {
        MessageLane.DeadLetter => PeekSource.DeadLetter,
        MessageLane.TransferDeadLetter => PeekSource.TransferDeadLetter,
        _ => PeekSource.Main,
    };

    public bool CanPeek(MessageLane lane) => _browser.CanPeek(Entity, SourceOf(lane));

    /// <summary>The next sequence number the lane's sub-queue will be peeked from.</summary>
    public long NextSequence(MessageLane lane) => _cursors[SourceOf(lane)].Next;

    public bool HasMore(MessageLane lane)
    {
        var cursor = _cursors[SourceOf(lane)];
        return cursor.Waiting(lane).Count > 0 || !cursor.Exhausted;
    }

    /// <summary>Starts the lane's sub-queue again from the first message (both main lanes when it is the main entity).</summary>
    public void Reset(MessageLane lane) => _cursors[SourceOf(lane)] = new Cursor();

    public async Task<QuickPage> LoadAsync(MessageLane lane, int count, CancellationToken cancellationToken = default)
    {
        if (count is < 1 or > MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(count), $"A page is 1 to {MaxBatch} messages.");
        var source = SourceOf(lane);
        if (!_browser.CanPeek(Entity, source))
            return new QuickPage([], false, 0);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cursor = _cursors[source];
            var waiting = cursor.Waiting(lane);
            var calls = 0;
            while (waiting.Count < count && !cursor.Exhausted && calls < MaxCallsPerPage)
            {
                var ask = Math.Clamp(count - waiting.Count, 1, MaxBatch);
                var frames = await _browser.PeekAsync(Entity, source, cursor.Next, ask, cancellationToken).ConfigureAwait(false);
                calls++;
                if (frames.Count == 0)
                {
                    cursor.Exhausted = true;
                    break;
                }

                if (frames.Count > MaxBatch)
                    throw new InvalidOperationException("The broker returned more messages than a peek allows.");
                var highest = cursor.Next - 1;
                foreach (var frame in frames.OrderBy(f => f.SequenceNumber))
                {
                    if (frame.SequenceNumber < cursor.Next)
                        throw new InvalidOperationException("The broker returned a message below the cursor.");
                    highest = Math.Max(highest, frame.SequenceNumber);
                    var into = source switch
                    {
                        PeekSource.DeadLetter => MessageLane.DeadLetter,
                        PeekSource.TransferDeadLetter => MessageLane.TransferDeadLetter,
                        _ => frame.Lane == MessageLane.Scheduled ? MessageLane.Scheduled : MessageLane.Active,
                    };
                    cursor.Waiting(into).Enqueue(QuickMessage.From(frame, _redaction));
                }

                cursor.Next = highest + 1;
            }

            var page = new List<QuickMessage>(Math.Min(count, waiting.Count));
            while (page.Count < count && waiting.Count > 0)
                page.Add(waiting.Dequeue());
            return new QuickPage(page, waiting.Count > 0 || !cursor.Exhausted, calls);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class Cursor
    {
        private readonly Dictionary<MessageLane, Queue<QuickMessage>> _waiting = new();

        public long Next { get; set; }

        public bool Exhausted { get; set; }

        public Queue<QuickMessage> Waiting(MessageLane lane)
        {
            if (!_waiting.TryGetValue(lane, out var queue))
                _waiting[lane] = queue = new Queue<QuickMessage>();
            return queue;
        }
    }
}
