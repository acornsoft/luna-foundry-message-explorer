// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Correlation;
using Acornsoft.MessageExplorer.Model;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus;

public sealed record ServiceBusPeekFrame(
    long EnqueuedSequenceNumber,
    MessageState State,
    int DeliveryCount,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? LockedUntil,
    string? Subject,
    string? DeadLetterReason,
    string? TraceParent,
    IReadOnlyDictionary<string, string> Body);

public interface IServiceBusPeekTransport
{
    Task<IReadOnlyList<ServiceBusPeekFrame>> PeekAsync(
        string entity,
        SubQueue subQueue,
        long fromSequenceNumber,
        int max,
        CancellationToken cancellationToken);
}

public sealed class ServiceBusObserveClient
{
    public const int MaxBatch = 100;

    private readonly IServiceBusPeekTransport _transport;
    private readonly RedactionPipeline _redaction;
    private readonly DateTimeOffset _observedAt;

    public ServiceBusObserveClient(
        IServiceBusPeekTransport transport,
        RedactionPipeline redaction,
        DateTimeOffset observedAt)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _redaction = redaction ?? throw new ArgumentNullException(nameof(redaction));
        _observedAt = observedAt;
    }

    public AdapterCapabilities Capabilities => ProviderMatrix.ServiceBus;

    public async Task<IReadOnlyList<ObservedMessage>> PeekAsync(
        string entity,
        SubQueue subQueue,
        long fromSequenceNumber,
        int max,
        CancellationToken cancellationToken = default)
    {
        ObserveMode.Allow(BrokerOperation.Peek);
        if (string.IsNullOrWhiteSpace(entity))
            throw new ArgumentException("Entity is required.", nameof(entity));
        if (fromSequenceNumber < 0)
            throw new ArgumentOutOfRangeException(nameof(fromSequenceNumber));
        if (max is < 1 or > MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(max), "Observe mode caps a peek at 100 messages.");

        var frames = await _transport.PeekAsync(entity, subQueue, fromSequenceNumber, max, cancellationToken).ConfigureAwait(false);
        if (frames.Count > max)
            throw new InvalidOperationException("Transport returned more frames than observe mode allows.");

        var result = new List<ObservedMessage>(frames.Count);
        foreach (var frame in frames)
        {
            if (frame.EnqueuedSequenceNumber < fromSequenceNumber)
                throw new InvalidOperationException("Transport returned a sequence below the cursor.");
            result.Add(Map(entity, subQueue, frame));
        }

        return result;
    }

    private ObservedMessage Map(string entity, SubQueue subQueue, ServiceBusPeekFrame frame)
    {
        var body = new Dictionary<string, string>(frame.Body, StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(frame.Subject))
            body["subject"] = frame.Subject;

        var redacted = _redaction.Redact(body);
        var expired = subQueue is SubQueue.DeadLetter or SubQueue.TransferDeadLetter
            ? false
            : frame.ExpiresAt is DateTimeOffset expires && expires < _observedAt;

        string? trace = null;
        if (TraceParent.TryParse(frame.TraceParent, out var parsed) && parsed is not null)
            trace = parsed.ToHeader();

        var position = new Position(
            "sequence",
            frame.EnqueuedSequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var envelope = CloudEventEnvelope.Observed(
            "servicebus",
            entity,
            position,
            subQueue,
            frame.State,
            frame.DeliveryCount,
            frame.ExpiresAt,
            frame.DeadLetterReason,
            trace,
            _redaction.ProfileId,
            _observedAt,
            redacted.Fields.TryGetValue("subject", out var subject) ? subject : null,
            redacted.Fields);

        return new ObservedMessage(
            envelope,
            position,
            subQueue,
            frame.State,
            frame.DeliveryCount,
            frame.ExpiresAt,
            expired,
            frame.EnqueuedSequenceNumber,
            frame.LockedUntil);
    }
}
