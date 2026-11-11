// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Reporting;
using Acornsoft.MessageExplorer.Jobs.Running;
using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>What the packaged (self-contained) desktop app needs: bundled samples, and plugins without a dotnet install.</summary>
public sealed class PackagedAppTests
{
    [Fact]
    public void Bundled_samples_are_found_in_the_app_bundle_resources()
    {
        using var temp = new TempFolder();
        temp.Write("LFME.app/Contents/Resources/samples/jobs/erp-standin/collection.yaml", "collection: erp-standin\n");
        temp.Write("LFME.app/Contents/Resources/samples/jobs/crew-stock/collection.yaml", "collection: crew-stock\n");
        temp.Write("LFME.app/Contents/Resources/samples/jobs/gsl-reg-e/README.md", "stub\n");
        var macOs = Path.Combine(temp.Root, "LFME.app", "Contents", "MacOS");
        Directory.CreateDirectory(macOs);

        var folder = BundledSamples.Locate(macOs);

        Assert.Equal(Path.Combine(temp.Root, "LFME.app", "Contents", "Resources", "samples", "jobs"), folder);
        Assert.Equal(["crew-stock", "erp-standin"], BundledSamples.List(folder).Select(s => s.Name));
    }

    [Fact]
    public void Samples_next_to_the_app_are_found_and_none_gives_null()
    {
        using var temp = new TempFolder();
        Assert.Null(BundledSamples.Locate(temp.Root));
        Assert.Empty(BundledSamples.List(null));

        temp.Write("samples/jobs/python-hello/collection.yaml", "collection: python-hello\n");
        Assert.Equal(Path.Combine(temp.Root, "samples", "jobs"), BundledSamples.Locate(temp.Root));
    }

    [Fact]
    public void The_jobs_panel_lists_bundled_samples_opens_one_and_starts_the_picker_there()
    {
        var samples = Repo.Path("samples", "jobs");
        var dialogs = new FakeDialogs(null, null);
        var panel = new JobsViewModel(InlineUiScheduler.Instance, dialogs, environmentVariables: _ => null, samplesFolder: samples);

        Assert.Equal(["crew-stock", "erp-standin", "python-hello"], panel.Samples.Select(s => s.Name));
        Assert.True(panel.HasSamples);

        panel.SelectedSample = panel.Samples.Single(s => s.Name == "erp-standin");
        Assert.Equal(Repo.ErpStandin, panel.CollectionFolder);
        Assert.Equal(JobPanelState.Ready, panel.State);

        panel.BrowseCommand.Execute(null);
        Assert.Equal(samples, dialogs.StartFolder);
    }

    [Fact]
    public void Startup_opens_the_default_sample_only_when_no_collection_was_given()
    {
        var samples = Repo.Path("samples", "jobs");
        var none = StartupOptions.Parse([], _ => null).WithDefaultCollection(samples);
        Assert.Equal(Path.Combine(samples, BundledSamples.DefaultSample), none.Collection);
        Assert.Null(none.Section);

        var given = StartupOptions.Parse(["--collection", "/x"], _ => null).WithDefaultCollection(samples);
        Assert.Equal("/x", given.Collection);

        Assert.Null(StartupOptions.Parse([], _ => null).WithDefaultCollection(null).Collection);
        Assert.Equal("/tmp/r.txt", StartupOptions.Parse(["--exit-after-run", "/tmp/r.txt"], _ => null).ExitAfterRunReport);
    }

    [Fact]
    public async Task The_report_after_a_run_names_the_state_summary_and_counters()
    {
        var panel = new JobsViewModel(InlineUiScheduler.Instance, environmentVariables: _ => null);
        Assert.Null(panel.FinishedReport);
        panel.OpenCollection(Repo.ErpStandin);

        await panel.RunAsync();

        var lines = panel.FinishedReport!.Split('\n');
        Assert.Equal("Passed", lines[0]);
        Assert.StartsWith("Passed: erp-standin.erp-day.seed-424242. 55 of 55 passed", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("55 sent", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plugins_run_inside_the_desktop_app_with_the_same_results_as_dotnet()
    {
        Assert.True(File.Exists(Repo.DesktopDll), Repo.DesktopDll);
        var options = new PluginHostOptions
        {
            // The package runs its own executable; here the desktop build output runs under dotnet instead.
            CommandResolver = command => DotnetPluginCommand.RunWithSelf(command, Repo.DesktopDll) is var self && !ReferenceEquals(self, command)
                ? ["dotnet", .. self]
                : command,
        };
        var job = JobLoader.Load(Path.Combine(Repo.ErpStandin, "jobs", "erp-day.job.yaml"), null, _ => null);

        var selfHosted = await new JobRunner(new DefaultPluginLauncher(options)).RunAsync(job);
        var plain = await new JobRunner().RunAsync(job);

        Assert.True(selfHosted.Succeeded);
        Assert.Equal(JUnitWriter.Write(plain), JUnitWriter.Write(selfHosted));
    }

    /// <summary>Set LFME_PACKAGED_EXE to a published app executable to check that build (for example single-file) hosts plugins too.</summary>
    [PackagedExeFact]
    public async Task A_published_app_executable_hosts_the_plugins()
    {
        var exe = PackagedExeFactAttribute.Executable!;
        var options = new PluginHostOptions { CommandResolver = command => DotnetPluginCommand.RunWithSelf(command, exe) };
        var job = JobLoader.Load(Path.Combine(Repo.ErpStandin, "jobs", "erp-day.job.yaml"), null, _ => null);

        var selfHosted = await new JobRunner(new DefaultPluginLauncher(options)).RunAsync(job);

        Assert.Equal(JUnitWriter.Write(await new JobRunner().RunAsync(job)), JUnitWriter.Write(selfHosted));
    }
}

public sealed class PackagedExeFactAttribute : FactAttribute
{
    public static readonly string? Executable = Environment.GetEnvironmentVariable("LFME_PACKAGED_EXE");

    public PackagedExeFactAttribute()
    {
        if (string.IsNullOrEmpty(Executable))
            Skip = "LFME_PACKAGED_EXE is not set.";
    }
}
