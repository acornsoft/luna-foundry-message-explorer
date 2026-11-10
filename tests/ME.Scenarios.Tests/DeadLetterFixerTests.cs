// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Scenarios.GslRegE;

namespace Acornsoft.MessageExplorer.Scenarios.Tests;

public class DeadLetterFixerTests
{
    private static DeadLetterSnapshot Letter(string body, string? correlationId = null, long sequence = 42, string reason = "MissingDisputeId", string messageId = "m-1") =>
        new(sequence, messageId, correlationId, "reg-e.deadlines.computed", reason, "d", 1, DateTimeOffset.UnixEpoch, body,
            new Dictionary<string, object> { ["lfmeRunId"] = "r1", ["lfmeStep"] = "x", ["Diagnostic-Id"] = "00-abc" });

    private const string Legacy =
        "{\"NoticeDate\":\"2026-11-09\",\"NewAccount\":false,\"PosOrForeign\":false,\"InvestigationBusinessDays\":10,\"InvestigationDueDate\":\"2026-11-24\"," +
        "\"ProvisionalCreditDueDate\":\"2026-11-24\",\"ExtendedInvestigationCalendarDays\":45,\"ExtendedInvestigationDueDate\":\"2026-12-24\"}";

    [Fact]
    public void Missing_id_gets_a_stable_synthetic_id()
    {
        var fix = DeadLetterFixer.TryFix(Letter(Legacy, sequence: 3));
        Assert.True(fix.Fixable);
        Assert.Equal("GSL-DSP-900003", fix.Fixed!.CorrelationId);
        Assert.Contains("GSL-DSP-900003", fix.Fixed.Body, StringComparison.Ordinal);
        Assert.Contains("assigned synthetic dispute id", fix.Explanation, StringComparison.Ordinal);
        Assert.Contains("camelCase", fix.Explanation, StringComparison.Ordinal);
        Assert.Equal(fix.Fixed.CorrelationId, DeadLetterFixer.TryFix(Letter(Legacy, sequence: 3)).Fixed!.CorrelationId);
    }

    [Fact]
    public void Fixed_message_passes_the_contract_and_carries_lineage()
    {
        var fix = DeadLetterFixer.TryFix(Letter(Legacy));
        Assert.Null(FakeRegEBroker.Judge(fix.Fixed!).Reason);
        Assert.Equal("m-1-r1", fix.Fixed!.MessageId);
        Assert.Equal("m-1", fix.Fixed.ApplicationProperties[RegEContract.ResubmitOfProperty]);
        Assert.Equal("r1", fix.Fixed.ApplicationProperties[RegEContract.RunIdProperty]);
        Assert.False(fix.Fixed.ApplicationProperties.ContainsKey("Diagnostic-Id"));
        Assert.Equal("2026-11-24", fix.Fixed.ApplicationProperties["investigationDueDate"]);
    }

    [Fact]
    public void Inconsistent_counts_are_recomputed()
    {
        var body = Legacy.Replace("\"InvestigationBusinessDays\":10", "\"InvestigationBusinessDays\":15", StringComparison.Ordinal);
        var fix = DeadLetterFixer.TryFix(Letter(body, "GSL-DSP-123456", reason: "InconsistentDeadlines"));
        Assert.True(fix.Fixable);
        Assert.Equal("GSL-DSP-123456", fix.Fixed!.CorrelationId);
        Assert.Contains("recomputed deadlines (10 business days, 45 calendar days)", fix.Explanation, StringComparison.Ordinal);
        Assert.Null(FakeRegEBroker.Judge(fix.Fixed).Reason);
    }

    [Fact]
    public void Sender_investigation_date_is_kept_when_counts_agree()
    {
        var body = Legacy.Replace("\"InvestigationDueDate\":\"2026-11-24\"", "\"InvestigationDueDate\":\"2026-11-25\"", StringComparison.Ordinal);
        var fix = DeadLetterFixer.TryFix(Letter(body, "GSL-DSP-123456"));
        Assert.Contains("\"investigationDueDate\":\"2026-11-25\"", fix.Fixed!.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("recomputed", fix.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_message_is_resubmitted_as_is()
    {
        var camel = "{\"disputeId\":\"GSL-DSP-123456\",\"noticeDate\":\"2026-11-09\",\"newAccount\":false,\"posOrForeign\":false,\"investigationBusinessDays\":10," +
            "\"investigationDueDate\":\"2026-11-24\",\"extendedInvestigationCalendarDays\":45,\"extendedInvestigationDueDate\":\"2026-12-24\"}";
        var fix = DeadLetterFixer.TryFix(Letter(camel, "GSL-DSP-123456", reason: "MaxDeliveryCountExceeded"));
        Assert.Equal("no content change; resubmitting as is", fix.Explanation);
    }

    [Theory]
    [InlineData("not json", "not JSON")]
    [InlineData("[1]", "not a JSON object")]
    [InlineData("{\"noticeDate\":\"11/09/2026\",\"newAccount\":false,\"posOrForeign\":false}", "will not guess a date")]
    [InlineData("{\"noticeDate\":\"2026-11-09\",\"posOrForeign\":false}", "will not guess a flag")]
    public void Unfixable_messages_say_why(string body, string expected)
    {
        var fix = DeadLetterFixer.TryFix(Letter(body));
        Assert.False(fix.Fixable);
        Assert.Contains(expected, fix.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Mismatched_ids_get_a_replacement()
    {
        var body = "{\"disputeId\":\"GSL-DSP-111111\",\"noticeDate\":\"2026-11-09\",\"newAccount\":false,\"posOrForeign\":false}";
        var fix = DeadLetterFixer.TryFix(Letter(body, "GSL-DSP-222222", sequence: 7));
        Assert.Equal("GSL-DSP-900007", fix.Fixed!.CorrelationId);
    }

    [Theory]
    [InlineData("m-1", "m-1-r1")]
    [InlineData("m-1-r1", "m-1-r2")]
    [InlineData("m-1-r9", "m-1-r10")]
    public void Resubmit_ids_count_up(string original, string expected) =>
        Assert.Equal(expected, DeadLetterFixer.ResubmitId(original));
}
