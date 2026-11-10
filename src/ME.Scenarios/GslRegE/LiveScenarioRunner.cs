// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Acornsoft.MessageExplorer.Model;

namespace Acornsoft.MessageExplorer.Scenarios.GslRegE;

/// <summary>Control-plane facts the sandbox gate needs. Read from Azure Resource Manager, not from the scenario file.</summary>
public sealed record NamespaceFacts(
    string NamespaceName,
    string SubscriptionId,
    IReadOnlyDictionary<string, string> Tags,
    bool DisableLocalAuth);

public sealed record QueueCounts(long Active, long DeadLetter)
{
    public override string ToString() =>
        "active=" + Active.ToString(CultureInfo.InvariantCulture) + " dead-letter=" + DeadLetter.ToString(CultureInfo.InvariantCulture);
}

public sealed record ResubmitOutcome(long SequenceNumber, string OriginalMessageId, string? NewMessageId, bool Resubmitted, string Note);

/// <summary>
/// The broker operations a live scenario needs. Simulation mode, not observe mode: it sends, and resubmit
/// receives from the dead-letter subqueue and completes there. The Azure implementation lives in
/// ME.Adapters.ServiceBus.Azure; tests use an in-memory fake.
/// </summary>
public interface IScenarioBroker
{
    Task<NamespaceFacts> DescribeNamespaceAsync(CancellationToken cancellationToken);

    Task<QueueCounts> GetCountsAsync(CancellationToken cancellationToken);

    Task SendAsync(PlannedMessage message, CancellationToken cancellationToken);

    /// <summary>Peek (no lock) up to <paramref name="max"/> dead-lettered messages from sequence 0.</summary>
    Task<IReadOnlyList<DeadLetterSnapshot>> PeekDeadLettersAsync(int max, CancellationToken cancellationToken);

    /// <summary>
    /// Lock up to <paramref name="max"/> dead-lettered messages. For each, <paramref name="fix"/> returns a replacement
    /// (send it to the main queue, then complete the dead-letter) or null (abandon: it stays dead-lettered).
    /// </summary>
    Task<IReadOnlyList<ResubmitOutcome>> ResubmitDeadLettersAsync(
        Func<DeadLetterSnapshot, PlannedMessage?> fix,
        int max,
        CancellationToken cancellationToken);
}

/// <summary>Which tags mark a namespace as a sandbox the runner may write to.</summary>
public static class SandboxTags
{
    /// <summary>
    /// A namespace is a sandbox when it is tagged env=test, or env=dev together with dataClass=synthetic.
    /// dataClass=synthetic alone is not enough: env=prod (or any other env) with that tag is refused.
    /// The GSL dev namespace is tagged env=dev, dataClass=synthetic.
    /// </summary>
    public static bool IsSandbox(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        tags.TryGetValue("env", out var env);
        tags.TryGetValue("dataClass", out var dataClass);
        return env == "test" || (env == "dev" && dataClass == "synthetic");
    }
}

public sealed record LiveRunOptions(string RunId, TimeSpan SettleTimeout, TimeSpan PollInterval, bool DryRun);

public sealed record FixPreview(DeadLetterSnapshot Message, FixResult Fix);

public sealed record LiveRunReport(
    bool Allowed,
    string? BlockReason,
    string RunId,
    int Planned,
    int Sent,
    QueueCounts? Before,
    QueueCounts? After,
    IReadOnlyList<DeadLetterSnapshot> RunDeadLetters,
    IReadOnlyList<FixPreview> FixPlan,
    IReadOnlyList<string> Mismatches);

public sealed record ResubmitReport(bool Allowed, string? BlockReason, bool Executed, IReadOnlyList<FixPreview> Preview, IReadOnlyList<ResubmitOutcome> Outcomes, QueueCounts? After);

/// <summary>Which dead-letters a resubmit may touch: one run's messages, or listed sequence numbers. Never "all".</summary>
public sealed record ResubmitSelection(string? RunId, IReadOnlySet<long> SequenceNumbers)
{
    public bool IsEmpty => RunId is null && SequenceNumbers.Count == 0;

    public bool Matches(DeadLetterSnapshot message) =>
        (RunId is not null && message.RunId == RunId) || SequenceNumbers.Contains(message.SequenceNumber);
}

/// <summary>
/// Runs the GSL Reg E scenario against a live queue: gate, send, watch counts settle, show dead-letters with
/// reasons, and preview the fix. Resubmit is a separate call that needs an explicit confirmation.
/// Output is plain text so the console runner and a later UI panel can show the same lines.
/// </summary>
public sealed class LiveScenarioRunner
{
    public const int PeekCap = 100;

    private readonly IScenarioBroker _broker;
    private readonly SandboxPolicy _policy;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TextWriter _out;

    public LiveScenarioRunner(IScenarioBroker broker, SandboxPolicy policy, IClock clock, Func<TimeSpan, CancellationToken, Task> delay, TextWriter output)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _out = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task<LiveRunReport> RunAsync(ScenarioDefinition scenario, LiveRunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(options);
        var target = RegETarget.From(scenario);
        var messages = RegEDisputeGenerator.Generate(scenario, options.RunId);
        Line($"Scenario {scenario.Id} (seed {scenario.Seed}, synthetic data only), run {options.RunId}");
        Line($"Target {target.FullyQualifiedNamespace} / {target.Queue}, auth: Microsoft Entra");

        var gate = await GateAsync(target, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
            return Blocked(options.RunId, messages.Count, gate);
        if (messages.Count > scenario.MaxMessages)
            return Blocked(options.RunId, messages.Count, $"Scenario plans {messages.Count} messages; max_messages is {scenario.MaxMessages}.");

        var expectedDead = messages.Count(m => m.Expected == ExpectedOutcome.DeadLettered);
        Line($"Plan: {messages.Count} messages, {messages.Count - expectedDead} valid, {expectedDead} malformed");
        foreach (var group in messages.GroupBy(m => m.StepId))
            Line($"  {group.Key,-24} {group.Count(),3}  expect {(group.First().Expected == ExpectedOutcome.Consumed ? "consumed" : "dead-letter " + group.First().ExpectedReason)}");

        var before = await _broker.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        Line($"Counts before: {before}");
        if (options.DryRun)
        {
            Line("Dry run: nothing sent.");
            return new LiveRunReport(true, null, options.RunId, messages.Count, 0, before, before, [], [], []);
        }

        var started = _clock.UtcNow;
        var sent = 0;
        foreach (var message in messages)
        {
            var kill = new KillSwitchInput(false, sent, scenario.MaxMessages, _clock.UtcNow - started, TimeSpan.FromSeconds(scenario.MaxDurationSeconds), 0m, 1m);
            if (KillSwitch.ShouldStop(kill))
            {
                Line($"Kill switch stopped the send after {sent} messages.");
                break;
            }

            await _broker.SendAsync(message, cancellationToken).ConfigureAwait(false);
            sent++;
        }

        Line($"Sent {sent} of {messages.Count}.");

        var after = await WaitForSettleAsync(before, expectedDead, sent == messages.Count, options, cancellationToken).ConfigureAwait(false);

        var dead = (await _broker.PeekDeadLettersAsync(PeekCap, cancellationToken).ConfigureAwait(false))
            .Where(m => m.RunId == options.RunId)
            .OrderBy(m => m.SequenceNumber)
            .ToList();
        Line("");
        Line($"Dead-letters from this run: {dead.Count}");
        foreach (var message in dead)
            Line($"  seq {message.SequenceNumber,-5} {message.CorrelationId ?? "(no dispute id)",-15} {message.StepId,-24} {message.DeadLetterReason}: {message.DeadLetterDescription}");

        var mismatches = Compare(messages.Take(sent).ToList(), dead);
        Line("");
        if (mismatches.Count == 0)
            Line("Every message ended where the scenario expected.");
        else
            foreach (var mismatch in mismatches)
                Line("MISMATCH " + mismatch);

        var plan = dead.Select(m => new FixPreview(m, DeadLetterFixer.TryFix(m))).ToList();
        Line("");
        Line("Fix preview (nothing resubmitted yet):");
        foreach (var item in plan)
            Line($"  seq {item.Message.SequenceNumber,-5} {(item.Fix.Fixable ? "fixable" : "needs a person")}: {item.Fix.Explanation}");
        if (plan.Any(p => p.Fix.Fixable))
            Line($"To resubmit the fixable ones: lfme dlq resubmit --run {options.RunId} --yes");

        return new LiveRunReport(true, null, options.RunId, messages.Count, sent, before, after, dead, plan, mismatches);
    }

    public async Task<ResubmitReport> ResubmitAsync(RegETarget target, ResubmitSelection selection, bool confirmed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(selection);
        if (selection.IsEmpty)
            return new ResubmitReport(false, "Pick a run (--run) or sequence numbers (--sequence). Resubmit never takes the whole dead-letter queue.", false, [], [], null);

        var gate = await GateAsync(target, cancellationToken).ConfigureAwait(false);
        if (gate is not null)
            return new ResubmitReport(false, gate, false, [], [], null);

        var preview = (await _broker.PeekDeadLettersAsync(PeekCap, cancellationToken).ConfigureAwait(false))
            .Where(selection.Matches)
            .OrderBy(m => m.SequenceNumber)
            .Select(m => new FixPreview(m, DeadLetterFixer.TryFix(m)))
            .ToList();
        Line($"Selected {preview.Count} dead-letter(s); {preview.Count(p => p.Fix.Fixable)} fixable.");
        foreach (var item in preview)
            Line($"  seq {item.Message.SequenceNumber,-5} {item.Message.DeadLetterReason}: {(item.Fix.Fixable ? "fix -> " : "skip, needs a person -> ")}{item.Fix.Explanation}");

        if (!confirmed)
        {
            Line("Preview only. Add --yes to resubmit the fixable ones.");
            return new ResubmitReport(true, null, false, preview, [], null);
        }

        var outcomes = await _broker.ResubmitDeadLettersAsync(
            message => selection.Matches(message) ? DeadLetterFixer.TryFix(message).Fixed : null,
            PeekCap,
            cancellationToken).ConfigureAwait(false);
        var mine = outcomes.Where(o => o.Resubmitted || preview.Any(p => p.Message.SequenceNumber == o.SequenceNumber)).ToList();
        foreach (var outcome in mine)
            Line($"  seq {outcome.SequenceNumber,-5} {(outcome.Resubmitted ? "resubmitted as " + outcome.NewMessageId : "left in dead-letter")}: {outcome.Note}");

        var counts = await _broker.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        Line($"Counts after resubmit: {counts}");
        return new ResubmitReport(true, null, true, preview, mine, counts);
    }

    internal static IReadOnlyList<string> Compare(IReadOnlyList<PlannedMessage> sent, IReadOnlyList<DeadLetterSnapshot> dead)
    {
        var byId = dead.ToDictionary(d => d.MessageId, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var message in sent)
        {
            var found = byId.TryGetValue(message.MessageId, out var letter);
            if (message.Expected == ExpectedOutcome.Consumed && found)
                problems.Add($"{message.MessageId} should have been consumed but was dead-lettered ({letter!.DeadLetterReason}).");
            else if (message.Expected == ExpectedOutcome.DeadLettered && !found)
                problems.Add($"{message.MessageId} should have been dead-lettered ({message.ExpectedReason}) but is not in the dead-letter queue yet.");
            else if (message.Expected == ExpectedOutcome.DeadLettered && letter!.DeadLetterReason != message.ExpectedReason)
                problems.Add($"{message.MessageId} dead-lettered as {letter.DeadLetterReason}, expected {message.ExpectedReason}.");
        }

        return problems;
    }

    private async Task<QueueCounts> WaitForSettleAsync(QueueCounts before, int expectedDead, bool allSent, LiveRunOptions options, CancellationToken cancellationToken)
    {
        var deadline = _clock.UtcNow + options.SettleTimeout;
        QueueCounts counts;
        while (true)
        {
            counts = await _broker.GetCountsAsync(cancellationToken).ConfigureAwait(false);
            var elapsed = options.SettleTimeout - (deadline - _clock.UtcNow);
            Line($"  t+{elapsed.TotalSeconds,4:0}s  {counts}");
            var drained = counts.Active <= before.Active;
            var deadArrived = !allSent || counts.DeadLetter - before.DeadLetter >= expectedDead;
            if (drained && deadArrived)
            {
                Line($"Settled: {counts}");
                return counts;
            }

            if (_clock.UtcNow >= deadline)
            {
                Line($"Not settled after {options.SettleTimeout.TotalSeconds:0}s: {counts}. Is the consumer running?");
                return counts;
            }

            await _delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> GateAsync(RegETarget target, CancellationToken cancellationToken)
    {
        var facts = await _broker.DescribeNamespaceAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(facts.NamespaceName, target.NamespaceName, StringComparison.OrdinalIgnoreCase))
            return $"Broker describes {facts.NamespaceName}, but the scenario targets {target.NamespaceName}.";
        var request = new SandboxRequest(facts.NamespaceName, facts.SubscriptionId, SandboxTags.IsSandbox(facts.Tags), facts.DisableLocalAuth);
        var decision = SandboxGate.Evaluate(request, _policy);
        Line($"Sandbox gate: {(decision.Allowed ? "allowed" : "blocked, " + decision.Reason)} (sandbox tag {(request.TaggedTest ? "yes" : "no")}, disableLocalAuth {facts.DisableLocalAuth.ToString().ToLowerInvariant()})");
        return decision.Allowed ? null : decision.Reason;
    }

    private LiveRunReport Blocked(string runId, int planned, string reason)
    {
        Line("Blocked: " + reason);
        return new LiveRunReport(false, reason, runId, planned, 0, null, null, [], [], []);
    }

    private void Line(string text) => _out.WriteLine(text);
}
