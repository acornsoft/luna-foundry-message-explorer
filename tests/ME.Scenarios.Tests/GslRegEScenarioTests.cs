// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using Acornsoft.MessageExplorer.Scenarios.GslRegE;

namespace Acornsoft.MessageExplorer.Scenarios.Tests;

public class GslRegEScenarioTests
{
    private static ScenarioDefinition Scenario() => ScenarioParser.LoadEmbedded("gsl-reg-e-dispute.yaml");

    private static SandboxPolicy Policy() => SandboxPolicy.LoadEmbedded();

    [Fact]
    public void Scenario_parses_with_target_settings_and_entra_auth()
    {
        var scenario = Scenario();
        var target = RegETarget.From(scenario);
        Assert.Equal("gsl-reg-e-dispute", scenario.Id);
        Assert.Equal(63101, scenario.Seed);
        Assert.Equal("sbns-gsl-dev-eastus-001.servicebus.windows.net", target.FullyQualifiedNamespace);
        Assert.Equal("sbns-gsl-dev-eastus-001", target.NamespaceName);
        Assert.Equal("sbq-gsl-dev-001", target.Queue);
        Assert.Equal("entra", scenario.SettingsOrEmpty["auth"]);
        Assert.Equal(15, scenario.Steps.Sum(step => step.Count));
    }

    [Fact]
    public void Older_scenarios_keep_their_fingerprint_and_have_no_settings()
    {
        var old = ScenarioParser.LoadEmbedded("reg-e-dispute.yaml");
        Assert.Null(old.Settings);
        Assert.Empty(old.SettingsOrEmpty);
        Assert.Equal(old.Fingerprint(), (old with { Settings = null }).Fingerprint());
        Assert.NotEqual(Scenario().Fingerprint(), (Scenario() with { Settings = null }).Fingerprint());
    }

    [Theory]
    [InlineData("auth: entra", "auth: sas", "auth must be entra")]
    [InlineData("target_namespace: sbns-gsl-dev-eastus-001.servicebus.windows.net", "target_namespace: sbns.example.com", "target_namespace")]
    [InlineData("target_namespace: sbns-gsl-dev-eastus-001.servicebus.windows.net", "target_namespace: Endpoint=sb//x.servicebus.windows.net", "target_namespace")]
    [InlineData("data_class: synthetic", "data_class: production", "data_class")]
    [InlineData("notice_date_from: 2026-11-02", "notice_date_from: 11/02/2026", "notice_date_from")]
    [InlineData("target_queue: sbq-gsl-dev-001", "", "target_queue is required")]
    [InlineData("auth: entra", "auth: entra\nshared_key: abc", "cannot carry credentials")]
    public void Target_rejects_unsafe_or_missing_settings(string find, string replace, string expected)
    {
        var yaml = ScenarioParser.ReadResource("gsl-reg-e-dispute.yaml").Replace(find, replace, StringComparison.Ordinal);
        var error = Assert.Throws<ScenarioParseException>(() => RegETarget.From(ScenarioParser.Parse(yaml)));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_is_deterministic_for_a_seed_and_run()
    {
        var first = RegEDisputeGenerator.Generate(Scenario(), "run-1");
        var again = RegEDisputeGenerator.Generate(Scenario(), "run-1");
        Assert.Equal(first.Select(m => m.Body), again.Select(m => m.Body));
        Assert.Equal(first.Select(m => m.CorrelationId), again.Select(m => m.CorrelationId));
        var otherSeed = RegEDisputeGenerator.Generate(Scenario() with { Seed = 1 }, "run-1");
        Assert.NotEqual(first.Select(m => m.CorrelationId), otherSeed.Select(m => m.CorrelationId));
    }

    [Fact]
    public void Generator_makes_the_planned_mix()
    {
        var messages = RegEDisputeGenerator.Generate(Scenario(), "run-1");
        Assert.Equal(15, messages.Count);
        Assert.Equal(12, messages.Count(m => m.Expected == ExpectedOutcome.Consumed));
        Assert.Equal(3, messages.Count(m => m.Expected == ExpectedOutcome.DeadLettered));
        Assert.Equal(3, messages.Count(m => m.ApplicationProperties["newAccount"] is true));
        Assert.Equal(3, messages.Count(m => m.ApplicationProperties["posOrForeign"] is true));
        Assert.Equal(
            ["InconsistentDeadlines", "MissingDisputeId", "SchemaViolation"],
            messages.Where(m => m.ExpectedReason is not null).Select(m => m.ExpectedReason!).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_message_is_stamped_and_labeled_synthetic()
    {
        foreach (var message in RegEDisputeGenerator.Generate(Scenario(), "run-1"))
        {
            Assert.Equal("reg-e.deadlines.computed", message.Subject);
            Assert.Equal("application/json", message.ContentType);
            Assert.StartsWith("lfme-run-1-", message.MessageId, StringComparison.Ordinal);
            Assert.Equal(true, message.ApplicationProperties["synthetic"]);
            Assert.Equal("run-1", message.ApplicationProperties[RegEContract.RunIdProperty]);
            Assert.Equal("lfme-scenario", message.ApplicationProperties["source"]);
            Assert.Contains("Not a real dispute", message.Body, StringComparison.Ordinal);
            if (message.StepId != "missing-dispute-id")
                Assert.True(RegEContract.IsDisputeId(message.CorrelationId));
        }
    }

    [Fact]
    public void Message_ids_and_dispute_ids_are_unique()
    {
        var messages = RegEDisputeGenerator.Generate(Scenario(), "run-1");
        Assert.Equal(messages.Count, messages.Select(m => m.MessageId).Distinct().Count());
        var ids = messages.Where(m => m.CorrelationId is not null).Select(m => m.CorrelationId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Valid_messages_pass_an_independent_contract_check_and_malformed_ones_fail_for_the_planned_reason()
    {
        foreach (var message in RegEDisputeGenerator.Generate(Scenario(), "run-1"))
        {
            var (reason, _) = FakeRegEBroker.Judge(message);
            Assert.Equal(message.ExpectedReason, reason);
        }
    }

    [Fact]
    public void Body_matches_the_published_field_list()
    {
        var valid = RegEDisputeGenerator.Generate(Scenario(), "run-1").First(m => m.StepId == "pos-foreign");
        using var doc = JsonDocument.Parse(valid.Body);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var field in new[] { "schema", "synthetic", "disputeId", "noticeDate", "newAccount", "posOrForeign", "investigationBusinessDays", "investigationDueDate", "provisionalCreditDueDate", "extendedInvestigationCalendarDays", "extendedInvestigationDueDate", "citations" })
            Assert.Contains(field, names);
        Assert.Equal(90, doc.RootElement.GetProperty("extendedInvestigationCalendarDays").GetInt32());
    }

    [Theory]
    [InlineData("bad run")]
    [InlineData("")]
    [InlineData("run/1")]
    public void Run_ids_are_restricted(string runId) =>
        Assert.Throws<ArgumentException>(() => RegEDisputeGenerator.Generate(Scenario(), runId));

    [Fact]
    public void Unknown_template_is_rejected()
    {
        var yaml = ScenarioParser.ReadResource("gsl-reg-e-dispute.yaml").Replace("template: gsl-reg-e-standard", "template: something-else", StringComparison.Ordinal);
        Assert.Throws<ScenarioParseException>(() => RegEDisputeGenerator.Generate(ScenarioParser.Parse(yaml), "r1"));
    }

    [Theory]
    [InlineData("2026-11-09", false, false, 10, "2026-11-24", 45, "2026-12-24")]
    [InlineData("2026-11-09", true, false, 20, "2026-12-09", 90, "2027-02-07")]
    [InlineData("2026-11-09", false, true, 10, "2026-11-24", 90, "2027-02-07")]
    [InlineData("2026-11-20", false, false, 10, "2026-12-07", 45, "2027-01-04")]
    public void Deadline_math_follows_the_rule_and_the_holiday_calendar(string notice, bool newAccount, bool pos, int days, string due, int calendar, string extended)
    {
        var d = RegEDeadlines.Compute(DateOnly.Parse(notice), newAccount, pos);
        Assert.Equal(days, d.InvestigationBusinessDays);
        Assert.Equal(DateOnly.Parse(due), d.InvestigationDueDate);
        Assert.Equal(calendar, d.ExtendedInvestigationCalendarDays);
        Assert.Equal(DateOnly.Parse(extended), d.ExtendedInvestigationDueDate);
    }

    [Fact]
    public void Holiday_calendar_skips_veterans_day_and_thanksgiving()
    {
        Assert.False(BusinessCalendar.IsBusinessDay(new DateOnly(2026, 11, 11)));
        Assert.False(BusinessCalendar.IsBusinessDay(new DateOnly(2026, 11, 26)));
        Assert.False(BusinessCalendar.IsBusinessDay(new DateOnly(2027, 7, 5)));
        Assert.True(BusinessCalendar.IsBusinessDay(new DateOnly(2026, 11, 12)));
    }

    [Theory]
    [InlineData("env", "test", true)]
    [InlineData("dataClass", "synthetic", true)]
    [InlineData("env", "dev", false)]
    [InlineData("env", "prod", false)]
    [InlineData("dataClass", "real", false)]
    public void Sandbox_tags(string key, string value, bool expected) =>
        Assert.Equal(expected, SandboxTags.IsSandbox(new Dictionary<string, string> { [key] = value }));

    [Fact]
    public void Extra_subscriptions_extend_only_subscriptions()
    {
        var policy = Policy().WithSubscriptions([" sub-a ", "", "sub-b"]);
        Assert.Contains("sub-a", policy.Subscriptions);
        Assert.Contains("sub-b", policy.Subscriptions);
        Assert.DoesNotContain("", policy.Subscriptions);
        Assert.Equal(Policy().Namespaces.Order(), policy.Namespaces.Order());
    }

    [Fact]
    public void Embedded_allow_list_names_the_dev_namespace_but_no_real_subscription()
    {
        var policy = Policy();
        Assert.Contains("sbns-gsl-dev-eastus-001", policy.Namespaces);
        Assert.Equal(["00000000-0000-0000-0000-000000000001"], policy.Subscriptions.ToArray());
    }
}
