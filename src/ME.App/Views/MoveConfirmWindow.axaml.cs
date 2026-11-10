// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Operations.Move;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Acornsoft.MessageExplorer.Shell.Views;

/// <summary>The per-drop confirm dialog. "Don't move" is the default and Escape; moving needs a click on the move button.</summary>
public partial class MoveConfirmWindow : Window
{
    public MoveConfirmWindow()
    {
        InitializeComponent();
    }

    public MoveConfirmWindow(MovePlan plan)
        : this()
    {
        ArgumentNullException.ThrowIfNull(plan);
        ConfirmTitle.Text = plan.Title;
        ConfirmFrom.Text = plan.FromText;
        ConfirmTo.Text = plan.ToText;
        ConfirmHow.Text = plan.HowText;
        ConfirmOrder.Text = plan.OrderText;
        ConfirmWarnings.ItemsSource = plan.Warnings;
        ConfirmMoveButton.Content = $"Move {plan.Count} message{(plan.Count == 1 ? string.Empty : "s")}";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnMove(object? sender, RoutedEventArgs e) => Close(true);
}

/// <summary>Shows <see cref="MoveConfirmWindow"/> over the main window.</summary>
public sealed class WindowMoveConfirmer(Window owner) : IMoveConfirmer
{
    public async Task<bool> ConfirmAsync(MovePlan plan) =>
        await new MoveConfirmWindow(plan).ShowDialog<bool>(owner).ConfigureAwait(true);
}
