// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.ComponentModel;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        // A narrow main pane (Voice open, a small window) stacks the lane cards 2 by 2.
        var cards = this.FindControl<ItemsControl>("QuickLaneCards");
        if (cards is not null)
            cards.LayoutUpdated += (_, _) => ApplyCardGrid(cards);
    }

    /// <summary>Below this main-pane width, more than two lane cards wrap to two rows.</summary>
    public const double NarrowCardsWidth = 720;

    /// <summary>Rows and columns for the lane cards: one row when wide, 2 by 2 when narrow (with more than two cards).</summary>
    public static (int Rows, int Columns) CardGrid(double width, int cards) =>
        width < NarrowCardsWidth && cards > 2 ? (2, 2) : (1, Math.Max(cards, 1));

    private static void ApplyCardGrid(ItemsControl cards)
    {
        if (cards.ItemsPanelRoot is not UniformGrid grid || cards.Parent is not Control pane)
            return;
        var (rows, columns) = CardGrid(pane.Bounds.Width, cards.ItemCount);
        if (grid.Rows != rows)
            grid.Rows = rows;
        if (grid.Columns != columns)
            grid.Columns = columns;
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
