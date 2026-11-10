// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.Demo;
using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell;

/// <summary>
/// Command-line options for the desktop app:
/// <c>--collection &lt;folder&gt;</c> opens a test-job collection, <c>--tab accounts|logins|queues|topics|jobs|move</c> picks the tab (Move mode itself stays off; no option turns it on),
/// <c>--synthetic-demo</c> (or <c>LFME_SYNTHETIC_DEMO=1</c>) adds and connects the synthetic demo provider,
/// <c>--pace &lt;simulated seconds per second&gt;</c> picks a pace, <c>--run</c> starts the selected job, and
/// <c>--exit-after-run &lt;file&gt;</c> writes the finished report to the file and quits (a smoke check).
/// </summary>
public sealed record StartupOptions(string? Collection, int? Tab, bool SyntheticDemo, double? Pace = null, bool Run = false, string? ExitAfterRunReport = null)
{
    /// <summary>Open the default bundled sample when no collection was given. The tab is left alone.</summary>
    public StartupOptions WithDefaultCollection(string? samplesFolder)
    {
        if (Collection is not null || samplesFolder is null)
            return this;
        var sample = Path.Combine(samplesFolder, BundledSamples.DefaultSample);
        return Directory.Exists(sample) ? this with { Collection = sample } : this;
    }

    public static StartupOptions Parse(IReadOnlyList<string> args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        string? collection = null;
        int? tab = null;
        var demo = environment("LFME_SYNTHETIC_DEMO") is "1" or "true";
        double? pace = null;
        var run = false;
        string? report = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--collection" when i + 1 < args.Count:
                    collection = args[++i];
                    break;
                case "--tab" when i + 1 < args.Count:
                    tab = args[++i].ToLowerInvariant() switch
                    {
                        "accounts" => MainViewModel.AccountsTab,
                        "logins" => MainViewModel.LoginsTab,
                        "queues" => MainViewModel.QueuesTab,
                        "topics" => MainViewModel.TopicsTab,
                        "jobs" => MainViewModel.JobsTab,
                        "move" => MainViewModel.MoveTab,
                        _ => null,
                    };
                    break;
                case "--synthetic-demo":
                    demo = true;
                    break;
                case "--pace" when i + 1 < args.Count:
                    pace = double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
                    break;
                case "--run":
                    run = true;
                    break;
                case "--exit-after-run" when i + 1 < args.Count:
                    report = args[++i];
                    break;
            }
        }

        return new StartupOptions(collection, tab ?? (collection is null ? null : MainViewModel.JobsTab), demo, pace, run, report);
    }

    public void Apply(MainViewModel main)
    {
        ArgumentNullException.ThrowIfNull(main);
        if (Collection is not null)
            main.Jobs.OpenCollection(Collection);
        if (Tab is { } tab)
            main.SelectedTab = tab;
        if (Pace is { } pace)
            main.Jobs.SelectedPace = main.Jobs.Paces.FirstOrDefault(p => p.SimulatedSecondsPerSecond == pace) ?? main.Jobs.SelectedPace;
        if (SyntheticDemo)
        {
            var option = main.Connect.Providers.FirstOrDefault(p => p.Id == SyntheticDemoProvider.Id);
            if (option is not null)
            {
                option.IsSelected = true;
                main.Connect.UseEntra = true;
                main.Connect.NamespaceText = "synthetic-demo";
                main.Connect.ConnectCommand.Execute(null);
            }
        }

        if (Run && main.Jobs.RunCommand.CanExecute(null))
            main.Jobs.RunCommand.Execute(null);
    }
}
