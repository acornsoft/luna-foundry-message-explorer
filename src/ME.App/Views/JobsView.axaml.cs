// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Acornsoft.MessageExplorer.Shell.Views;

public partial class JobsView : UserControl
{
    public static readonly IValueConverter FilterLabel =
        new FuncValueConverter<LiveFilter, string>(LiveRunViewModel.FilterLabel);

    public JobsView()
    {
        InitializeComponent();
    }
}
