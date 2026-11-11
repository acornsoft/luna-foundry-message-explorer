// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;

namespace Acornsoft.MessageExplorer.Shell;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Views.SecretGuard.Attach(ConnectionStringBox);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel main && main.Jobs.Dialogs is null)
                main.Jobs.Dialogs = new WindowJobsDialogs(this);
            if (DataContext is MainViewModel withMove && withMove.Move.Confirmer is null)
                withMove.Move.Confirmer = new Views.WindowMoveConfirmer(this);
        };
    }

    private void OnRecentDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is MainViewModel main && main.Sources.OpenRecentCommand.CanExecute(null))
            main.Sources.OpenRecentCommand.Execute(null);
    }

    private void OnSavedDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is MainViewModel main && main.Connect.ConnectSavedCommand.CanExecute(null))
            main.Connect.ConnectSavedCommand.Execute(null);
    }

    private void OnBrowseDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (DataContext is MainViewModel main && main.Sources.OpenCommand.CanExecute(null))
            main.Sources.OpenCommand.Execute(null);
    }
}
