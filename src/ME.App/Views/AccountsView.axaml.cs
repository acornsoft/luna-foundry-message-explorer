// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;

namespace Acornsoft.MessageExplorer.Shell.Views;

public partial class AccountsView : UserControl
{
    public AccountsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AccountsViewModel accounts && accounts.Clipboard is null)
            {
                accounts.Clipboard = async text =>
                {
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                        await clipboard.SetTextAsync(text).ConfigureAwait(true);
                };
            }
        };
    }

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AccountsViewModel accounts && accounts.OpenCommand.CanExecute(null))
            accounts.OpenCommand.Execute(null);
    }
}
