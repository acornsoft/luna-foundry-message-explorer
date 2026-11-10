// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Acornsoft.MessageExplorer.Model;

namespace Acornsoft.MessageExplorer.Scenarios;

public sealed class ScenarioParseException : FormatException
{
    public ScenarioParseException(string message)
        : base(message)
    {
    }
}

public sealed record ScenarioStep(string Id, int Count, string Template);

public sealed record ScenarioDefinition(
    string Id,
    int Seed,
    string Clock,
    int MaxMessages,
    int MaxDurationSeconds,
    IReadOnlyList<ScenarioStep> Steps,
    IReadOnlyList<string> ExpectedHops,
    IReadOnlyDictionary<string, string>? Settings = null)
{
    /// <summary>Root keys other than the reserved ones, for example <c>target_queue</c>.</summary>
    public IReadOnlyDictionary<string, string> SettingsOrEmpty =>
        Settings ?? new Dictionary<string, string>(StringComparer.Ordinal);

    public string Fingerprint()
    {
        var text = Id
            + "|"
            + Seed.ToString(CultureInfo.InvariantCulture)
            + "|"
            + string.Join(",", Steps.Select(step => step.Id + ":" + step.Count.ToString(CultureInfo.InvariantCulture) + ":" + step.Template));
        if (Settings is { Count: > 0 })
            text += "|" + string.Join(",", Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    public static string StepStream(int seed, string stepId)
    {
        var text = seed.ToString(CultureInfo.InvariantCulture) + ":" + stepId;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}

public static class ScenarioParser
{
    public static ScenarioDefinition Parse(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var root = new Dictionary<string, string>(StringComparer.Ordinal);
        var steps = new List<ScenarioStep>();
        var hops = new List<string>();
        StepDraft? step = null;
        var section = "";

        foreach (var raw in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = StripComment(raw);
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                var item = trimmed[2..].Trim();
                if (section == "expected_hops")
                {
                    hops.Add(item);
                    continue;
                }

                if (section == "steps")
                {
                    if (step is not null)
                        steps.Add(step.Build());
                    step = StepDraft.Start(item);
                    continue;
                }

                throw new ScenarioParseException("A list item appeared outside a section.");
            }

            if (line.StartsWith("    ", StringComparison.Ordinal) && step is not null && section == "steps")
            {
                step.Apply(trimmed);
                continue;
            }

            if (step is not null && section == "steps")
            {
                steps.Add(step.Build());
                step = null;
            }

            var (key, value) = Split(trimmed);
            if (value.Length == 0)
            {
                section = key;
                continue;
            }

            section = "";
            root[key] = value;
        }

        if (step is not null)
            steps.Add(step.Build());

        if (!root.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
            throw new ScenarioParseException("Scenario id is required.");
        if (!root.TryGetValue("seed", out var seedText) || !int.TryParse(seedText, NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
            throw new ScenarioParseException("Scenario seed is required.");
        if (!root.TryGetValue("clock", out var clock) || clock is not ("virtual" or "wall"))
            throw new ScenarioParseException("Clock must be virtual or wall.");
        if (!root.TryGetValue("max_messages", out var maxText) || !int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out var maxMessages) || maxMessages < 1)
            throw new ScenarioParseException("max_messages is required.");
        if (!root.TryGetValue("max_duration_seconds", out var durationText) || !int.TryParse(durationText, NumberStyles.None, CultureInfo.InvariantCulture, out var duration) || duration < 1)
            throw new ScenarioParseException("max_duration_seconds is required.");
        if (steps.Count == 0)
            throw new ScenarioParseException("A scenario needs at least one step.");

        var reserved = new HashSet<string>(StringComparer.Ordinal) { "id", "seed", "clock", "max_messages", "max_duration_seconds" };
        var settings = root.Where(pair => !reserved.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        return new ScenarioDefinition(id, seed, clock, maxMessages, duration, steps, hops, settings.Count == 0 ? null : settings);
    }

    public static ScenarioDefinition LoadEmbedded(string resourceName)
    {
        return Parse(ReadResource(resourceName));
    }

    public static string ReadResource(string resourceName)
    {
        using var stream = typeof(ScenarioParser).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Missing scenario resource " + resourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string StripComment(string raw)
    {
        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith('#'))
            return "";
        return raw.TrimEnd();
    }

    private static (string key, string value) Split(string line)
    {
        var index = line.IndexOf(':');
        if (index <= 0)
            throw new ScenarioParseException("Expected key: value.");
        var key = line[..index].Trim();
        var value = line[(index + 1)..].Trim();
        return (key, value);
    }

    private sealed class StepDraft
    {
        private string? _id;
        private int? _count;
        private string? _template;

        public static StepDraft Start(string item)
        {
            var draft = new StepDraft();
            draft.Apply(item);
            return draft;
        }

        public void Apply(string item)
        {
            var index = item.IndexOf(':');
            if (index <= 0)
                throw new ScenarioParseException("Expected a step field.");
            var key = item[..index].Trim();
            var value = item[(index + 1)..].Trim();
            if (key == "id")
                _id = value;
            else if (key == "count")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                    throw new ScenarioParseException("Step count must be an integer.");
                _count = count;
            }
            else if (key == "template")
                _template = value;
            else
                throw new ScenarioParseException("Unknown step field " + key);
        }

        public ScenarioStep Build()
        {
            if (string.IsNullOrWhiteSpace(_id) || _count is null || string.IsNullOrWhiteSpace(_template))
                throw new ScenarioParseException("A step needs id, count, and template.");
            return new ScenarioStep(_id, _count.Value, _template);
        }
    }
}

public sealed record SandboxRequest(
    string NamespaceName,
    string SubscriptionId,
    bool TaggedTest,
    bool DisableLocalAuth);

public sealed class SandboxPolicy
{
    public SandboxPolicy(IEnumerable<string> namespaces, IEnumerable<string> subscriptions)
    {
        Namespaces = new HashSet<string>(namespaces, StringComparer.Ordinal);
        Subscriptions = new HashSet<string>(subscriptions, StringComparer.Ordinal);
    }

    public IReadOnlySet<string> Namespaces { get; }
    public IReadOnlySet<string> Subscriptions { get; }

    public static SandboxPolicy LoadEmbedded()
    {
        return new SandboxPolicy(ReadNames("allowed-namespaces.txt"), ReadNames("allowed-subscriptions.txt"));
    }

    /// <summary>
    /// Adds subscriptions from local configuration (for example the LFME_ALLOWED_SUBSCRIPTIONS variable) so real
    /// subscription ids stay out of the repository. Namespaces still come only from the embedded list.
    /// </summary>
    public SandboxPolicy WithSubscriptions(IEnumerable<string> extra)
    {
        ArgumentNullException.ThrowIfNull(extra);
        var cleaned = extra.Select(item => item.Trim()).Where(item => item.Length > 0);
        return new SandboxPolicy(Namespaces, Subscriptions.Concat(cleaned));
    }

    private static IEnumerable<string> ReadNames(string resourceName)
    {
        foreach (var raw in ScenarioParser.ReadResource(resourceName).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            yield return line;
        }
    }
}

public sealed record SandboxDecision(bool Allowed, string Reason)
{
    public static SandboxDecision Allow() => new(true, "");
    public static SandboxDecision Block(string reason) => new(false, reason);
}

public static class SandboxGate
{
    public static SandboxDecision Evaluate(SandboxRequest request, SandboxPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.Namespaces.Count == 0 || policy.Subscriptions.Count == 0)
            return SandboxDecision.Block("Allow-list is empty.");
        if (!request.TaggedTest)
            return SandboxDecision.Block("Namespace is not tagged env=test.");
        if (!request.DisableLocalAuth)
            return SandboxDecision.Block("disableLocalAuth must be true.");
        if (!policy.Namespaces.Contains(request.NamespaceName))
            return SandboxDecision.Block("Namespace is not on the allow-list.");
        if (!policy.Subscriptions.Contains(request.SubscriptionId))
            return SandboxDecision.Block("Subscription is not on the allow-list.");
        return SandboxDecision.Allow();
    }
}

public sealed record KillSwitchInput(
    bool StopFlagSet,
    int MessagesSent,
    int MaxMessages,
    TimeSpan Elapsed,
    TimeSpan MaxDuration,
    decimal Cost,
    decimal CostCap);

public static class KillSwitch
{
    public static bool ShouldStop(KillSwitchInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return input.StopFlagSet
            || input.MessagesSent >= input.MaxMessages
            || input.Elapsed >= input.MaxDuration
            || input.Cost >= input.CostCap;
    }
}

public static class ResourceCleanup
{
    public static bool MayDelete(IReadOnlyDictionary<string, string> tags, string runId)
    {
        ArgumentNullException.ThrowIfNull(tags);
        return tags.TryGetValue("created-by", out var createdBy)
            && createdBy == "me-runner"
            && tags.TryGetValue("run-id", out var taggedRun)
            && taggedRun == runId;
    }
}

public static class BusinessCalendar
{
    // US federal holidays as observed (Saturday -> Friday, Sunday -> Monday), 2026 and 2027.
    // A fixed list keeps scenario runs reproducible. Extend it before running scenarios dated after 2027.
    private static readonly HashSet<DateOnly> Holidays =
    [
        new(2026, 1, 1),
        new(2026, 1, 19),
        new(2026, 2, 16),
        new(2026, 5, 25),
        new(2026, 6, 19),
        new(2026, 7, 3),
        new(2026, 9, 7),
        new(2026, 10, 12),
        new(2026, 11, 11),
        new(2026, 11, 26),
        new(2026, 12, 25),
        new(2027, 1, 1),
        new(2027, 1, 18),
        new(2027, 2, 15),
        new(2027, 5, 31),
        new(2027, 6, 18),
        new(2027, 7, 5),
        new(2027, 9, 6),
        new(2027, 10, 11),
        new(2027, 11, 11),
        new(2027, 11, 25),
        new(2027, 12, 24),
    ];

    public static bool IsBusinessDay(DateOnly date) =>
        date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
        && !Holidays.Contains(date);

    public static DateOnly AddBusinessDays(DateOnly start, int days)
    {
        if (days < 0)
            throw new ArgumentOutOfRangeException(nameof(days));
        var date = start;
        var left = days;
        while (left > 0)
        {
            date = date.AddDays(1);
            if (IsBusinessDay(date))
                left--;
        }

        return date;
    }
}

public sealed class VirtualClock : IClock
{
    public VirtualClock(DateTimeOffset start) => UtcNow = start;

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by)
    {
        if (by < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(by));
        UtcNow += by;
    }
}

public sealed record ScenarioPlan(
    bool Allowed,
    string? BlockReason,
    int Seed,
    string ScenarioHash,
    bool Executed,
    int MessagesSent)
{
    public static ScenarioPlan Blocked(string reason) => new(false, reason, 0, "", false, 0);

    public static ScenarioPlan Planned(ScenarioDefinition scenario) =>
        new(true, null, scenario.Seed, scenario.Fingerprint(), false, 0);
}

public sealed class ScenarioRunner
{
    public ScenarioPlan Plan(ScenarioDefinition scenario, SandboxRequest sandbox, SandboxPolicy policy, KillSwitchInput kill)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        var decision = SandboxGate.Evaluate(sandbox, policy);
        if (!decision.Allowed)
            return ScenarioPlan.Blocked(decision.Reason);
        if (KillSwitch.ShouldStop(kill))
            return ScenarioPlan.Blocked("Kill switch is set.");
        return ScenarioPlan.Planned(scenario);
    }
}
