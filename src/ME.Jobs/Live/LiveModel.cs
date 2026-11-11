// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;

namespace Acornsoft.MessageExplorer.Jobs.Live;

/// <summary>Good traffic, or a poison message a consumer should dead-letter (a zero-line order, a ticket with no item code).</summary>
public enum MessageKind
{
    Good,
    Poison,
}

/// <summary>
/// One generated message for a live send: where it goes, its correlation ID, its synthetic body, its kind and
/// the generator that made it. Generated once per run; a sink (or, later, several) is handed the same instance.
/// </summary>
public sealed record LiveMessage(string Entity, string CorrelationId, JsonObject Body, MessageKind Kind, string Generator)
{
    public static LiveMessage Create(string entity, string correlationId, JsonObject body, string generator) =>
        new(entity, correlationId, body, PoisonRule.Classify(body), generator);
}

/// <summary>How the sample generators mark poison: orders with no lines, crew tickets with an empty item code.</summary>
public static class PoisonRule
{
    public static MessageKind Classify(JsonObject body)
    {
        var poison = body["lines"] switch
        {
            JsonArray lines => lines.Count == 0,
            JsonValue value when value.TryGetValue<int>(out var count) => count == 0,
            _ => false,
        };
        if (body["itemCode"] is JsonValue item && item.TryGetValue<string>(out var code) && string.IsNullOrWhiteSpace(code))
            poison = true;
        return poison ? MessageKind.Poison : MessageKind.Good;
    }
}

/// <summary>
/// The limits on a live send. Dry run is the default; a real send must turn it off on purpose.
/// <see cref="HardCap"/> is fixed in code: no job file or flag can raise it.
/// </summary>
public sealed record LiveSendOptions
{
    /// <summary>The most messages one run may send, whatever the job says.</summary>
    public const int HardCap = 500;

    /// <summary>The fastest pace a job may ask for.</summary>
    public const int MaxPerMinute = 60;

    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromHours(1);

    public static readonly TimeSpan MaxTimeToLive = TimeSpan.FromDays(1);

    /// <summary>rate.perMinute: sends are spaced evenly by the wall clock.</summary>
    public int PerMinute { get; init; } = 10;

    /// <summary>maxMessages: over this, the run stops. 1 to <see cref="HardCap"/>.</summary>
    public int MaxMessages { get; init; } = HardCap;

    /// <summary>ttl: the time-to-live set on every message.</summary>
    public TimeSpan TimeToLive { get; init; } = DefaultTimeToLive;

    /// <summary>True unless the run was started to really send.</summary>
    public bool DryRun { get; init; } = true;

    /// <summary>When this file exists, the run stops before its next message.</summary>
    public string? StopFile { get; init; }

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (PerMinute is < 1 or > MaxPerMinute)
            problems.Add($"rate.perMinute: {PerMinute} must be 1 to {MaxPerMinute}");
        if (MaxMessages is < 1 or > HardCap)
            problems.Add($"maxMessages: {MaxMessages} must be 1 to {HardCap} (the hard cap)");
        if (TimeToLive <= TimeSpan.Zero || TimeToLive > MaxTimeToLive)
            problems.Add($"ttl: {TimeToLive} must be more than zero and at most one day");
        return problems;
    }
}

/// <summary>A live send was refused or stopped for a safety reason (allow-list, cap, entity).</summary>
public sealed class LiveSendException(string message) : Exception(message);

public enum LiveStopReason
{
    Completed,
    CapReached,
    Stopped,
    StopFile,

    /// <summary>The sink threw (for example the broker refused or the network failed); later messages were not sent.</summary>
    Failed,
}

/// <summary>What a live send did: planned, sent (zero in a dry run), why it stopped, and counts by entity and kind.</summary>
public sealed record LiveSendReport(
    int Planned,
    int Sent,
    bool DryRun,
    LiveStopReason StopReason,
    IReadOnlyDictionary<(string Entity, MessageKind Kind), int> Counts)
{
    /// <summary>Planned messages that did not go out (cap, stop switch). In a dry run nothing goes out.</summary>
    public int NotSent => DryRun ? 0 : Planned - Sent;

    /// <summary>Messages sent (or, in a dry run, that would be sent) to an entity, of one kind.</summary>
    public int Count(string entity, MessageKind kind) => Counts.GetValueOrDefault((entity, kind));
}

/// <summary>
/// Where generated messages go. The runner generates once and hands each message to one sink. A future
/// composite sink can wrap several (fan-out: one run feeding N sinks) without changing generation or pacing.
/// </summary>
public interface IMessageSink : IAsyncDisposable
{
    string Name { get; }

    Task SendAsync(LiveMessage message, TimeSpan timeToLive, CancellationToken cancellationToken);
}

/// <summary>The wall clock a live send is paced by. Tests use a fake that moves only when waited on.</summary>
public interface ILiveClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemLiveClock(TimeProvider? time = null) : ILiveClock
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public DateTimeOffset UtcNow => _time.GetUtcNow();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, _time, cancellationToken);
}
