// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;

namespace Acornsoft.MessageExplorer.Scenarios.GslRegE;

/// <summary>A dead-lettered message as the explorer shows it. Read from a peek; nothing is locked.</summary>
public sealed record DeadLetterSnapshot(
    long SequenceNumber,
    string MessageId,
    string? CorrelationId,
    string? Subject,
    string? DeadLetterReason,
    string? DeadLetterDescription,
    int DeliveryCount,
    DateTimeOffset EnqueuedTime,
    string Body,
    IReadOnlyDictionary<string, object> ApplicationProperties)
{
    public string? RunId => ApplicationProperties.TryGetValue(RegEContract.RunIdProperty, out var value) ? value?.ToString() : null;

    public string? StepId => ApplicationProperties.TryGetValue(RegEContract.StepProperty, out var value) ? value?.ToString() : null;
}

/// <summary>Either a corrected message ready to resubmit, or why a person has to look at it.</summary>
public sealed record FixResult(PlannedMessage? Fixed, string Explanation)
{
    public bool Fixable => Fixed is not null;
}

/// <summary>
/// "Fix, then resubmit" for the Reg E contract. A fix only uses what is already in the message:
/// it stamps a missing dispute id and recomputes the deadlines from noticeDate and the two flags.
/// It never invents a notice date or a flag. If those are unreadable, a person has to decide.
/// </summary>
public static class DeadLetterFixer
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "schema", "synthetic", "disputeId", "noticeDate", "newAccount", "posOrForeign", "investigationBusinessDays",
        "investigationDueDate", "provisionalCreditDueDate", "extendedInvestigationCalendarDays", "extendedInvestigationDueDate",
        "citations", "note",
    };

    /// <summary>A replacement synthetic id derived from the sequence number, so the same message always gets the same id.</summary>
    public static string ReplacementDisputeId(long sequenceNumber) =>
        RegEContract.DisputeId(900_000 + (int)(Math.Abs(sequenceNumber) % 100_000));

    public static FixResult TryFix(DeadLetterSnapshot message)
    {
        ArgumentNullException.ThrowIfNull(message);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(message.Body);
        }
        catch (JsonException)
        {
            return new FixResult(null, "Body is not JSON. A person has to rebuild it.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new FixResult(null, "Body is not a JSON object. A person has to rebuild it.");

            if (!TryGet(root, "noticeDate", out var noticeElement) || noticeElement.ValueKind != JsonValueKind.String
                || !DateOnly.TryParseExact(noticeElement.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var notice))
                return new FixResult(null, "noticeDate is missing or not yyyy-MM-dd. The fixer will not guess a date; a person has to supply it.");

            if (!TryBool(root, "newAccount", out var newAccount) || !TryBool(root, "posOrForeign", out var posOrForeign))
                return new FixResult(null, "newAccount or posOrForeign is missing. The fixer will not guess a flag.");

            var changes = new List<string>();
            string? bodyId = TryGet(root, "disputeId", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
            string disputeId;
            if (RegEContract.IsDisputeId(bodyId) && (message.CorrelationId is null || message.CorrelationId == bodyId))
                disputeId = bodyId!;
            else if (bodyId is null && RegEContract.IsDisputeId(message.CorrelationId))
                disputeId = message.CorrelationId!;
            else
            {
                disputeId = ReplacementDisputeId(message.SequenceNumber);
                changes.Add("assigned synthetic dispute id " + disputeId);
            }

            var deadlines = RegEDeadlines.Compute(notice, newAccount, posOrForeign);
            // The sender owns the business-day calendar. When the day counts and the calendar-day due date already
            // agree with the rules, keep the sender's investigation due date instead of overriding it with ours.
            if (CountsAgree(root, deadlines)
                && DateOnly.TryParseExact(Str(root, "investigationDueDate"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var senderDue)
                && senderDue > notice)
                deadlines = deadlines with { InvestigationDueDate = senderDue };
            if (!SameDeadlines(root, deadlines))
                changes.Add("recomputed deadlines (" + deadlines.InvestigationBusinessDays.ToString(CultureInfo.InvariantCulture) + " business days, "
                    + deadlines.ExtendedInvestigationCalendarDays.ToString(CultureInfo.InvariantCulture) + " calendar days)");
            if (HasPascalCase(root))
                changes.Add("rewrote field names to camelCase");
            if (changes.Count == 0)
                changes.Add("no content change; resubmitting as is");

            var body = RegEDisputeGenerator.Body(disputeId, deadlines);
            var properties = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var pair in message.ApplicationProperties)
            {
                if (!pair.Key.StartsWith("DeadLetter", StringComparison.Ordinal) && pair.Key != "Diagnostic-Id")
                    properties[pair.Key] = pair.Value;
            }

            properties["newAccount"] = newAccount;
            properties["posOrForeign"] = posOrForeign;
            properties["investigationDueDate"] = RegEDisputeGenerator.Iso(deadlines.InvestigationDueDate);
            properties["schema"] = RegEContract.SchemaId;
            properties["synthetic"] = true;
            properties[RegEContract.ResubmitOfProperty] = message.MessageId;
            var explanation = string.Join("; ", changes);
            properties[RegEContract.FixProperty] = explanation;

            var fixedMessage = new PlannedMessage(
                ResubmitId(message.MessageId),
                disputeId,
                RegEContract.Subject,
                RegEContract.ContentType,
                RegEDisputeGenerator.Serialize(body),
                properties,
                message.StepId ?? "resubmit",
                ExpectedOutcome.Consumed,
                null);
            return new FixResult(fixedMessage, explanation);
        }
    }

    internal static string ResubmitId(string original)
    {
        var index = original.LastIndexOf("-r", StringComparison.Ordinal);
        if (index > 0 && int.TryParse(original[(index + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            return original[..index] + "-r" + (n + 1).ToString(CultureInfo.InvariantCulture);
        return original + "-r1";
    }

    private static bool CountsAgree(JsonElement root, RegEDeadlines d)
    {
        return Int(root, "investigationBusinessDays") == d.InvestigationBusinessDays
            && Int(root, "extendedInvestigationCalendarDays") == d.ExtendedInvestigationCalendarDays
            && Str(root, "extendedInvestigationDueDate") == RegEDisputeGenerator.Iso(d.ExtendedInvestigationDueDate);
    }

    private static bool SameDeadlines(JsonElement root, RegEDeadlines d)
    {
        return Int(root, "investigationBusinessDays") == d.InvestigationBusinessDays
            && Int(root, "extendedInvestigationCalendarDays") == d.ExtendedInvestigationCalendarDays
            && Str(root, "investigationDueDate") == RegEDisputeGenerator.Iso(d.InvestigationDueDate)
            && Str(root, "extendedInvestigationDueDate") == RegEDisputeGenerator.Iso(d.ExtendedInvestigationDueDate);
    }

    private static bool HasPascalCase(JsonElement root) =>
        root.EnumerateObject().Any(p => Known.Contains(p.Name) && p.Name.Length > 0 && char.IsUpper(p.Name[0]));

    private static int? Int(JsonElement root, string name) =>
        TryGet(root, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string? Str(JsonElement root, string name) =>
        TryGet(root, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryBool(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!TryGet(root, name, out var v))
            return false;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = v.GetBoolean();
            return true;
        }

        return false;
    }

    private static bool TryGet(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
