// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Acornsoft.MessageExplorer.Model;

public enum SubQueue
{
    Main,
    DeadLetter,
    TransferDeadLetter,
}

public enum MessageState
{
    Active,
    Deferred,
    Scheduled,
}

public sealed record Position(string Kind, string Value);

public static class CloudEventsNames
{
    private static readonly Regex ExtensionName = new("^[a-z0-9]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void RequireExtensionName(string name)
    {
        if (string.IsNullOrEmpty(name) || !ExtensionName.IsMatch(name))
            throw new ArgumentException(
                $"CloudEvents extension name '{name}' must be lowercase letters and digits.",
                nameof(name));
    }

    public static string ToExtension(SubQueue subQueue) => subQueue switch
    {
        SubQueue.Main => "main",
        SubQueue.DeadLetter => "deadletter",
        SubQueue.TransferDeadLetter => "transferdeadletter",
        _ => throw new ArgumentOutOfRangeException(nameof(subQueue), subQueue, "Unknown sub-queue."),
    };

    public static string ToExtension(MessageState state) => state switch
    {
        MessageState.Active => "active",
        MessageState.Deferred => "deferred",
        MessageState.Scheduled => "scheduled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown message state."),
    };
}

public sealed class CloudEventEnvelope
{
    public CloudEventEnvelope(
        string id,
        string source,
        string type,
        DateTimeOffset time,
        string? subject,
        IReadOnlyDictionary<string, string> extensions,
        IReadOnlyDictionary<string, string> data)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("CloudEvents id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("CloudEvents source is required.", nameof(source));
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("CloudEvents type is required.", nameof(type));
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(data);

        foreach (var name in extensions.Keys)
            CloudEventsNames.RequireExtensionName(name);

        Id = id;
        Source = source;
        Type = type;
        Time = time;
        Subject = subject;
        Extensions = new Dictionary<string, string>(extensions, StringComparer.Ordinal);
        Data = new Dictionary<string, string>(data, StringComparer.Ordinal);
    }

    public string SpecVersion => "1.0";
    public string Id { get; }
    public string Source { get; }
    public string Type { get; }
    public DateTimeOffset Time { get; }
    public string? Subject { get; }
    public IReadOnlyDictionary<string, string> Extensions { get; }
    public IReadOnlyDictionary<string, string> Data { get; }

    public static CloudEventEnvelope Observed(
        string provider,
        string entity,
        Position position,
        SubQueue subQueue,
        MessageState state,
        int deliveryCount,
        DateTimeOffset? expiresAt,
        string? deadLetterReason,
        string? traceParent,
        string redactionProfile,
        DateTimeOffset time,
        string? subject,
        IReadOnlyDictionary<string, string> data)
    {
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["provider"] = provider,
            ["entity"] = entity,
            ["positionkind"] = position.Kind,
            ["positionvalue"] = position.Value,
            ["deliverycount"] = deliveryCount.ToString(CultureInfo.InvariantCulture),
            ["msgstate"] = CloudEventsNames.ToExtension(state),
            ["subqueue"] = CloudEventsNames.ToExtension(subQueue),
            ["redactionprofile"] = redactionProfile,
            ["envtag"] = "unspecified",
        };

        if (expiresAt is DateTimeOffset expires)
            extensions["expiresat"] = expires.ToString("o", CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(deadLetterReason))
            extensions["dlqreason"] = deadLetterReason;
        if (!string.IsNullOrEmpty(traceParent))
            extensions["traceparent"] = traceParent;

        var id = provider + "-" + position.Kind + "-" + position.Value;
        var source = provider + "://observe/" + entity;
        return new CloudEventEnvelope(
            id,
            source,
            "com.acornsoft.messageexplorer.observed.v1",
            time,
            subject,
            extensions,
            data);
    }
}

public sealed record ObservedMessage(
    CloudEventEnvelope Envelope,
    Position Position,
    SubQueue SubQueue,
    MessageState State,
    int DeliveryCount,
    DateTimeOffset? ExpiresAt,
    bool IsExpired,
    long EnqueuedSequenceNumber,
    DateTimeOffset? LockedUntil);
