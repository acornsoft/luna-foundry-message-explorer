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
    }

    /// <summary>Header clicks sort in the view model, so the order is the same one the tests check.</summary>
    private void OnQueuesGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is not MainViewModel main)
            return;
        if (Enum.TryParse<QueueSortColumn>(e.Column.SortMemberPath, out var column))
        {
            main.Queues.Sort(column);
            e.Handled = true;
        }
    }
}
