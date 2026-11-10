// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acornsoft.MessageExplorer.Shell.Views;

public partial class LoginsView : UserControl
{
    public LoginsView()
    {
        InitializeComponent();
        SecretGuard.Attach(SecretBox);
    }
}

/// <summary>Stops a masked secret box from putting its text on the clipboard.</summary>
public static class SecretGuard
{
    public static void Attach(TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        box.CopyingToClipboard += Block;
        box.CuttingToClipboard += Block;
    }

    private static void Block(object? sender, RoutedEventArgs e) => e.Handled = true;
}
