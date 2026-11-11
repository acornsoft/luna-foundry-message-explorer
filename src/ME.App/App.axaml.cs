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
    /// <summary>
    /// Set by a mobile head (Android) before start-up: the platform secret store behind the credential vault.
    /// Desktop heads leave it null and get the OS store for their platform.
    /// </summary>
    public static Func<Credentials.ISecretStore>? PlatformSecretStore { get; set; }

    /// <summary>
    /// Set by a desktop head before start-up: its microphone and speaker. Left null (Android, tests), the Voice panel
    /// takes typed questions only.
    /// </summary>
    public static Func<Voice.IVoiceAudio>? PlatformVoiceAudio { get; set; }

    private static int _iconsRegistered;

    public override void Initialize()
    {
        RegisterIcons();
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>Font Awesome Free icons for the quick view. Registered once per process (tests start many apps).</summary>
    public static void RegisterIcons()
    {
        if (Interlocked.Exchange(ref _iconsRegistered, 1) == 0)
            Projektanker.Icons.Avalonia.IconProvider.Current.Register<Projektanker.Icons.Avalonia.FontAwesome.FontAwesomeIconProvider>();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var samples = BundledSamples.Locate(AppContext.BaseDirectory);
            var options = StartupOptions.Parse(desktop.Args ?? [], Environment.GetEnvironmentVariable).WithDefaultCollection(samples);
            var accounts = options.SyntheticDemo ? AccountServices.SyntheticDemo() : AccountServices.ForThisComputer(Environment.GetEnvironmentVariable);
            var move = options.SyntheticDemo ? MoveServices.ForDemo(Demo.SyntheticDemoProvider.SharedBroker) : MoveServices.ForThisComputer(Environment.GetEnvironmentVariable);
            var quick = options.SyntheticDemo ? QuickViewServices.ForDemo(Demo.SyntheticDemoProvider.SharedBroker) : QuickViewServices.Default();
            var voice = VoiceServices.ForThisComputer(PlatformVoiceAudio);
            var main = new MainViewModel(MainViewModel.DefaultCatalog(options.SyntheticDemo), new DispatcherUiScheduler(), RunnerFactory(), samples, accounts, move, quick, voice, UiSettings(options.SyntheticDemo));
            desktop.Exit += (_, _) => (voice.Audio as IDisposable)?.Dispose();
            if (options.ExitAfterRunReport is { } report)
                ExitAfterRun(desktop, main.Jobs, report);
            options.Apply(main);
            desktop.MainWindow = options.PhoneView
                ? new Avalonia.Controls.Window { Title = "Message Explorer (phone view)", Width = 412, Height = 860, Content = new Views.MobileShellView { DataContext = main } }
                : new MainWindow { DataContext = main };
            _ = accounts.ImportLegacyAsync();
            _ = main.Accounts.RestoreAsync();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single)
        {
            // Mobile: saved logins and accounts only. No plugin processes, and Move mode is not offered.
            var accounts = AccountServices.ForThisComputer(Environment.GetEnvironmentVariable, PlatformSecretStore?.Invoke());
            var main = new MainViewModel(MainViewModel.DefaultCatalog(false), new DispatcherUiScheduler(), null, null, accounts, MoveServices.InMemory(), settings: UiSettings(false));
            single.MainView = new Views.MobileShellView { DataContext = main };
            _ = main.Accounts.RestoreAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Theme and recent connections, in ui-settings.json in the config folder. The synthetic demo keeps its recent
    /// list in memory (so made-up namespaces never land in the real file) but still remembers the theme.
    /// </summary>
    private static Settings.IUiSettingsStore UiSettings(bool syntheticDemo)
    {
        var file = new Settings.FileUiSettingsStore(Accounts.AppPaths.ConfigDirectory(Environment.GetEnvironmentVariable));
        return syntheticDemo ? new Settings.ThemeOnlyUiSettingsStore(file) : file;
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
