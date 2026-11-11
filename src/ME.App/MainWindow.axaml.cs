// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Ctrl+F (Cmd+F on a Mac) jumps to the search box; Esc closes the topmost sheet or panel.</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel main)
            return;
        if (e.Key == Key.F && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            main.CloseTopmost();
            ResourceSearchBox.Focus();
            ResourceSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (ResourceSearchBox.IsFocused && !string.IsNullOrEmpty(ResourceSearchBox.Text))
            {
                ResourceSearchBox.Text = string.Empty;
                e.Handled = true;
                return;
            }

            e.Handled = main.CloseTopmost();
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel main)
            main.CloseTopmost();
    }

    private void OnCloseVoice(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel main)
            main.IsVoiceOpen = false;
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
}
