// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Scenarios.GslRegE;

namespace Acornsoft.MessageExplorer.Scenarios.Tests;

public class LiveScenarioRunnerTests
{
    private static ScenarioDefinition Scenario() => ScenarioParser.LoadEmbedded("gsl-reg-e-dispute.yaml");

    private static (LiveScenarioRunner Runner, FakeRegEBroker Broker, StringWriter Output) Make(FakeRegEBroker? broker = null)
    {
        broker ??= new FakeRegEBroker();
        var clock = new StepClock();
        var output = new StringWriter();
        return (new LiveScenarioRunner(broker, SandboxPolicy.LoadEmbedded(), clock, clock.Delay, output), broker, output);
    }

    private static LiveRunOptions Options(bool dryRun = false) => new("run-1", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), dryRun);

    [Fact]
    public async Task Full_run_sends_settles_and_explains_the_dead_letters()
    {
        var (runner, broker, output) = Make();
        var report = await runner.RunAsync(Scenario(), Options());

        Assert.True(report.Allowed);
        Assert.Equal(15, report.Sent);
        Assert.Equal(12, broker.Completed);
        Assert.Equal(new QueueCounts(0, 3), report.After);
        Assert.Equal(3, report.RunDeadLetters.Count);
        Assert.Empty(report.Mismatches);
        Assert.Equal(2, report.FixPlan.Count(p => p.Fix.Fixable));
        var text = output.ToString();
        Assert.Contains("Sandbox gate: allowed", text, StringComparison.Ordinal);
        Assert.Contains("Settled: active=0 dead-letter=3", text, StringComparison.Ordinal);
        Assert.Contains("Every message ended where the scenario expected.", text, StringComparison.Ordinal);
        Assert.Contains("lfme dlq resubmit --run run-1 --yes", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dry_run_sends_nothing()
    {
        var (runner, broker, output) = Make();
        var report = await runner.RunAsync(Scenario(), Options(dryRun: true));
        Assert.True(report.Allowed);
        Assert.Equal(0, report.Sent);
        Assert.Empty(broker.Sent);
        Assert.Contains("Dry run: nothing sent.", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("env-prod")]
    [InlineData("env-prod-synthetic")]
    [InlineData("local-auth")]
    [InlineData("other-namespace")]
    [InlineData("other-subscription")]
    public async Task Gate_blocks_before_any_send(string variant)
    {
        var broker = new FakeRegEBroker();
        broker.Facts = variant switch
        {
            "env-prod" => broker.Facts with { Tags = new Dictionary<string, string> { ["env"] = "prod" } },
            "env-prod-synthetic" => broker.Facts with { Tags = new Dictionary<string, string> { ["env"] = "prod", ["dataClass"] = "synthetic" } },
            "local-auth" => broker.Facts with { DisableLocalAuth = false },
            "other-namespace" => broker.Facts with { NamespaceName = "sbns-somewhere-else" },
            _ => broker.Facts with { SubscriptionId = "11111111-1111-1111-1111-111111111111" },
        };
        var (runner, _, output) = Make(broker);
        var report = await runner.RunAsync(Scenario(), Options());
        Assert.False(report.Allowed);
        Assert.Empty(broker.Sent);
        Assert.Contains("Blocked:", output.ToString(), StringComparison.Ordinal);
        if (variant is "env-prod" or "env-prod-synthetic")
            Assert.Contains("Accept env=test, or env=dev together with dataClass=synthetic", report.BlockReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Max_messages_is_enforced()
    {
        var (runner, broker, _) = Make();
        var report = await runner.RunAsync(Scenario() with { MaxMessages = 10 }, Options());
        Assert.False(report.Allowed);
        Assert.Empty(broker.Sent);
    }

    [Fact]
    public async Task Without_a_consumer_the_run_times_out_and_reports_mismatches()
    {
        var (runner, broker, output) = Make(new FakeRegEBroker { ConsumerRunning = false });
        var report = await runner.RunAsync(Scenario(), Options());
        Assert.Equal(new QueueCounts(15, 0), report.After);
        Assert.Contains("Is the consumer running?", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, report.Mismatches.Count);
        Assert.Equal(15, broker.Active.Count);
    }

    [Fact]
    public async Task Only_this_runs_dead_letters_are_reported()
    {
        var broker = new FakeRegEBroker();
        broker.DeadLetters.Add(new DeadLetterSnapshot(999, "older", null, null, "MaxDeliveryCountExceeded", "x", 10, DateTimeOffset.UnixEpoch, "{}", new Dictionary<string, object>()));
        var (runner, _, _) = Make(broker);
        var report = await runner.RunAsync(Scenario(), Options());
        Assert.DoesNotContain(report.RunDeadLetters, d => d.SequenceNumber == 999);
        Assert.Equal(3, report.RunDeadLetters.Count);
    }

    [Fact]
    public async Task Resubmit_previews_without_yes_and_changes_nothing()
    {
        var (runner, broker, output) = Make();
        await runner.RunAsync(Scenario(), Options());
        var report = await runner.ResubmitAsync(RegETarget.From(Scenario()), new ResubmitSelection("run-1", new HashSet<long>()), confirmed: false);
        Assert.True(report.Allowed);
        Assert.False(report.Executed);
        Assert.Equal(3, broker.DeadLetters.Count);
        Assert.Contains("Add --yes", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resubmit_with_yes_moves_fixable_ones_and_they_are_consumed()
    {
        var (runner, broker, _) = Make();
        await runner.RunAsync(Scenario(), Options());
        var report = await runner.ResubmitAsync(RegETarget.From(Scenario()), new ResubmitSelection("run-1", new HashSet<long>()), confirmed: true);
        Assert.True(report.Executed);
        Assert.Equal(2, report.Outcomes.Count(o => o.Resubmitted));
        broker.Drain();
        Assert.Single(broker.DeadLetters);
        Assert.Equal("SchemaViolation", broker.DeadLetters[0].DeadLetterReason);
        Assert.Equal(14, broker.Completed);
    }

    [Fact]
    public async Task Resubmit_never_touches_unselected_messages()
    {
        var broker = new FakeRegEBroker();
        var legacy = "{\"NoticeDate\":\"2026-11-09\",\"NewAccount\":false,\"PosOrForeign\":false,\"InvestigationBusinessDays\":10,\"InvestigationDueDate\":\"2026-11-24\",\"ExtendedInvestigationCalendarDays\":45,\"ExtendedInvestigationDueDate\":\"2026-12-24\"}";
        broker.DeadLetters.Add(new DeadLetterSnapshot(1, "older-1", null, null, "MaxDeliveryCountExceeded", "x", 10, DateTimeOffset.UnixEpoch, legacy, new Dictionary<string, object>()));
        broker.DeadLetters.Add(new DeadLetterSnapshot(2, "older-2", null, null, "MaxDeliveryCountExceeded", "x", 10, DateTimeOffset.UnixEpoch, legacy, new Dictionary<string, object>()));
        var (runner, _, _) = Make(broker);

        var report = await runner.ResubmitAsync(RegETarget.From(Scenario()), new ResubmitSelection(null, new HashSet<long> { 2 }), confirmed: true);

        Assert.Single(report.Outcomes, o => o.Resubmitted);
        Assert.Equal(1, Assert.Single(broker.DeadLetters).SequenceNumber);
    }

    [Fact]
    public async Task Resubmit_needs_a_selection()
    {
        var (runner, broker, _) = Make();
        var report = await runner.ResubmitAsync(RegETarget.From(Scenario()), new ResubmitSelection(null, new HashSet<long>()), confirmed: true);
        Assert.False(report.Allowed);
        Assert.Contains("never takes the whole dead-letter queue", report.BlockReason, StringComparison.Ordinal);
        Assert.Equal(0, broker.CountCalls);
    }

    [Fact]
    public async Task Resubmit_is_gated_too()
    {
        var broker = new FakeRegEBroker();
        broker.Facts = broker.Facts with { DisableLocalAuth = false };
        var (runner, _, _) = Make(broker);
        var report = await runner.ResubmitAsync(RegETarget.From(Scenario()), new ResubmitSelection("run-1", new HashSet<long>()), confirmed: true);
        Assert.False(report.Allowed);
    }

    [Fact]
    public void Compare_flags_each_kind_of_mismatch()
    {
        var messages = RegEDisputeGenerator.Generate(Scenario(), "run-1");
        var valid = messages.First(m => m.Expected == ExpectedOutcome.Consumed);
        var bad = messages.First(m => m.ExpectedReason == "SchemaViolation");
        DeadLetterSnapshot Dead(PlannedMessage m, string reason) =>
            new(1, m.MessageId, m.CorrelationId, m.Subject, reason, "", 1, DateTimeOffset.UnixEpoch, m.Body, m.ApplicationProperties);

        Assert.Contains("should have been consumed", Assert.Single(LiveScenarioRunner.Compare([valid], [Dead(valid, "X")])), StringComparison.Ordinal);
        Assert.Contains("should have been dead-lettered", Assert.Single(LiveScenarioRunner.Compare([bad], [])), StringComparison.Ordinal);
        Assert.Contains("expected SchemaViolation", Assert.Single(LiveScenarioRunner.Compare([bad], [Dead(bad, "BodyNotJson")])), StringComparison.Ordinal);
        Assert.Empty(LiveScenarioRunner.Compare([bad], [Dead(bad, "SchemaViolation")]));
    }
}
