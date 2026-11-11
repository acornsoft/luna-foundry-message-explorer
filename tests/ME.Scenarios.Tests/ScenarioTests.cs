// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Scenarios;

namespace Acornsoft.MessageExplorer.Scenarios.Tests;

public class ScenarioTests
{
    [Fact]
    public void Starter_scenarios_parse_with_a_seed()
    {
        var dispute = ScenarioParser.LoadEmbedded("reg-e-dispute.yaml");
        var takeover = ScenarioParser.LoadEmbedded("account-takeover.yaml");

        Assert.Equal(63001, dispute.Seed);
        Assert.Equal("virtual", dispute.Clock);
        Assert.Equal(["disputes/reg-e-clock", "disputes/case-sync"], dispute.ExpectedHops);
        Assert.Equal(100, dispute.Steps[0].Count);
        Assert.Equal(dispute.Fingerprint(), ScenarioParser.Parse(ScenarioParser.ReadResource("reg-e-dispute.yaml")).Fingerprint());
        Assert.Equal(63002, takeover.Seed);
        Assert.NotEqual(dispute.Fingerprint(), takeover.Fingerprint());
    }

    [Fact]
    public void A_scenario_without_a_seed_is_rejected()
    {
        var yaml = "id: demo\nclock: virtual\nmax_messages: 1\nmax_duration_seconds: 1\nsteps:\n  - id: one\n    count: 1\n    template: synthetic\n";
        var error = Assert.Throws<ScenarioParseException>(() => ScenarioParser.Parse(yaml));
        Assert.Contains("seed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sandbox_gate_fails_closed()
    {
        var policy = SandboxPolicy.LoadEmbedded();
        var good = new SandboxRequest("sb-gsl-test-synthetic", "00000000-0000-0000-0000-000000000001", true, true);
        Assert.True(SandboxGate.Evaluate(good, policy).Allowed);

        Assert.False(SandboxGate.Evaluate(good with { NamespaceName = "sb-gsl-prod" }, policy).Allowed);
        Assert.False(SandboxGate.Evaluate(good with { TaggedTest = false }, policy).Allowed);
        Assert.False(SandboxGate.Evaluate(good with { DisableLocalAuth = false }, policy).Allowed);
        Assert.False(SandboxGate.Evaluate(good with { SubscriptionId = "not-listed" }, policy).Allowed);
        Assert.False(SandboxGate.Evaluate(good, new SandboxPolicy([], [])).Allowed);
    }

    [Fact]
    public void Kill_switch_stops_on_every_hard_limit()
    {
        var open = new KillSwitchInput(false, 1, 10, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), 0m, 1m);
        Assert.False(KillSwitch.ShouldStop(open));
        Assert.True(KillSwitch.ShouldStop(open with { StopFlagSet = true }));
        Assert.True(KillSwitch.ShouldStop(open with { MessagesSent = 10 }));
        Assert.True(KillSwitch.ShouldStop(open with { Elapsed = TimeSpan.FromMinutes(5) }));
        Assert.True(KillSwitch.ShouldStop(open with { Cost = 1m }));
    }

    [Fact]
    public void Cleanup_deletes_only_resources_from_this_run()
    {
        var tags = new Dictionary<string, string> { ["created-by"] = "me-runner", ["run-id"] = "run-7" };
        Assert.True(ResourceCleanup.MayDelete(tags, "run-7"));
        Assert.False(ResourceCleanup.MayDelete(tags, "run-8"));
        Assert.False(ResourceCleanup.MayDelete(new Dictionary<string, string> { ["created-by"] = "someone", ["run-id"] = "run-7" }, "run-7"));
    }

    [Fact]
    public void Business_days_use_the_fixed_calendar()
    {
        Assert.Equal(new DateOnly(2026, 12, 28), BusinessCalendar.AddBusinessDays(new DateOnly(2026, 12, 24), 1));
    }

    [Fact]
    public void Runner_plans_a_run_and_sends_nothing()
    {
        var scenario = ScenarioParser.LoadEmbedded("reg-e-dispute.yaml");
        var policy = SandboxPolicy.LoadEmbedded();
        var sandbox = new SandboxRequest("sb-gsl-test-synthetic", "00000000-0000-0000-0000-000000000001", true, true);
        var kill = new KillSwitchInput(false, 0, scenario.MaxMessages, TimeSpan.Zero, TimeSpan.FromSeconds(scenario.MaxDurationSeconds), 0m, 1m);
        var runner = new ScenarioRunner();

        var plan = runner.Plan(scenario, sandbox, policy, kill);
        Assert.True(plan.Allowed);
        Assert.False(plan.Executed);
        Assert.Equal(0, plan.MessagesSent);
        Assert.Equal(63001, plan.Seed);
        Assert.Equal(scenario.Fingerprint(), plan.ScenarioHash);

        var blocked = runner.Plan(scenario, sandbox with { TaggedTest = false }, policy, kill);
        Assert.False(blocked.Allowed);
        Assert.False(blocked.Executed);
        Assert.Equal(0, blocked.MessagesSent);

        var stopped = runner.Plan(scenario, sandbox, policy, kill with { StopFlagSet = true });
        Assert.False(stopped.Allowed);
        Assert.Equal(0, stopped.MessagesSent);
    }

    [Fact]
    public void Step_streams_are_stable_and_differ_by_step()
    {
        var left = ScenarioDefinition.StepStream(63001, "plant-disputes");
        var again = ScenarioDefinition.StepStream(63001, "plant-disputes");
        var other = ScenarioDefinition.StepStream(63001, "plant-alerts");
        Assert.Equal(left, again);
        Assert.NotEqual(left, other);
    }

    [Fact]
    public void Virtual_clock_advances_without_waiting()
    {
        var clock = new VirtualClock(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 12, 5, 0, TimeSpan.Zero), clock.UtcNow);
    }
}
