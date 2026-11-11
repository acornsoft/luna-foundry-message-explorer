// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Shell;
using Acornsoft.MessageExplorer.Voice.Audio;
using Acornsoft.MessageExplorer.Voice.PortAudio;

namespace Acornsoft.MessageExplorer.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The packaged app has no separate dotnet; plugin processes are this executable with --run-plugin.
        if (args.Length >= 2 && args[0] == DotnetPluginCommand.RunPluginSwitch)
            return PluginSelfHost.Run(args[1], args[2..]);
        // Microphone and speaker for the Voice tab. Devices open on first push-to-talk, not here.
        App.PlatformVoiceAudio = () => new PcmVoiceAudio(new PortAudioBackend());
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
