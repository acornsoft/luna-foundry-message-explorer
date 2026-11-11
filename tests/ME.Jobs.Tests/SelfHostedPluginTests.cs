// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Loading;
using Acornsoft.MessageExplorer.Jobs.Reporting;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Jobs.Tests;

public sealed class SelfHostedPluginTests
{
    [Fact]
    public void A_dotnet_dll_command_runs_through_the_app_itself()
    {
        Assert.Equal(
            ["/Applications/LFME.app/Contents/MacOS/lfme", "--run-plugin", "../plugins/Erp.dll", "--verbose"],
            DotnetPluginCommand.RunWithSelf(["dotnet", "../plugins/Erp.dll", "--verbose"], "/Applications/LFME.app/Contents/MacOS/lfme"));
    }

    [Theory]
    [InlineData("python3", "feed.py")]
    [InlineData("dotnet", "run")]
    [InlineData("./tool", "x.dll")]
    public void Other_commands_are_left_alone(string program, string argument)
    {
        IReadOnlyList<string> command = [program, argument];
        Assert.Same(command, DotnetPluginCommand.RunWithSelf(command, "/self"));
    }

    [Theory]
    [InlineData("/usr/share/dotnet/dotnet", null)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", null)]
    [InlineData(null, null)]
    [InlineData("/Applications/LFME.app/Contents/MacOS/Acornsoft.MessageExplorer.Desktop", "/Applications/LFME.app/Contents/MacOS/Acornsoft.MessageExplorer.Desktop")]
    public void The_app_hosts_plugins_only_when_it_runs_from_its_own_executable(string? processPath, string? expected)
    {
        Assert.Equal(expected, DotnetPluginCommand.SelfHostPath(processPath));
    }

    [Fact]
    public async Task The_host_uses_the_command_resolver_and_gives_the_same_result()
    {
        var seen = new List<string>();
        var options = new PluginHostOptions
        {
            CommandResolver = command =>
            {
                seen.Add(string.Join(' ', command.Select(Path.GetFileName)));
                return command;
            },
        };
        var job = JobLoader.Load(Repo.ErpJob, null, _ => null);

        var resolved = await new JobRunner(new DefaultPluginLauncher(options)).RunAsync(job);
        var plain = await new JobRunner().RunAsync(job);

        Assert.Equal(["dotnet OrderGenerator.dll", "dotnet ErpStandin.dll"], seen);
        Assert.Equal(JUnitWriter.Write(plain), JUnitWriter.Write(resolved));
    }
}
