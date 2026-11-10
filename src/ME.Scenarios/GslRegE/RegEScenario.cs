// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Acornsoft.MessageExplorer.Scenarios.GslRegE;

/// <summary>
/// The wire contract the GSL dev consumer expects on its queue (schema gsl.reg-e.deadlines/v1).
/// Written here from the published field list, not shared code. Synthetic data only.
/// </summary>
public static partial class RegEContract
{
    public const string Subject = "reg-e.deadlines.computed";
    public const string SchemaId = "gsl.reg-e.deadlines/v1";
    public const string ContentType = "application/json";
    public const string Source = "lfme-scenario";

    public const string RunIdProperty = "lfmeRunId";
    public const string ScenarioProperty = "lfmeScenario";
    public const string StepProperty = "lfmeStep";
    public const string ResubmitOfProperty = "lfmeResubmitOf";
    public const string FixProperty = "lfmeFix";

    [GeneratedRegex(@"^GSL-DSP-\d{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex DisputeIdPattern();

    public static bool IsDisputeId(string? value) => value is not null && DisputeIdPattern().IsMatch(value);

    public static string DisputeId(int number)
    {
        if (number is < 0 or > 999_999)
            throw new ArgumentOutOfRangeException(nameof(number));
        return "GSL-DSP-" + number.ToString("D6", CultureInfo.InvariantCulture);
    }
}

/// <summary>The dead-letter reasons the consumer uses. The explorer groups by these.</summary>
public static class RegEDeadLetterReasons
{
    public const string BodyNotJson = "BodyNotJson";
    public const string SchemaViolation = "SchemaViolation";
    public const string MissingDisputeId = "MissingDisputeId";
    public const string InvalidDisputeId = "InvalidDisputeId";
    public const string DisputeIdMismatch = "DisputeIdMismatch";
    public const string InconsistentDeadlines = "InconsistentDeadlines";
    public const string MaxDeliveryCountExceeded = "MaxDeliveryCountExceeded";
}

/// <summary>Where a scenario sends. Entra only: there is no field for a key or a connection string.</summary>
public sealed record RegETarget(string FullyQualifiedNamespace, string Queue, DateOnly NoticeDateFrom)
{
    public const string NamespaceSuffix = ".servicebus.windows.net";

    public string NamespaceName => FullyQualifiedNamespace[..^NamespaceSuffix.Length];

    public static RegETarget From(ScenarioDefinition scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var settings = scenario.SettingsOrEmpty;
        string Need(string key) =>
            settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new ScenarioParseException(key + " is required for a live scenario.");

        var fqns = Need("target_namespace");
        if (!fqns.EndsWith(NamespaceSuffix, StringComparison.Ordinal) || fqns.Length <= NamespaceSuffix.Length || fqns.Contains('/', StringComparison.Ordinal))
            throw new ScenarioParseException("target_namespace must be a host name ending in " + NamespaceSuffix + ".");
        if (fqns.Contains(';', StringComparison.Ordinal) || fqns.Contains('=', StringComparison.Ordinal))
            throw new ScenarioParseException("target_namespace looks like a connection string. Use a host name and Entra auth.");

        var queue = Need("target_queue");
        if (Need("auth") != "entra")
            throw new ScenarioParseException("auth must be entra. Keys and connection strings are not supported.");
        if (Need("data_class") != "synthetic")
            throw new ScenarioParseException("data_class must be synthetic.");
        if (!DateOnly.TryParseExact(Need("notice_date_from"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
            throw new ScenarioParseException("notice_date_from must be yyyy-MM-dd.");

        foreach (var key in settings.Keys)
        {
            var lowered = key.ToLowerInvariant();
            if (lowered.Contains("key", StringComparison.Ordinal) || lowered.Contains("secret", StringComparison.Ordinal)
                || lowered.Contains("connection", StringComparison.Ordinal) || lowered.Contains("sas", StringComparison.Ordinal))
                throw new ScenarioParseException("Scenario files cannot carry credentials (" + key + ").");
        }

        return new RegETarget(fqns, queue, from);
    }
}

/// <summary>Reg E error-resolution windows, 12 CFR 1005.11(c). Demo model, not legal advice.</summary>
public sealed record RegEDeadlines(
    DateOnly NoticeDate,
    bool NewAccount,
    bool PosOrForeign,
    int InvestigationBusinessDays,
    DateOnly InvestigationDueDate,
    int ExtendedInvestigationCalendarDays,
    DateOnly ExtendedInvestigationDueDate)
{
    public static RegEDeadlines Compute(DateOnly noticeDate, bool newAccount, bool posOrForeign)
    {
        // 10 business days, or 20 for a new account: 1005.11(c)(1) and (c)(3)(i).
        var businessDays = newAccount ? 20 : 10;
        // 45 calendar days, or 90 for a new account, point of sale, or foreign-initiated transfer: 1005.11(c)(2), (c)(3)(ii).
        var calendarDays = newAccount || posOrForeign ? 90 : 45;
        return new RegEDeadlines(
            noticeDate,
            newAccount,
            posOrForeign,
            businessDays,
            BusinessCalendar.AddBusinessDays(noticeDate, businessDays),
            calendarDays,
            noticeDate.AddDays(calendarDays));
    }

    public IReadOnlyList<string> Citations()
    {
        var list = new List<string> { NewAccount ? "12 CFR 1005.11(c)(3)(i)" : "12 CFR 1005.11(c)(1)", "12 CFR 1005.11(c)(2)(i)" };
        if (ExtendedInvestigationCalendarDays == 90)
            list.Add("12 CFR 1005.11(c)(3)(ii)");
        return list;
    }
}

public enum ExpectedOutcome
{
    Consumed,
    DeadLettered,
}

/// <summary>One message the scenario will send. Body is UTF-8 JSON text.</summary>
public sealed record PlannedMessage(
    string MessageId,
    string? CorrelationId,
    string Subject,
    string ContentType,
    string Body,
    IReadOnlyDictionary<string, object> ApplicationProperties,
    string StepId,
    ExpectedOutcome Expected,
    string? ExpectedReason);

/// <summary>Seeded generator for the GSL Reg E scenario. Same seed and run id give the same messages.</summary>
public static class RegEDisputeGenerator
{
    public const string StandardTemplate = "gsl-reg-e-standard";
    public const string NewAccountTemplate = "gsl-reg-e-new-account";
    public const string PosForeignTemplate = "gsl-reg-e-pos-foreign";
    public const string MissingIdTemplate = "gsl-reg-e-malformed-missing-id";
    public const string InconsistentTemplate = "gsl-reg-e-malformed-inconsistent";
    public const string BadDateTemplate = "gsl-reg-e-malformed-bad-date";

    public static readonly IReadOnlySet<string> Templates = new HashSet<string>(StringComparer.Ordinal)
    {
        StandardTemplate, NewAccountTemplate, PosForeignTemplate, MissingIdTemplate, InconsistentTemplate, BadDateTemplate,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<PlannedMessage> Generate(ScenarioDefinition scenario, string runId)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (string.IsNullOrWhiteSpace(runId) || runId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
            throw new ArgumentException("Run id must be letters, digits, and dashes.", nameof(runId));

        var target = RegETarget.From(scenario);
        var usedIds = new HashSet<int>();
        var result = new List<PlannedMessage>();
        foreach (var step in scenario.Steps)
        {
            if (!Templates.Contains(step.Template))
                throw new ScenarioParseException("Unknown template " + step.Template);
            var stream = ScenarioDefinition.StepStream(scenario.Seed, step.Id);
            var random = new Random(int.Parse(stream[..7], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            for (var i = 0; i < step.Count; i++)
            {
                int number;
                do
                {
                    number = 600_000 + random.Next(0, 100_000);
                }
                while (!usedIds.Add(number));

                var notice = target.NoticeDateFrom.AddDays(random.Next(0, 7));
                var messageId = "lfme-" + runId + "-" + (result.Count + 1).ToString("D3", CultureInfo.InvariantCulture);
                result.Add(Build(scenario.Id, runId, step.Id, step.Template, messageId, RegEContract.DisputeId(number), notice));
            }
        }

        return result;
    }

    private static PlannedMessage Build(string scenarioId, string runId, string stepId, string template, string messageId, string disputeId, DateOnly notice)
    {
        var newAccount = template == NewAccountTemplate;
        var posOrForeign = template == PosForeignTemplate;
        var deadlines = RegEDeadlines.Compute(notice, newAccount, posOrForeign);
        var body = Body(disputeId, deadlines);
        string? correlationId = disputeId;
        var expected = ExpectedOutcome.Consumed;
        string? reason = null;

        switch (template)
        {
            case MissingIdTemplate:
                body.Remove("disputeId");
                correlationId = null;
                expected = ExpectedOutcome.DeadLettered;
                reason = RegEDeadLetterReasons.MissingDisputeId;
                break;
            case InconsistentTemplate:
                body["investigationBusinessDays"] = 15;
                expected = ExpectedOutcome.DeadLettered;
                reason = RegEDeadLetterReasons.InconsistentDeadlines;
                break;
            case BadDateTemplate:
                body["noticeDate"] = notice.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                expected = ExpectedOutcome.DeadLettered;
                reason = RegEDeadLetterReasons.SchemaViolation;
                break;
        }

        var properties = Properties(deadlines, scenarioId, runId, stepId);
        return new PlannedMessage(
            messageId,
            correlationId,
            RegEContract.Subject,
            RegEContract.ContentType,
            JsonSerializer.Serialize(body, Json),
            properties,
            stepId,
            expected,
            reason);
    }

    internal static Dictionary<string, object?> Body(string? disputeId, RegEDeadlines d)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schema"] = RegEContract.SchemaId,
            ["synthetic"] = true,
            ["disputeId"] = disputeId,
            ["noticeDate"] = Iso(d.NoticeDate),
            ["newAccount"] = d.NewAccount,
            ["posOrForeign"] = d.PosOrForeign,
            ["investigationBusinessDays"] = d.InvestigationBusinessDays,
            ["investigationDueDate"] = Iso(d.InvestigationDueDate),
            ["provisionalCreditDueDate"] = Iso(d.InvestigationDueDate),
            ["extendedInvestigationCalendarDays"] = d.ExtendedInvestigationCalendarDays,
            ["extendedInvestigationDueDate"] = Iso(d.ExtendedInvestigationDueDate),
            ["citations"] = d.Citations(),
            ["note"] = "Synthetic message from a Luna Foundry Message Explorer scenario. Not a real dispute.",
        };
        if (disputeId is null)
            body.Remove("disputeId");
        return body;
    }

    internal static Dictionary<string, object> Properties(RegEDeadlines d, string scenarioId, string runId, string stepId) =>
        new(StringComparer.Ordinal)
        {
            ["newAccount"] = d.NewAccount,
            ["posOrForeign"] = d.PosOrForeign,
            ["investigationDueDate"] = Iso(d.InvestigationDueDate),
            ["schema"] = RegEContract.SchemaId,
            ["synthetic"] = true,
            ["source"] = RegEContract.Source,
            [RegEContract.ScenarioProperty] = scenarioId,
            [RegEContract.RunIdProperty] = runId,
            [RegEContract.StepProperty] = stepId,
        };

    internal static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static string Serialize(Dictionary<string, object?> body) => JsonSerializer.Serialize(body, Json);
}
