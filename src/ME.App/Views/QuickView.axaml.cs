// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Acornsoft.MessageExplorer.Shell.Views;

public partial class QuickView : UserControl
{
    private QuickViewViewModel? _model;
    private QuickEntityViewModel? _entity;

    public QuickView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => Bind(DataContext as QuickViewViewModel);
    }

    private void Bind(QuickViewViewModel? model)
    {
        if (_model is not null)
            _model.PropertyChanged -= OnModelChanged;
        _model = model;
        if (model is null)
            return;
        model.PropertyChanged += OnModelChanged;
        // The copy icons put only the redacted text on the clipboard.
        model.CopyText = text => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
        Watch(model.Entity);
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuickViewViewModel.Entity))
            Watch(_model?.Entity);
    }

    private void Watch(QuickEntityViewModel? entity)
    {
        if (_entity is not null)
            _entity.PropertyChanged -= OnEntityChanged;
        _entity = entity;
        if (entity is not null)
            entity.PropertyChanged += OnEntityChanged;
    }

    /// <summary>A lane card was clicked: scroll its section into view once it has laid out.</summary>
    private void OnEntityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(QuickEntityViewModel.Focused) || _entity?.Focused is not { } lane)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            var sections = this.FindControl<ItemsControl>("QuickSections");
            var index = _entity?.Lanes.ToList().IndexOf(lane) ?? -1;
            if (sections?.ContainerFromIndex(index) is Control container)
                container.BringIntoView();
        }, DispatcherPriority.Background);
    }

    private void OnCloseDrawer(object? sender, RoutedEventArgs e)
    {
        if (_model?.Entity is { } entity)
            entity.Selected = null;
    }
}
