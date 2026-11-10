// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Jobs.Bus;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Jobs.Tests;

internal static class Repo
{
    public static string Root { get; } = FindRoot();

    public static string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public static string ErpJob => Path("samples", "jobs", "erp-standin", "jobs", "erp-day.job.yaml");

    public static string PythonJob => Path("samples", "jobs", "python-hello", "jobs", "hello.job.yaml");

    public static string PythonPlugin => Path("samples", "plugins", "python", "hello_generator.py");

    public static string ErpDll => Path("artifacts", "plugins", "ErpStandin", "ErpStandin.dll");

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

/// <summary>Runs only when python3 is on the PATH. CI on ubuntu has it.</summary>
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

/// <summary>A scratch folder with job files, removed after the test.</summary>
internal sealed class TempCollection : IDisposable
{
    public TempCollection()
    {
        Root = Directory.CreateTempSubdirectory("lfme-jobs-").FullName;
        Directory.CreateDirectory(System.IO.Path.Combine(Root, "environments"));
        Directory.CreateDirectory(System.IO.Path.Combine(Root, "jobs"));
    }

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
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>An in-process plugin built from delegates, for fast runner tests.</summary>
internal sealed class FakePlugin : InProcessPlugin
{
    public FakePlugin(string name, JsonObject? capabilities = null, params PluginRole[] roles)
    {
        Info = new PluginInfo(name, "1.0.0", PluginProtocol.Version, roles.ToHashSet(), capabilities ?? new JsonObject());
    }

    public override PluginInfo Info { get; }

    public Func<DateTimeOffset, IReadOnlyList<OutboundMessage>>? OnTick { get; init; }

    public Func<BusMessage, DateTimeOffset, IReadOnlyList<OutboundMessage>>? OnDeliver { get; init; }

    public Func<BusMessage, IReadOnlyList<JsonObject>>? OnExpect { get; init; }

    public Func<string, IReadOnlyList<Observation>, Verdict>? OnAssert { get; init; }

    public bool Disposed { get; private set; }

    public override Task<IReadOnlyList<OutboundMessage>> TickAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        OnTick is null ? base.TickAsync(now, cancellationToken) : Task.FromResult(OnTick(now));

    public override Task<IReadOnlyList<OutboundMessage>> DeliverAsync(BusMessage message, DateTimeOffset now, CancellationToken cancellationToken) =>
        OnDeliver is null ? base.DeliverAsync(message, now, cancellationToken) : Task.FromResult(OnDeliver(message, now));

    public override Task<IReadOnlyList<JsonObject>> ExpectAsync(BusMessage sent, CancellationToken cancellationToken) =>
        OnExpect is null ? base.ExpectAsync(sent, cancellationToken) : Task.FromResult(OnExpect(sent));

    public override Task<Verdict> AssertAsync(string correlationId, IReadOnlyList<JsonObject> expected, IReadOnlyList<Observation> actual, CancellationToken cancellationToken) =>
        OnAssert is null ? base.AssertAsync(correlationId, expected, actual, cancellationToken) : Task.FromResult(OnAssert(correlationId, actual));

    public override ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Launches fakes by plugin name; built-ins still come from <see cref="BuiltinPlugins"/>.</summary>
internal sealed class FakeLauncher(Dictionary<string, Func<IPluginHost>> fakes) : IPluginLauncher
{
    public Task<IPluginHost> LaunchAsync(PluginBinding binding, JobDefinition job, InMemoryBus bus, CancellationToken cancellationToken) =>
        Task.FromResult(binding.Builtin ? BuiltinPlugins.Create(binding, bus, job.TargetEntity) : fakes[binding.Name]());
}
