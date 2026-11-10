// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Shell.QuickView;

/// <summary>A name and a value shown in the detail drawer. The value is already redacted.</summary>
public sealed record QuickField(string Name, string Value);

/// <summary>
/// One peeked message after redaction. This is the only message type the quick view shows: every string on it
/// has been through the redaction pipeline, and body and application property values are masked.
/// </summary>
public sealed class QuickMessage
{
    public const string Masked = "••••••";
    public const string None = "—";

    private QuickMessage(
        long sequenceNumber,
        MessageLane lane,
        bool deferred,
        string messageId,
        string subject,
        string enqueuedText,
        string enqueuedFull,
        string? scheduledText,
        string sizeText,
        int deliveryCount,
        string reason,
        string? description,
        string bodyPreview,
        IReadOnlyList<QuickField> system,
        IReadOnlyList<QuickField> properties,
        IReadOnlyList<string> bodyFields,
        string bodyText)
    {
        SequenceNumber = sequenceNumber;
        Lane = lane;
        Deferred = deferred;
        MessageId = messageId;
        Subject = subject;
        EnqueuedText = enqueuedText;
        EnqueuedFull = enqueuedFull;
        ScheduledText = scheduledText;
        SizeText = sizeText;
        DeliveryCount = deliveryCount;
        Reason = reason;
        Description = description;
        BodyPreview = bodyPreview;
        SystemProperties = system;
        ApplicationProperties = properties;
        BodyFields = bodyFields;
        BodyText = bodyText;
    }

    public long SequenceNumber { get; }

    public string SequenceText => SequenceNumber.ToString(CultureInfo.InvariantCulture);

    public MessageLane Lane { get; }

    public bool Deferred { get; }

    public string MessageId { get; }

    public string Subject { get; }

    /// <summary>Enqueued time of day in UTC, for the row.</summary>
    public string EnqueuedText { get; }

    /// <summary>Enqueued date and time in UTC, for the drawer.</summary>
    public string EnqueuedFull { get; }

    /// <summary>When a scheduled message is due (UTC), or null.</summary>
    public string? ScheduledText { get; }

    public string SizeText { get; }

    public int DeliveryCount { get; }

    /// <summary>The dead-letter reason (scrubbed), the due time for a scheduled message, "deferred", or a dash.</summary>
    public string Reason { get; }

    public bool HasReason => Lane is MessageLane.DeadLetter or MessageLane.TransferDeadLetter && Reason != None;

    public string? Description { get; }

    /// <summary>One line for the row: which body fields there are, all masked.</summary>
    public string BodyPreview { get; }

    public IReadOnlyList<QuickField> SystemProperties { get; }

    /// <summary>Application property names with masked values.</summary>
    public IReadOnlyList<QuickField> ApplicationProperties { get; }

    public bool HasApplicationProperties => ApplicationProperties.Count > 0;

    /// <summary>Body field names. Their values are dropped by the profile.</summary>
    public IReadOnlyList<string> BodyFields { get; }

    /// <summary>The masked body for the drawer: field names with masked values, never the values.</summary>
    public string BodyText { get; }

    /// <summary>What the copy icon puts on the clipboard: the redacted row and drawer text only.</summary>
    public string CopyText
    {
        get
        {
            var builder = new StringBuilder();
            foreach (var item in SystemProperties)
                builder.Append(item.Name).Append(": ").Append(item.Value).Append('\n');
            foreach (var item in ApplicationProperties)
                builder.Append(item.Name).Append(": ").Append(item.Value).Append('\n');
            builder.Append("Body (masked):\n").Append(BodyText);
            return builder.ToString();
        }
    }

    /// <summary>Every string this message can put on screen or on the clipboard (the redaction tests read these).</summary>
    public IEnumerable<string> Surfaces()
    {
        yield return MessageId;
        yield return Subject;
        yield return Reason;
        if (Description is not null)
            yield return Description;
        yield return BodyPreview;
        yield return BodyText;
        yield return CopyText;
        foreach (var item in SystemProperties.Concat(ApplicationProperties))
        {
            yield return item.Name;
            yield return item.Value;
        }
    }

    /// <summary>Redacts a frame. Nothing from the frame reaches the result except through <paramref name="redaction"/>.</summary>
    public static QuickMessage From(PeekedFrame frame, RedactionPipeline redaction)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(redaction);
        var input = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                input[key] = value;
        }

        Put("subject", frame.Subject);
        Put("dlq_reason", frame.DeadLetterReason);
        Put("dlq_description", frame.DeadLetterDescription);
        Put("message_id", frame.MessageId);
        Put("correlation_id", frame.CorrelationId);
        Put("session_id", frame.SessionId);
        Put("content_type", frame.ContentType);
        foreach (var pair in frame.Body)
            input[QuickViewRedactionProfile.BodyPrefix + pair.Key] = pair.Value ?? string.Empty;
        foreach (var pair in frame.ApplicationProperties)
            input[QuickViewRedactionProfile.PropertyPrefix + pair.Key] = pair.Value ?? string.Empty;

        var result = redaction.Redact(input);
        string Kept(string key) => result.Fields.TryGetValue(key, out var value) && value.Length > 0 ? value : None;

        var bodyNames = frame.Body.Keys.Select(SafeName).Order(StringComparer.Ordinal).ToList();
        var propertyNames = frame.ApplicationProperties.Keys.Select(SafeName).Order(StringComparer.Ordinal).ToList();
        var enqueued = frame.EnqueuedAt?.ToUniversalTime();
        var scheduled = frame.ScheduledFor?.ToUniversalTime();
        var reason = frame.Lane switch
        {
            MessageLane.DeadLetter or MessageLane.TransferDeadLetter => Kept("dlq_reason"),
            MessageLane.Scheduled when scheduled is { } due => "due " + due.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            _ when frame.Deferred => "deferred",
            _ => None,
        };

        var system = new List<QuickField>
        {
            new("SequenceNumber", frame.SequenceNumber.ToString(CultureInfo.InvariantCulture)),
            new("MessageId", Kept("message_id")),
            new("EnqueuedTime", enqueued?.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? None),
            new("Subject", Kept("subject")),
            new("Size", Size(frame.SizeInBytes)),
            new("DeliveryCount", frame.DeliveryCount.ToString(CultureInfo.InvariantCulture)),
            new("State", frame.Lane == MessageLane.Scheduled ? "Scheduled" : frame.Deferred ? "Deferred" : "Active"),
            new("SessionId", Kept("session_id")),
            new("CorrelationId", Kept("correlation_id")),
            new("ContentType", Kept("content_type")),
        };
        if (scheduled is { } at)
            system.Add(new("ScheduledEnqueueTime", at.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)));

        var body = bodyNames.Count == 0
            ? "(empty)"
            : "{\n" + string.Join(",\n", bodyNames.Select(name => $"  \"{name}\": \"{Masked}\"")) + "\n}";
        var preview = bodyNames.Count == 0
            ? "body empty"
            : $"masked · {bodyNames.Count} field{(bodyNames.Count == 1 ? "" : "s")}: " + string.Join(", ", bodyNames.Take(4)) + (bodyNames.Count > 4 ? ", …" : "");

        return new QuickMessage(
            frame.SequenceNumber,
            frame.Lane,
            frame.Deferred,
            Kept("message_id"),
            Kept("subject"),
            enqueued?.ToString("HH:mm:ss", CultureInfo.InvariantCulture) ?? None,
            enqueued?.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? None,
            scheduled?.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Size(frame.SizeInBytes),
            frame.DeliveryCount,
            reason,
            result.Fields.TryGetValue("dlq_description", out var description) ? description : null,
            preview,
            system,
            propertyNames.Select(name => new QuickField(name, Masked)).ToList(),
            bodyNames,
            body);
    }

    /// <summary>A field or property name is shown, so it goes through the pattern net and is capped too.</summary>
    private static string SafeName(string name)
    {
        var scrubbed = PiiNet.Scrub(name ?? string.Empty);
        return scrubbed.Length <= 64 ? scrubbed : scrubbed[..61] + "...";
    }

    public static string Size(long? bytes) => bytes switch
    {
        null => None,
        < 1024 => $"{bytes.Value.ToString(CultureInfo.InvariantCulture)} B",
        < 1024 * 1024 => $"{(bytes.Value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes.Value / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB",
    };
}
