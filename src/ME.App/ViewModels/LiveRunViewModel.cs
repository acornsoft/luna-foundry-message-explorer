// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Reporting;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum LiveFilter
{
    All,
    Pending,
    Passed,
    Failed,
    DeadLettered,
    Late,
}

/// <summary>
/// The live run view: expected versus actual per correlation ID while a job runs, with running counters.
/// Steps arrive on the runner's thread and are queued; at most one flush is posted to the UI at a time,
/// and a flush applies every queued step at once. A long run costs one UI update per flush, not per message.
/// </summary>
public sealed class LiveRunViewModel : ViewModelBase
{
    private readonly IUiScheduler _scheduler;
    private readonly ConcurrentQueue<RunStep> _pending = new();
    private readonly Dictionary<string, LiveRowViewModel> _byId = new(StringComparer.Ordinal);
    private readonly List<LiveRowViewModel> _all = [];
    private int _flushQueued;
    private LiveFilter _filter = LiveFilter.All;
    private DateTimeOffset? _clock;
    private DateTimeOffset _clockStart;
    private string _summary = "No run yet.";
    private bool _isRunning;
    private RunResult? _lastResult;
    private string? _exportedTo;

    public LiveRunViewModel(IUiScheduler scheduler, IJobsDialogs? dialogs = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        Dialogs = dialogs;
        ExportJUnitCommand = new AsyncCommand(ExportWithDialogAsync, () => _lastResult is not null && Dialogs is not null);
    }

    public IJobsDialogs? Dialogs { get; set; }

    /// <summary>Rows that match the filter, in send order.</summary>
    public ObservableCollection<LiveRowViewModel> Rows { get; } = [];

    public IReadOnlyList<LiveRowViewModel> AllRows => _all;

    public IReadOnlyList<LiveFilter> Filters { get; } = Enum.GetValues<LiveFilter>();

    /// <summary>Label shown in the filter picker; matches the status column wording.</summary>
    public static string FilterLabel(LiveFilter filter) =>
        filter == LiveFilter.DeadLettered ? "Dead-lettered" : filter.ToString();

    public LiveFilter Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value))
                Refilter();
        }
    }

    public AsyncCommand ExportJUnitCommand { get; }

    public RunResult? LastResult => _lastResult;

    public bool HasResult => _lastResult is not null;

    public bool IsRunning
    {
        get => _isRunning;
        private set => Set(ref _isRunning, value);
    }

    public int FlushCount { get; private set; }

    public int StepCount { get; private set; }

    public int SentCount => _all.Count;

    public int PendingCount => Count(LiveStatus.Pending);

    public int PassedCount => Count(LiveStatus.Passed);

    public int FailedCount => Count(LiveStatus.Failed);

    public int DeadLetteredCount => Count(LiveStatus.DeadLettered);

    public int LateCount => Count(LiveStatus.Late);

    public int Throttled { get; private set; }

    public int Retries { get; private set; }

    public int DeadLetterEvents { get; private set; }

    public int Duplicates { get; private set; }

    public string CountersText => string.Create(CultureInfo.InvariantCulture,
        $"{SentCount} sent · {PendingCount} pending · {PassedCount} passed · {FailedCount} failed · {DeadLetteredCount} dead-lettered · {LateCount} late");

    public string EventsText => string.Create(CultureInfo.InvariantCulture,
        $"Plugin events: {Throttled} throttled · {Retries} retries · {DeadLetterEvents} dead-lettered · {Duplicates} duplicates dropped");

    public string ClockText => _clock is { } now
        ? $"Simulated clock {now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC (+{(now - _clockStart).TotalMinutes.ToString("0", CultureInfo.InvariantCulture)} min)"
        : string.Empty;

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string? ExportedTo
    {
        get => _exportedTo;
        private set => Set(ref _exportedTo, value);
    }

    public void Begin(JobDefinition job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _pending.Clear();
        _byId.Clear();
        _all.Clear();
        Rows.Clear();
        Throttled = Retries = DeadLetterEvents = Duplicates = 0;
        FlushCount = StepCount = 0;
        _clockStart = job.ClockStart;
        _clock = job.ClockStart;
        _lastResult = null;
        ExportedTo = null;
        IsRunning = true;
        Summary = $"Running {job.RunId}...";
        RaiseAll();
    }

    /// <summary>Called on the runner's thread. Queues the step and posts a flush if none is waiting.</summary>
    public void Enqueue(RunStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _pending.Enqueue(step);
        if (Interlocked.Exchange(ref _flushQueued, 1) == 0)
            _scheduler.Post(Flush);
    }

    /// <summary>On the UI thread: apply every queued step, then raise each changed property once.</summary>
    public void Flush()
    {
        Interlocked.Exchange(ref _flushQueued, 0);
        var applied = 0;
        var added = new List<LiveRowViewModel>();
        while (_pending.TryDequeue(out var step))
        {
            applied++;
            _clock = step.Now;
            foreach (var sent in step.Sent)
            {
                if (_byId.ContainsKey(sent.CorrelationId))
                    continue;
                var row = new LiveRowViewModel(sent);
                _byId[sent.CorrelationId] = row;
                _all.Add(row);
                added.Add(row);
            }

            foreach (var observation in step.Observed)
            {
                if (_byId.TryGetValue(observation.CorrelationId, out var row))
                    row.Observe(observation);
            }

            foreach (var notice in step.Events)
                CountEvent(notice.Kind);
        }

        if (applied == 0)
            return;
        StepCount += applied;
        FlushCount++;
        if (_clock is { } now)
        {
            foreach (var row in _all)
                row.Tick(now);
        }

        if (Filter == LiveFilter.All)
        {
            foreach (var row in added)
                Rows.Add(row);
        }
        else
        {
            Refilter();
        }

        RaiseAll();
    }

    public void Complete(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Flush();
        foreach (var record in result.Records)
        {
            if (!_byId.TryGetValue(record.CorrelationId, out var row))
            {
                row = new LiveRowViewModel(new SentRecord(record.CorrelationId, record.SentAt, record.Expected));
                foreach (var observation in record.Actual)
                    row.Observe(observation);
                _byId[record.CorrelationId] = row;
                _all.Add(row);
            }

            row.Finish(record);
        }

        // The run's totals are the source of truth; they also cover events sent after the last step.
        Throttled = Sum(result, "throttled");
        Retries = Sum(result, "retry");
        DeadLetterEvents = Sum(result, "deadLettered");
        Duplicates = Sum(result, "duplicateDropped");
        _clock = result.SimulatedEnd;
        _lastResult = result;
        IsRunning = false;
        Summary = string.Create(CultureInfo.InvariantCulture,
            $"{(result.Succeeded ? "Passed" : "Failed")}: {result.Job.RunId}. {result.Passed} of {result.Records.Count} passed, {result.Failed} failed. {result.SentCount} sent, {result.BusMessages} bus messages, {(result.SimulatedEnd - result.Job.ClockStart).TotalMinutes:0} simulated minutes.");
        Refilter();
        RaiseAll();
        ExportJUnitCommand.RaiseCanExecuteChanged();
    }

    public void Stop(string summary)
    {
        Flush();
        IsRunning = false;
        Summary = summary;
        RaiseAll();
    }

    /// <summary>The JUnit XML for the last run: byte for byte what <c>me-jobs run --junit</c> writes.</summary>
    public async Task ExportJUnitAsync(string path)
    {
        if (_lastResult is null)
            throw new InvalidOperationException("There is no finished run to export.");
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(path, JUnitWriter.Write(_lastResult), new UTF8Encoding(false)).ConfigureAwait(true);
        ExportedTo = path;
    }

    private async Task ExportWithDialogAsync()
    {
        if (_lastResult is null || Dialogs is null)
            return;
        var path = await Dialogs.PickJUnitPathAsync(_lastResult.Job.RunId + ".junit.xml").ConfigureAwait(true);
        if (!string.IsNullOrEmpty(path))
            await ExportJUnitAsync(path).ConfigureAwait(true);
    }

    private static int Sum(RunResult result, string kind) =>
        result.Events.Where(pair => pair.Key.EndsWith("." + kind, StringComparison.Ordinal)).Sum(pair => pair.Value);

    private void CountEvent(string kind)
    {
        switch (kind)
        {
            case "throttled":
                Throttled++;
                break;
            case "retry":
                Retries++;
                break;
            case "deadLettered":
                DeadLetterEvents++;
                break;
            case "duplicateDropped":
                Duplicates++;
                break;
        }
    }

    private int Count(LiveStatus status) => _all.Count(row => row.Status == status);

    private bool Matches(LiveRowViewModel row) => Filter switch
    {
        LiveFilter.All => true,
        LiveFilter.Pending => row.Status == LiveStatus.Pending,
        LiveFilter.Passed => row.Status == LiveStatus.Passed,
        LiveFilter.Failed => row.Status == LiveStatus.Failed,
        LiveFilter.DeadLettered => row.Status == LiveStatus.DeadLettered,
        LiveFilter.Late => row.Status == LiveStatus.Late,
        _ => true,
    };

    private void Refilter()
    {
        Rows.Clear();
        foreach (var row in _all.Where(Matches))
            Rows.Add(row);
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(SentCount));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(PassedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(DeadLetteredCount));
        OnPropertyChanged(nameof(LateCount));
        OnPropertyChanged(nameof(Throttled));
        OnPropertyChanged(nameof(Retries));
        OnPropertyChanged(nameof(DeadLetterEvents));
        OnPropertyChanged(nameof(Duplicates));
        OnPropertyChanged(nameof(CountersText));
        OnPropertyChanged(nameof(EventsText));
        OnPropertyChanged(nameof(ClockText));
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(LastResult));
    }
}
