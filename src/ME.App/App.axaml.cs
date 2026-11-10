// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Running;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Acornsoft.MessageExplorer.Shell;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var samples = BundledSamples.Locate(AppContext.BaseDirectory);
            var options = StartupOptions.Parse(desktop.Args ?? [], Environment.GetEnvironmentVariable).WithDefaultCollection(samples);
            var accounts = options.SyntheticDemo ? AccountServices.SyntheticDemo() : AccountServices.ForThisComputer(Environment.GetEnvironmentVariable);
            var main = new MainViewModel(MainViewModel.DefaultCatalog(options.SyntheticDemo), new DispatcherUiScheduler(), RunnerFactory(), samples, accounts);
            if (options.ExitAfterRunReport is { } report)
                ExitAfterRun(desktop, main.Jobs, report);
            options.Apply(main);
            desktop.MainWindow = new MainWindow { DataContext = main };
            _ = accounts.ImportLegacyAsync();
            _ = main.Accounts.RestoreAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The packaged app is self-contained; it runs <c>dotnet plugin.dll</c> commands itself.</summary>
    private static Func<JobRunner>? RunnerFactory()
    {
        if (DotnetPluginCommand.SelfHostPath(Environment.ProcessPath) is not { } self)
            return null;
        var options = new PluginHostOptions { CommandResolver = command => DotnetPluginCommand.RunWithSelf(command, self) };
        return () => new JobRunner(new DefaultPluginLauncher(options));
    }

    private static void ExitAfterRun(IClassicDesktopStyleApplicationLifetime desktop, JobsViewModel jobs, string reportPath)
    {
        jobs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(JobsViewModel.FinishedReport) || jobs.FinishedReport is not { } report)
                return;
            var full = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, report + "\n");
            desktop.Shutdown(jobs.State == JobPanelState.Passed ? 0 : 1);
        };
    }
}
