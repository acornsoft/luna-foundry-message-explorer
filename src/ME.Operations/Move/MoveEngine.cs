// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Model;

namespace Acornsoft.MessageExplorer.Operations.Move;

/// <summary>A drop: these messages, from here, to there. <paramref name="Actor"/> is who asked, for the audit log.</summary>
public sealed record MoveRequest(MoveEndpoint Source, MoveEndpoint Target, MoveSelection Selection, string? Actor = null);

/// <summary>
/// A checked move, ready for the confirm dialog. Nothing is locked yet. <see cref="ExecuteAsync"/> refuses
/// to run it until <see cref="Confirm"/> is called, which the app does only when the user clicks Move.
/// </summary>
public sealed class MovePlan
{
    internal MovePlan(MoveRequest request, MoveRouteKind route, bool atomic, bool sessions, IReadOnlyList<string> warnings, IMoveTransport source, IMoveTransport target)
    {
        Request = request;
        Route = route;
        Atomic = atomic;
        Sessions = sessions;
        Warnings = warnings;
        SourceTransport = source;
        TargetTransport = target;
    }

    public MoveRequest Request { get; }

    public MoveRouteKind Route { get; }

    /// <summary>True when each message's send and complete run in one transaction (same namespace).</summary>
    public bool Atomic { get; }

    /// <summary>True when the source is read session by session.</summary>
    public bool Sessions { get; }

    public IReadOnlyList<string> Warnings { get; }

    public int Count => Request.Selection.Limit;

    public bool IsConfirmed { get; private set; }

    internal IMoveTransport SourceTransport { get; }

    internal IMoveTransport TargetTransport { get; }

    public string Title => $"Move {Count} message{(Count == 1 ? string.Empty : "s")}?";

    public string FromText => $"From: {Request.Source.Describe()} in {Request.Source.Namespace}";

    public string ToText => $"To: {Request.Target.Describe()} in {Request.Target.Namespace}";

    public string HowText => Atomic
        ? "How: one transaction per message. The copy is sent and the original removed together, or neither happens."
        : "How: at-least-once. The namespaces differ, so each copy is sent first and the original removed after the send succeeds.";

    public string OrderText => Sessions
        ? "Order: oldest first, one session at a time. Each message keeps its ID, session and correlation."
        : "Order: oldest first by sequence number. Each message keeps its ID, session and correlation.";

    public string ModeName => Atomic ? "transaction" : "at-least-once";

    public void Confirm() => IsConfirmed = true;
}

public enum MoveOutcome
{
    /// <summary>Every requested message moved.</summary>
    Completed,

    /// <summary>The source ran out, or some picked messages were gone, before the count was reached.</summary>
    Partial,

    /// <summary>A step failed. The move stopped there and every message not yet moved was released.</summary>
    Failed,

    /// <summary>The user cancelled. The message in flight finished; the rest were released.</summary>
    Cancelled,
}

public sealed record MoveResult(
    string MoveId,
    MoveOutcome Outcome,
    int Requested,
    int Moved,
    int Released,
    string Message,
    string? PossibleDuplicateMessageId = null);

public sealed record MoveProgress(int Moved, int Requested, string? SessionId);

/// <summary>
/// Moves messages one at a time, oldest first: lock, clone, send, complete. Within one namespace the send
/// and complete share a transaction; across namespaces the move is at-least-once. On any failure it stops,
/// releases every message it still holds, and reports how far it got. Cancel takes effect between messages,
/// so a message is never left half-moved by a cancel.
/// </summary>
public sealed class MoveEngine(MoveModeSwitch moveMode, IMoveAuditLog audit, IClock? clock = null)
{
    /// <summary>How long one receive waits for messages before the engine decides the source is empty.</summary>
    public TimeSpan ReceiveWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How many messages a picked move may look through (and hold) to find the picks.</summary>
    public int ScanLimit { get; init; } = 1000;

    private readonly IClock _clock = clock ?? new SystemClock();

    public async Task<MovePlan> PrepareAsync(MoveRequest request, IMoveTransport source, IMoveTransport? target = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(source);
        target ??= source;
        moveMode.EnsureOn();

        var route = MoveRoutes.Classify(request.Source, request.Target);
        var limit = request.Selection.Limit;
        if (limit < 1)
            throw new MoveRefusedException("Pick at least one message to move.");
        if (limit > MoveSelection.MaxBatch)
            throw new MoveRefusedException($"One drop moves at most {MoveSelection.MaxBatch} messages. Pick fewer and drop again.");
        if (!Same(request.Source.Namespace, source.Namespace) || !Same(request.Target.Namespace, target.Namespace))
            throw new MoveRefusedException("The source or target isn't in the namespace this connection opened.");

        foreach (var transport in new[] { source, target }.Distinct())
        {
            if (!transport.Capabilities.HasFlag(MoveCapabilities.Move))
                throw new MoveRefusedException($"{transport.ProviderName} can't move messages.");
        }

        if (request.Source.DeadLetter && !source.Capabilities.HasFlag(MoveCapabilities.DeadLetter))
            throw new MoveRefusedException($"{source.ProviderName} has no dead-letter queues to move from.");
        if (request.Target.Kind == MoveEntityKind.Topic && !target.Capabilities.HasFlag(MoveCapabilities.Topics))
            throw new MoveRefusedException($"{target.ProviderName} has no topics to move to.");

        var receive = await source.CheckReceiveAsync(request.Source, cancellationToken).ConfigureAwait(false);
        var send = await target.CheckSendAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (!receive.Allowed || !send.Allowed)
        {
            var problems = new[] { receive.Problem, send.Problem }.Where(p => p is not null);
            throw new MoveRefusedException("Move mode needs both receive rights on the source and send rights on the target. " + string.Join(" ", problems));
        }

        var atomic = ReferenceEquals(source, target) || Same(source.Namespace, target.Namespace);
        atomic = atomic && source.Capabilities.HasFlag(MoveCapabilities.Transactions);
        var sessions = !request.Source.DeadLetter
            && source.Capabilities.HasFlag(MoveCapabilities.Sessions)
            && await source.RequiresSessionsAsync(request.Source, cancellationToken).ConfigureAwait(false);

        var warnings = new List<string>();
        if (!atomic)
        {
            var dedup = await target.DuplicateDetectionAsync(request.Target, cancellationToken).ConfigureAwait(false);
            warnings.Add(dedup switch
            {
                true => "Duplicate detection is on for the target, so if a removal fails after a send, the repeat is dropped within its detection window.",
                false => "Duplicate detection is off for the target. If a removal fails after a send, the target keeps the copy and the source keeps the original, so check for a duplicate.",
                null => "Couldn't read whether the target has duplicate detection. If a removal fails after a send, the target may get a duplicate.",
            });
        }

        if (atomic && route is MoveRouteKind.DeadLetterToParent or MoveRouteKind.SubscriptionDeadLetterToTopic
            && await target.DuplicateDetectionAsync(request.Target, cancellationToken).ConfigureAwait(false) == true)
        {
            warnings.Add("The target has duplicate detection. A message whose original arrived inside the detection window is dropped by the broker as a repeat.");
        }

        if (request.Target.Kind == MoveEntityKind.Topic)
            warnings.Add($"Every subscription of topic {request.Target.Name} gets a copy, not only {request.Source.Name}.");

        if (request.Selection is MoveSelection.Oldest oldest)
        {
            var waiting = await source.PeekAsync(request.Source, oldest.Count, cancellationToken).ConfigureAwait(false);
            if (waiting.Count == 0)
                throw new MoveRefusedException($"The {request.Source.Describe()} has no messages to move.");
            if (waiting.Count < oldest.Count)
                request = request with { Selection = MoveSelection.OldestFirst(waiting.Count) };
        }

        if (request.Selection is MoveSelection.Picked picked)
        {
            var ahead = await source.PeekAsync(request.Source, MoveSelection.MaxBatch, cancellationToken).ConfigureAwait(false);
            var first = ahead.OrderBy(m => m.SequenceNumber).Take(picked.SequenceNumbers.Count).Select(m => m.SequenceNumber);
            if (!first.SequenceEqual(picked.SequenceNumbers.Order()))
                warnings.Add("Messages ahead of your picks are locked while the move runs and released after it. Each released message's delivery count goes up by one.");
        }

        return new MovePlan(request, route, atomic, sessions, warnings, source, target);
    }

    public async Task<MoveResult> ExecuteAsync(MovePlan plan, IProgress<MoveProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.IsConfirmed)
            throw new MoveRefusedException("This move wasn't confirmed. Nothing was moved.");
        moveMode.EnsureOn();

        var run = new Run(plan, progress, Guid.NewGuid().ToString("N"));
        var request = plan.Request;
        audit.Write(new MoveAuditEntry("move-started", _clock.UtcNow, run.MoveId, request.Actor, request.Source.MovedFromValue,
            request.Target.MovedFromValue, plan.Route.ToString(), plan.ModeName, plan.Count));

        MoveOutcome outcome;
        string message;
        try
        {
            await using var sender = plan.TargetTransport.OpenSender(request.Target);
            run.Sender = sender;
            var sessions = plan.Sessions;
            try
            {
                await ReadAllAsync(run, sessions, cancellationToken).ConfigureAwait(false);
            }
            catch (MoveSessionModeException mismatch) when (run.Moved == 0)
            {
                await ReadAllAsync(run, mismatch.RequiresSessions, cancellationToken).ConfigureAwait(false);
            }

            if (run.Moved == plan.Count)
            {
                outcome = MoveOutcome.Completed;
                message = $"Moved {run.Moved} of {plan.Count}.";
            }
            else
            {
                outcome = MoveOutcome.Partial;
                message = request.Selection is MoveSelection.Picked
                    ? $"Moved {run.Moved} of {plan.Count}. The others weren't found; they may have moved, expired or been received by someone else."
                    : $"Moved {run.Moved} of {plan.Count}. The source had no more messages.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = MoveOutcome.Cancelled;
            message = $"Cancelled after {run.Moved} of {plan.Count}. No message was left half-moved; the rest stay in the source.";
        }
        catch (Exception exception)
        {
            outcome = MoveOutcome.Failed;
            message = $"Stopped after {run.Moved} of {plan.Count}: {Describe(exception)} The rest stay in the source.";
            if (run.PossibleDuplicate is { } duplicate)
                message += $" Message {duplicate} was sent but not removed from the source, so the target may have a duplicate.";
        }

        audit.Write(new MoveAuditEntry("move-finished", _clock.UtcNow, run.MoveId, request.Actor, Outcome: outcome.ToString(),
            Moved: run.Moved, Released: run.Released, PossibleDuplicateMessageId: run.PossibleDuplicate,
            Reason: outcome is MoveOutcome.Failed or MoveOutcome.Partial or MoveOutcome.Cancelled ? message : null));
        return new MoveResult(run.MoveId, outcome, plan.Count, run.Moved, run.Released, message, run.PossibleDuplicate);
    }

    private async Task ReadAllAsync(Run run, bool sessions, CancellationToken cancellationToken)
    {
        var source = run.Plan.SourceTransport;
        var endpoint = run.Plan.Request.Source;
        if (!sessions)
        {
            await using var receiver = await source.OpenReceiverAsync(endpoint, cancellationToken).ConfigureAwait(false);
            await DrainAsync(run, receiver, cancellationToken).ConfigureAwait(false);
            return;
        }

        while (!run.Done)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var receiver = await source.AcceptNextSessionAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (receiver is null)
                return;
            await using (receiver.ConfigureAwait(false))
            {
                await DrainAsync(run, receiver, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Moves messages from one receiver (one session, or the whole source) until done or empty.</summary>
    private async Task DrainAsync(Run run, IMoveReceiver receiver, CancellationToken cancellationToken)
    {
        var pending = new List<LockedMessage>();
        var skipped = new List<LockedMessage>();
        try
        {
            while (!run.Done)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var want = run.Plan.Request.Selection is MoveSelection.Oldest ? run.Remaining : MoveSelection.MaxBatch;
                if (run.Scanned >= ScanLimit)
                    return;
                var batch = await receiver.ReceiveAsync(Math.Min(want, MoveSelection.MaxBatch), ReceiveWait, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                    return;
                pending.AddRange(batch.OrderBy(m => m.SequenceNumber));
                run.Scanned += batch.Count;

                while (pending.Count > 0)
                {
                    var message = pending[0];
                    if (run.Done || !run.Wants(message))
                    {
                        skipped.Add(message);
                        pending.RemoveAt(0);
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await MoveOneAsync(run, receiver, message).ConfigureAwait(false);
                    pending.RemoveAt(0);
                }
            }
        }
        finally
        {
            foreach (var held in pending.Concat(skipped))
            {
                try
                {
                    await receiver.AbandonAsync(held, CancellationToken.None).ConfigureAwait(false);
                    run.Released++;
                }
                catch (Exception)
                {
                    // The lock runs out on its own; the message becomes visible again.
                    run.Released++;
                }
            }
        }
    }

    private async Task MoveOneAsync(Run run, IMoveReceiver receiver, LockedMessage message)
    {
        var plan = run.Plan;
        var copy = MessageCloner.Clone(message, plan.Request.Source);

        // Neither step takes the cancel token: once a message starts moving, it finishes.
        if (plan.Atomic)
        {
            await plan.SourceTransport.RunInTransactionAsync(async () =>
            {
                await run.Sender!.SendAsync(copy, CancellationToken.None).ConfigureAwait(false);
                await receiver.CompleteAsync(message, CancellationToken.None).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        else
        {
            await run.Sender!.SendAsync(copy, CancellationToken.None).ConfigureAwait(false);
            run.PossibleDuplicate = message.MessageId ?? $"seq {message.SequenceNumber}";
            await receiver.CompleteAsync(message, CancellationToken.None).ConfigureAwait(false);
            run.PossibleDuplicate = null;
        }

        run.Moved++;
        run.Found(message);
        audit.Write(new MoveAuditEntry("message-moved", _clock.UtcNow, run.MoveId, plan.Request.Actor,
            SequenceNumber: message.SequenceNumber, MessageId: message.MessageId, SessionId: message.SessionId));
        run.Progress?.Report(new MoveProgress(run.Moved, plan.Count, receiver.SessionId));
    }

    /// <summary>Plain words for a failure. Transport messages are written to be safe; anything else shows only its type.</summary>
    public static string Describe(Exception exception) => exception switch
    {
        MoveRefusedException refused => refused.Message,
        MoveTransportException transport => transport.Message,
        UnauthorizedAccessException => "The broker said this login isn't allowed to do that.",
        TimeoutException => "The broker didn't answer in time.",
        _ => $"The broker returned an error ({exception.GetType().Name}).",
    };

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private sealed class Run(MovePlan plan, IProgress<MoveProgress>? progress, string moveId)
    {
        private readonly HashSet<long>? _picked = plan.Request.Selection is MoveSelection.Picked picked ? [.. picked.SequenceNumbers] : null;

        public MovePlan Plan { get; } = plan;

        public IProgress<MoveProgress>? Progress { get; } = progress;

        public string MoveId { get; } = moveId;

        public IMoveSender? Sender { get; set; }

        public int Moved { get; set; }

        public int Released { get; set; }

        public int Scanned { get; set; }

        public string? PossibleDuplicate { get; set; }

        public int Remaining => Plan.Count - Moved;

        public bool Done => Moved >= Plan.Count || (_picked is not null && _picked.Count == 0);

        public bool Wants(LockedMessage message) => _picked is null || _picked.Contains(message.SequenceNumber);

        public void Found(LockedMessage message) => _picked?.Remove(message.SequenceNumber);
    }
}
