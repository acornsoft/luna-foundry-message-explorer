// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Acornsoft.MessageExplorer.Shell.Views;

/// <summary>The Voice panel. The talk button sends audio only while it is held.</summary>
public partial class VoiceView : UserControl
{
    public VoiceView()
    {
        InitializeComponent();
        VoiceTalk.AddHandler(PointerPressedEvent, async (_, _) => await (ViewModel?.TalkPressedAsync() ?? Task.CompletedTask), RoutingStrategies.Tunnel);
        VoiceTalk.AddHandler(PointerReleasedEvent, async (_, _) => await (ViewModel?.TalkReleasedAsync() ?? Task.CompletedTask), RoutingStrategies.Tunnel);
        VoiceTalk.AddHandler(PointerCaptureLostEvent, async (_, _) => await (ViewModel?.TalkReleasedAsync() ?? Task.CompletedTask));
    }

    private VoiceViewModel? ViewModel => DataContext as VoiceViewModel;
}
