// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum LiveStatus
{
    Pending,
    Passed,
    Failed,
    DeadLettered,
    Late,
}

/// <summary>One correlation ID in the live view: what was expected, what has been seen, and the status.</summary>
public sealed class LiveRowViewModel : ViewModelBase
{
    private readonly List<Observation> _actual = [];
    private LiveStatus _status = LiveStatus.Pending;
    private string _reason = string.Empty;
    private bool _final;

    public LiveRowViewModel(SentRecord sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        CorrelationId = sent.CorrelationId;
        SentAt = sent.SentAt;
        Expected = sent.Expected;
        Deadline = Expected
            .Select(e => e["within"] is JsonValue v && v.TryGetValue<string>(out var text) ? TryDuration(text) : null)
            .Where(d => d is not null)
            .Select(d => (DateTimeOffset?)(SentAt + d!.Value))
            .Min();
        ExpectedText = string.Join("\n", Expected.Select(e => (string?)e["description"] ?? e.ToJsonString()));
    }

    public string CorrelationId { get; }

    public DateTimeOffset SentAt { get; }

    public IReadOnlyList<JsonObject> Expected { get; }

    /// <summary>Sent time plus the shortest <c>within</c> in the expectations, when there is one.</summary>
    public DateTimeOffset? Deadline { get; }

    public string SentText => SentAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string ExpectedText { get; }

    /// <summary>The hop timeline: each observation as "+seconds sink".</summary>
    public string ActualText => _actual.Count == 0
        ? "(nothing yet)"
        : string.Join("\n", _actual.Select(a => $"+{Seconds(a.At - SentAt)} {a.Sink}"));

    public TimeSpan? Latency => _actual.Count == 0 ? null : _actual.Max(a => a.At) - SentAt;

    public string LatencyText => Latency is { } latency ? Seconds(latency) : "-";

    public int HopCount => _actual.Count;

    public LiveStatus Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsPassed));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsDeadLettered));
                OnPropertyChanged(nameof(IsLate));
                OnPropertyChanged(nameof(IsPending));
            }
        }
    }

    public string StatusText => Status switch
    {
        LiveStatus.DeadLettered => "dead-lettered",
        _ => Status.ToString().ToLowerInvariant(),
    };

    public bool IsPending => Status == LiveStatus.Pending;

    public bool IsPassed => Status == LiveStatus.Passed;

    public bool IsFailed => Status == LiveStatus.Failed;

    public bool IsDeadLettered => Status == LiveStatus.DeadLettered;

    public bool IsLate => Status == LiveStatus.Late;

    public string Reason
    {
        get => _reason;
        private set => Set(ref _reason, value);
    }

    public bool IsFinal => _final;

    public static bool IsDeadLetterSink(string sink) => sink.EndsWith("/$deadletterqueue", StringComparison.OrdinalIgnoreCase);

    internal void Observe(Observation observation)
    {
        _actual.Add(observation);
        OnPropertyChanged(nameof(ActualText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(Latency));
        OnPropertyChanged(nameof(HopCount));
        if (!_final && IsDeadLetterSink(observation.Sink))
            Status = LiveStatus.DeadLettered;
    }

    /// <summary>
    /// While running: a row with nothing seen by its deadline turns late. A row that has hops but no verdict
    /// stays pending; only the assertion plugin can say whether those hops were enough, and it answers at the end.
    /// </summary>
    internal void Tick(DateTimeOffset now)
    {
        if (!_final && Status == LiveStatus.Pending && _actual.Count == 0 && Deadline is { } deadline && now > deadline)
            Status = LiveStatus.Late;
    }

    /// <summary>After the run: the verdict decides. A pass that went through a dead-letter queue shows as dead-lettered.</summary>
    internal void Finish(CorrelationRecord record)
    {
        _final = true;
        Reason = record.Verdict.Reason;
        var deadLettered = record.Actual.Any(a => IsDeadLetterSink(a.Sink));
        var late = Deadline is { } deadline && record.Actual.Count > 0 && SentAt + record.SimulatedDuration > deadline;
        Status = record.Verdict.Status switch
        {
            VerdictStatus.Pass when deadLettered => LiveStatus.DeadLettered,
            VerdictStatus.Pass => LiveStatus.Passed,
            _ when late && !deadLettered => LiveStatus.Late,
            _ => LiveStatus.Failed,
        };
    }

    private static TimeSpan? TryDuration(string text)
    {
        try
        {
            return XmlConvert.ToTimeSpan(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Seconds(TimeSpan span)
    {
        var builder = new StringBuilder();
        var seconds = (long)Math.Round(span.TotalSeconds);
        if (seconds >= 3600)
            builder.Append(CultureInfo.InvariantCulture, $"{seconds / 3600}h");
        if (seconds >= 60)
            builder.Append(CultureInfo.InvariantCulture, $"{seconds % 3600 / 60}m");
        builder.Append(CultureInfo.InvariantCulture, $"{seconds % 60}s");
        return builder.ToString();
    }
}
