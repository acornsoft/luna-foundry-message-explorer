// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json;
using Acornsoft.MessageExplorer.Model;
using Acornsoft.MessageExplorer.Scenarios.GslRegE;

namespace Acornsoft.MessageExplorer.Scenarios.Tests;

/// <summary>
/// In-memory queue with a consumer that applies the published gsl.reg-e.deadlines/v1 rules.
/// Written independently from the generator so the two can disagree and a test can catch it.
/// </summary>
internal sealed class FakeRegEBroker : IScenarioBroker
{
    private long _sequence;

    public NamespaceFacts Facts { get; set; } = new(
        "sbns-gsl-dev-eastus-001",
        "00000000-0000-0000-0000-000000000001",
        new Dictionary<string, string> { ["env"] = "dev", ["dataClass"] = "synthetic" },
        DisableLocalAuth: true);

    public bool ConsumerRunning { get; set; } = true;
    public List<PlannedMessage> Sent { get; } = [];
    public List<(long Sequence, PlannedMessage Message)> Active { get; } = [];
    public List<DeadLetterSnapshot> DeadLetters { get; } = [];
    public int Completed { get; private set; }
    public int CountCalls { get; private set; }

    public Task<NamespaceFacts> DescribeNamespaceAsync(CancellationToken cancellationToken) => Task.FromResult(Facts);

    public Task<QueueCounts> GetCountsAsync(CancellationToken cancellationToken)
    {
        CountCalls++;
        if (ConsumerRunning && CountCalls > 1)
            Drain();
        return Task.FromResult(new QueueCounts(Active.Count, DeadLetters.Count));
    }

    public Task SendAsync(PlannedMessage message, CancellationToken cancellationToken)
    {
        Sent.Add(message);
        Active.Add((++_sequence, message));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DeadLetterSnapshot>> PeekDeadLettersAsync(int max, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeadLetterSnapshot>>(DeadLetters.Take(max).ToList());

    public Task<IReadOnlyList<ResubmitOutcome>> ResubmitDeadLettersAsync(Func<DeadLetterSnapshot, PlannedMessage?> fix, int max, CancellationToken cancellationToken)
    {
        var outcomes = new List<ResubmitOutcome>();
        foreach (var letter in DeadLetters.Take(max).ToList())
        {
            var replacement = fix(letter);
            if (replacement is null)
            {
                outcomes.Add(new ResubmitOutcome(letter.SequenceNumber, letter.MessageId, null, false, "not selected or not fixable"));
                continue;
            }

            DeadLetters.Remove(letter);
            Active.Add((++_sequence, replacement));
            outcomes.Add(new ResubmitOutcome(letter.SequenceNumber, letter.MessageId, replacement.MessageId, true, "fixed"));
        }

        return Task.FromResult<IReadOnlyList<ResubmitOutcome>>(outcomes);
    }

    public void Drain()
    {
        foreach (var (sequence, message) in Active.ToList())
        {
            Active.RemoveAll(item => item.Sequence == sequence);
            var (reason, description) = Judge(message);
            if (reason is null)
            {
                Completed++;
                continue;
            }

            DeadLetters.Add(new DeadLetterSnapshot(sequence, message.MessageId, message.CorrelationId, message.Subject, reason, description, 1,
                DateTimeOffset.UnixEpoch, message.Body, message.ApplicationProperties));
        }
    }

    public static (string? Reason, string? Description) Judge(PlannedMessage message)
    {
        using var doc = JsonDocument.Parse(message.Body);
        var root = doc.RootElement;
        var bodyId = root.TryGetProperty("disputeId", out var id) ? id.GetString() : null;
        if (bodyId is null && message.CorrelationId is null)
            return ("MissingDisputeId", "no id");
        if (!root.TryGetProperty("noticeDate", out var n) || !DateOnly.TryParseExact(n.GetString(), "yyyy-MM-dd", out var notice))
            return ("SchemaViolation", "noticeDate");
        var newAccount = root.GetProperty("newAccount").GetBoolean();
        var pos = root.GetProperty("posOrForeign").GetBoolean();
        var businessDays = root.GetProperty("investigationBusinessDays").GetInt32();
        var calendarDays = root.GetProperty("extendedInvestigationCalendarDays").GetInt32();
        var extended = DateOnly.ParseExact(root.GetProperty("extendedInvestigationDueDate").GetString()!, "yyyy-MM-dd");
        if (businessDays != (newAccount ? 20 : 10) || calendarDays != (newAccount || pos ? 90 : 45) || extended != notice.AddDays(calendarDays))
            return ("InconsistentDeadlines", "counts");
        return (null, null);
    }
}

internal sealed class StepClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 10, 5, 0, 0, TimeSpan.Zero);

    public Task Delay(TimeSpan by, CancellationToken cancellationToken)
    {
        UtcNow += by;
        return Task.CompletedTask;
    }
}
