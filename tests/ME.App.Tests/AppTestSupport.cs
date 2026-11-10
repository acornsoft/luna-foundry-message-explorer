// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;

namespace Acornsoft.MessageExplorer.Shell.Tests;

internal static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public static string CrewStock => Path("samples", "jobs", "crew-stock");

    public static string CrewStockJob => Path("samples", "jobs", "crew-stock", "jobs", "shift-sync.job.yaml");

    public static string ErpStandin => Path("samples", "jobs", "erp-standin");

    /// <summary>The desktop head built in the same configuration as the tests.</summary>
    public static string DesktopDll => Path("src", "ME.App.Desktop", "bin", Configuration, "net8.0", "Acornsoft.MessageExplorer.Desktop.dll");

    private static string Configuration =>
        typeof(Repo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .OfType<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "MessageExplorer.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("MessageExplorer.sln was not found above the test output.");
    }
}

/// <summary>Runs only when python3 is on the PATH (the crew-stock sample has a Python plugin). CI on ubuntu has it.</summary>
public sealed class PythonFactAttribute : FactAttribute
{
    public static readonly bool Available = Probe();

    public PythonFactAttribute()
    {
        if (!Available)
            Skip = "python3 is not on the PATH.";
    }

    private static bool Probe()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("python3", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (process is null)
                return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>Collects posted actions so a test can see how many UI updates a run would cost.</summary>
internal sealed class ManualUiScheduler : ViewModels.IUiScheduler
{
    public List<Action> Posted { get; } = [];

    public void Post(Action action) => Posted.Add(action);

    public int RunAll()
    {
        var count = 0;
        while (Posted.Count > 0)
        {
            var action = Posted[0];
            Posted.RemoveAt(0);
            action();
            count++;
        }

        return count;
    }
}

internal sealed class FakeDialogs(string? folder, string? junitPath) : ViewModels.IJobsDialogs
{
    public string? SuggestedName { get; private set; }

    public string? StartFolder { get; private set; }

    public Task<string?> PickCollectionFolderAsync(string? startFolder)
    {
        StartFolder = startFolder;
        return Task.FromResult(folder);
    }

    public Task<string?> PickJUnitPathAsync(string suggestedFileName)
    {
        SuggestedName = suggestedFileName;
        return Task.FromResult(junitPath);
    }
}

internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Root = Directory.CreateTempSubdirectory("lfme-app-").FullName;

    public string Root { get; }

    public string Write(string relative, string text)
    {
        var path = System.IO.Path.Combine(Root, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, true);
        }
        catch (IOException)
        {
        }
    }
}
