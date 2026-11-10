// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using System.Globalization;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public enum JobPanelState
{
    NoCollection,
    Invalid,
    Ready,
    Running,
    Cancelling,
    Passed,
    Failed,
    Cancelled,
    Error,
}

public sealed record JobOption(string Name, string Path)
{
    public override string ToString() => Name;
}

/// <summary>How fast simulated time runs while you watch. Zero means as fast as the plugins answer.</summary>
public sealed record PaceOption(string Label, double SimulatedSecondsPerSecond)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Jobs panel: pick a collection folder, a job and an environment, see the pinned plugins and seed,
/// then run or cancel in process. Test jobs run against the in-memory broker only; the loader and the
/// runner refuse any other provider.
/// </summary>
public sealed class JobsViewModel : ViewModelBase
{
    public const double MaxPauseSeconds = 0.25;

    private readonly IUiScheduler _scheduler;
    private readonly Func<JobRunner> _runnerFactory;
    private readonly Func<string, string?> _environmentVariables;
    private string? _collectionFolder;
    private string? _collectionName;
    private JobOption? _selectedJob;
    private string? _selectedEnvironment;
    private JobDefinition? _definition;
    private string? _validationText;
    private JobPanelState _state = JobPanelState.NoCollection;
    private string _statusText = "Choose a collection folder.";
    private PaceOption _selectedPace;
    private CancellationTokenSource? _cancel;

    public JobsViewModel(
        IUiScheduler scheduler,
        IJobsDialogs? dialogs = null,
        Func<JobRunner>? runnerFactory = null,
        Func<string, string?>? environmentVariables = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _runnerFactory = runnerFactory ?? (() => new JobRunner());
        _environmentVariables = environmentVariables ?? Environment.GetEnvironmentVariable;
        Live = new LiveRunViewModel(scheduler, dialogs);
        Paces =
        [
            new PaceOption("As fast as possible", 0),
            new PaceOption("10 simulated minutes per second", 600),
            new PaceOption("1 simulated minute per second", 60),
        ];
        _selectedPace = Paces[0];
        BrowseCommand = new AsyncCommand(BrowseAsync, () => Dialogs is not null && !IsBusy);
        RunCommand = new AsyncCommand(RunAsync, () => _definition is not null && !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => State == JobPanelState.Running);
    }

    public LiveRunViewModel Live { get; }

    public IJobsDialogs? Dialogs
    {
        get => Live.Dialogs;
        set
        {
            Live.Dialogs = value;
            BrowseCommand.RaiseCanExecuteChanged();
            Live.ExportJUnitCommand.RaiseCanExecuteChanged();
        }
    }

    public AsyncCommand BrowseCommand { get; }

    public AsyncCommand RunCommand { get; }

    public RelayCommand CancelCommand { get; }

    public ObservableCollection<JobOption> Jobs { get; } = [];

    public ObservableCollection<string> Environments { get; } = [];

    /// <summary>Pinned plugins of the selected job, for example "hop-relay@0.1.0 · simulator".</summary>
    public ObservableCollection<string> Plugins { get; } = [];

    public IReadOnlyList<PaceOption> Paces { get; }

    public PaceOption SelectedPace
    {
        get => _selectedPace;
        set => Set(ref _selectedPace, value ?? Paces[0]);
    }

    public string? CollectionFolder => _collectionFolder;

    public string CollectionText => _collectionName is null ? "No collection open." : $"{_collectionName} ({_collectionFolder})";

    public JobOption? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (Set(ref _selectedJob, value))
                Reload();
        }
    }

    public string? SelectedEnvironment
    {
        get => _selectedEnvironment;
        set
        {
            if (Set(ref _selectedEnvironment, value))
                Reload();
        }
    }

    public JobDefinition? Definition => _definition;

    public string SeedText => _definition?.Seed.ToString(CultureInfo.InvariantCulture) ?? "-";

    public string ClockText => _definition is null
        ? "-"
        : string.Create(CultureInfo.InvariantCulture,
            $"{PluginProtocol.FormatTime(_definition.ClockStart)}, step {_definition.Step}, run {_definition.Duration}, drain {_definition.Drain}");

    public string TargetText => _definition is null ? "-" : $"{_definition.TargetEntity} on {_definition.Provider}";

    public string? ValidationText
    {
        get => _validationText;
        private set
        {
            if (Set(ref _validationText, value))
                OnPropertyChanged(nameof(HasValidationText));
        }
    }

    public bool HasValidationText => !string.IsNullOrEmpty(_validationText);

    public JobPanelState State
    {
        get => _state;
        private set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                BrowseCommand.RaiseCanExecuteChanged();
                RunCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy => State is JobPanelState.Running or JobPanelState.Cancelling;

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string SafetyText => "Test jobs use the in-memory broker only. Nothing is sent to a real broker.";

    /// <summary>Open a collection folder: the one with collection.yaml, jobs/ and environments/.</summary>
    public void OpenCollection(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var full = Path.GetFullPath(folder);
        Jobs.Clear();
        Environments.Clear();
        _definition = null;
        _selectedJob = null;
        _selectedEnvironment = null;
        _collectionFolder = full;
        _collectionName = null;

        if (!File.Exists(Path.Combine(full, "collection.yaml")))
        {
            ValidationText = $"No collection.yaml in {full}. Choose the folder of one collection, for example samples/jobs/crew-stock.";
            State = JobPanelState.NoCollection;
            StatusText = "Choose a collection folder.";
            RaiseDefinition();
            return;
        }

        _collectionName = Path.GetFileName(full);
        var jobsDir = Path.Combine(full, "jobs");
        if (Directory.Exists(jobsDir))
        {
            foreach (var path in Directory.GetFiles(jobsDir, "*.job.yaml").Order(StringComparer.Ordinal))
                Jobs.Add(new JobOption(Path.GetFileName(path)[..^".job.yaml".Length], path));
        }

        var envDir = Path.Combine(full, "environments");
        if (Directory.Exists(envDir))
        {
            foreach (var path in Directory.GetFiles(envDir, "*.yaml").Order(StringComparer.Ordinal))
                Environments.Add(Path.GetFileNameWithoutExtension(path));
        }

        _selectedEnvironment = Environments.Contains("local") ? "local" : Environments.FirstOrDefault();
        _selectedJob = Jobs.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedEnvironment));
        OnPropertyChanged(nameof(SelectedJob));
        if (_selectedJob is null)
        {
            ValidationText = "This collection has no jobs/*.job.yaml files.";
            State = JobPanelState.Invalid;
            StatusText = "No jobs.";
            RaiseDefinition();
            return;
        }

        Reload();
    }

    /// <summary>Run the selected job in process. Returns when the live view shows the final result.</summary>
    public async Task RunAsync()
    {
        var job = _definition;
        if (job is null || IsBusy)
            return;

        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        Live.Begin(job);
        State = JobPanelState.Running;
        StatusText = $"Running {job.RunId}...";
        var observer = new PacedObserver(Live, SelectedPace.SimulatedSecondsPerSecond);
        var runner = _runnerFactory();
        try
        {
            var result = await Task.Run(() => runner.RunAsync(job, observer, cancel.Token), CancellationToken.None).ConfigureAwait(true);
            await OnUiAsync(() => Live.Complete(result)).ConfigureAwait(true);
            State = result.Succeeded ? JobPanelState.Passed : JobPanelState.Failed;
            StatusText = Live.Summary;
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() => Live.Stop("Cancelled. Plugins were stopped.")).ConfigureAwait(true);
            State = JobPanelState.Cancelled;
            StatusText = "Cancelled.";
        }
        catch (Exception exception) when (exception is PluginException or JobRunException)
        {
            await OnUiAsync(() => Live.Stop("Stopped: " + exception.Message)).ConfigureAwait(true);
            State = JobPanelState.Error;
            StatusText = "Stopped: " + exception.Message;
        }
        finally
        {
            _cancel = null;
        }
    }

    public void Cancel()
    {
        if (State != JobPanelState.Running || _cancel is null)
            return;
        State = JobPanelState.Cancelling;
        StatusText = "Cancelling...";
        _cancel.Cancel();
    }

    private async Task BrowseAsync()
    {
        if (Dialogs is null)
            return;
        var folder = await Dialogs.PickCollectionFolderAsync().ConfigureAwait(true);
        if (!string.IsNullOrEmpty(folder))
            OpenCollection(folder);
    }

    private void Reload()
    {
        if (_selectedJob is null || IsBusy)
            return;
        try
        {
            _definition = JobLoader.Load(_selectedJob.Path, _selectedEnvironment, _environmentVariables);
            ValidationText = null;
            State = JobPanelState.Ready;
            StatusText = $"Ready: {_definition.RunId} ({_definition.EnvironmentName}).";
        }
        catch (JobValidationException exception)
        {
            _definition = null;
            ValidationText = exception.Message;
            State = JobPanelState.Invalid;
            StatusText = "The job file has problems.";
        }

        RaiseDefinition();
    }

    private void RaiseDefinition()
    {
        Plugins.Clear();
        foreach (var plugin in _definition?.Plugins ?? [])
        {
            var roles = string.Join(", ", plugin.Roles.Order().Select(PluginProtocol.RoleName));
            Plugins.Add($"{plugin.Name}@{plugin.Version} · {roles}{(plugin.Builtin ? " · built in" : string.Empty)}");
        }

        OnPropertyChanged(nameof(Definition));
        OnPropertyChanged(nameof(SeedText));
        OnPropertyChanged(nameof(ClockText));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(CollectionFolder));
        OnPropertyChanged(nameof(CollectionText));
        RunCommand.RaiseCanExecuteChanged();
    }

    private Task OnUiAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _scheduler.Post(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception exception)
            {
                done.SetException(exception);
            }
        });
        return done.Task;
    }

    /// <summary>Hands each step to the live view and, when a pace is set, waits so the run can be watched.</summary>
    private sealed class PacedObserver(LiveRunViewModel live, double simulatedSecondsPerSecond) : IRunObserver
    {
        private DateTimeOffset? _previous;

        public async ValueTask OnStepAsync(RunStep step, CancellationToken cancellationToken)
        {
            live.Enqueue(step);
            var previous = _previous ?? step.Now;
            _previous = step.Now;
            if (simulatedSecondsPerSecond <= 0)
                return;
            var pause = Math.Min(MaxPauseSeconds, (step.Now - previous).TotalSeconds / simulatedSecondsPerSecond);
            if (pause > 0)
                await Task.Delay(TimeSpan.FromSeconds(pause), cancellationToken).ConfigureAwait(false);
        }
    }
}
