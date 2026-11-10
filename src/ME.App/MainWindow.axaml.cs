// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Avalonia.Controls;

namespace Acornsoft.MessageExplorer.Shell;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var shell = ShellModel.Observe();
        Title = shell.Title;
        BannerText.Text = shell.Banner;
        PolicyText.Text = shell.DataPolicy;
        EmptyText.Text = shell.EmptyState;
        AdminPanel.IsVisible = shell.ShowAdminControls;
    }
}
