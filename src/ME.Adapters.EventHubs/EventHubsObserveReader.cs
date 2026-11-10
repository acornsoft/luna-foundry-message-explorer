// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Acornsoft.MessageExplorer.Correlation;
using Acornsoft.MessageExplorer.Model;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Adapters.EventHubs;

public enum EventHubsStartKind
{
    Latest,
    Earliest,
    AtTimestamp,
}

public sealed record EventHubsStart(EventHubsStartKind Kind, DateTimeOffset? At)
{
    public static EventHubsStart Latest { get; } = new(EventHubsStartKind.Latest, null);
    public static EventHubsStart Earliest { get; } = new(EventHubsStartKind.Earliest, null);

    public static EventHubsStart AtTimestamp(DateTimeOffset timestamp) => new(EventHubsStartKind.AtTimestamp, timestamp);
}

public sealed record EventHubsObserveOptions(
    string ConsumerGroup,
    int? OwnerLevel,
    bool UsesCheckpointStore,
    EventHubsStart Start)
{
    public static EventHubsObserveOptions Default { get; } = new(
        EventHubsObserveReader.ConsumerGroup,
        OwnerLevel: null,
        UsesCheckpointStore: false,
        EventHubsStart.Latest);
}

public sealed record EventHubFrame(
    string Offset,
    DateTimeOffset EnqueuedAt,
    string? Subject,
    string? TraceParent,
    IReadOnlyDictionary<string, string> Body);

public interface IEventHubPartitionLog
{
    string TailOffset(string partitionId);

    IReadOnlyList<EventHubFrame> ReadForward(string partitionId, string fromOffsetExclusive, int max);
}

public sealed class EventHubsObserveReader
{
    public const string ConsumerGroup = "cg-message-explorer";
    public const int MaxBatch = 100;

    private readonly EventHubsObserveOptions _options;
    private readonly IEventHubPartitionLog _log;
    private readonly RedactionPipeline _redaction;
    private readonly DateTimeOffset _observedAt;
    private readonly Dictionary<string, string> _positions = new(StringComparer.Ordinal);

    public EventHubsObserveReader(
        EventHubsObserveOptions options,
        IEventHubPartitionLog log,
        RedactionPipeline redaction,
        DateTimeOffset observedAt)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _redaction = redaction ?? throw new ArgumentNullException(nameof(redaction));
        _observedAt = observedAt;

        if (!string.Equals(options.ConsumerGroup, ConsumerGroup, StringComparison.Ordinal))
            throw new ReadRefusedException("Observe mode reads only the shared cg-message-explorer group.");
        if (options.OwnerLevel is not null)
            throw new ObserveModeViolationException(BrokerOperation.EpochAcquire);
        if (options.UsesCheckpointStore)
            throw new ObserveModeViolationException(BrokerOperation.Checkpoint);
        if (options.Start.Kind == EventHubsStartKind.AtTimestamp && options.Start.At is null)
            throw new ArgumentException("A look-back window needs a timestamp.", nameof(options));
    }

    public AdapterCapabilities Capabilities => ProviderMatrix.EventHubs;

    public IReadOnlyDictionary<string, string> Positions => _positions;

    public Task<IReadOnlyList<ObservedMessage>> ReadAsync(
        string partitionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObserveMode.Allow(BrokerOperation.LogRead);
        if (string.IsNullOrWhiteSpace(partitionId))
            throw new ArgumentException("Partition id is required.", nameof(partitionId));

        if (!_positions.TryGetValue(partitionId, out var cursor))
        {
            if (_options.Start.Kind == EventHubsStartKind.Latest)
            {
                _positions[partitionId] = _log.TailOffset(partitionId);
                return Task.FromResult<IReadOnlyList<ObservedMessage>>([]);
            }

            cursor = "";
        }

        var frames = _log.ReadForward(partitionId, cursor, MaxBatch);
        if (frames.Count > MaxBatch)
            throw new InvalidOperationException("Log returned more than the observe-mode cap.");

        if (frames.Count > 0)
            _positions[partitionId] = frames[^1].Offset;

        IEnumerable<EventHubFrame> selected = frames;
        if (_options.Start.Kind == EventHubsStartKind.AtTimestamp && _options.Start.At is DateTimeOffset at)
            selected = frames.Where(frame => frame.EnqueuedAt >= at);

        var mapped = selected.Select(frame => Map(partitionId, frame)).ToList();
        return Task.FromResult<IReadOnlyList<ObservedMessage>>(mapped);
    }

    private ObservedMessage Map(string partitionId, EventHubFrame frame)
    {
        var body = new Dictionary<string, string>(frame.Body, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(frame.Subject))
            body["subject"] = frame.Subject;

        var redacted = _redaction.Redact(body);
        string? trace = null;
        if (TraceParent.TryParse(frame.TraceParent, out var parsed) && parsed is not null)
            trace = parsed.ToHeader();

        var position = new Position("offset", frame.Offset);
        var envelope = CloudEventEnvelope.Observed(
            "eventhubs",
            partitionId,
            position,
            SubQueue.Main,
            MessageState.Active,
            deliveryCount: 0,
            expiresAt: null,
            deadLetterReason: null,
            trace,
            _redaction.ProfileId,
            _observedAt,
            redacted.Fields.TryGetValue("subject", out var subject) ? subject : null,
            redacted.Fields);

        var sequence = long.TryParse(frame.Offset, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedOffset)
            ? parsedOffset
            : 0L;

        return new ObservedMessage(
            envelope,
            position,
            SubQueue.Main,
            MessageState.Active,
            DeliveryCount: 0,
            ExpiresAt: null,
            IsExpired: false,
            sequence,
            LockedUntil: null);
    }
}
