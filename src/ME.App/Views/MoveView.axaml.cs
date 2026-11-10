// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Acornsoft.MessageExplorer.Shell.Views;

/// <summary>
/// Drag and drop for the Move tab. Dragging picked messages (or a source node) starts an in-app drag; the
/// tree highlights each queue or topic the drop is allowed on, and a drop hands the target to the view model,
/// which checks, asks and moves. Only an in-app marker travels with the drag, never message data.
/// </summary>
public partial class MoveView : UserControl
{
    private static readonly DataFormat<string> MoveFormat = DataFormat.CreateStringApplicationFormat("lfme-move");
    private const double DragThreshold = 6;
    private Point? _pressedAt;
    private Control? _pressedOn;
    private MoveTreeNode? _highlighted;

    public MoveView()
    {
        InitializeComponent();
        MoveMessageList.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        MoveMessageList.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        MoveTree.AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
        MoveTree.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel);
        MoveTree.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        MoveTree.AddHandler(DragDrop.DragLeaveEvent, (_, _) => Highlight(null));
        MoveTree.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MoveViewModel? ViewModel => DataContext as MoveViewModel;

    private void OnMessagesSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ViewModel?.SetSelectedMessages(MoveMessageList.SelectedItems?.OfType<MoveMessageRow>() ?? []);

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _pressedAt = e.GetPosition(this);
        _pressedOn = sender as Control;
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedAt is not { } start || ViewModel is not { } vm || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < DragThreshold && Math.Abs(delta.Y) < DragThreshold)
            return;
        _pressedAt = null;

        bool started;
        if (ReferenceEquals(_pressedOn, MoveMessageList))
        {
            var row = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as MoveMessageRow;
            var picked = vm.SelectedMessages.Count > 0 ? vm.SelectedMessages : row is null ? [] : new[] { row };
            started = picked.Count > 0 && vm.BeginDrag(vm.SelectedSource, picked);
        }
        else
        {
            started = vm.BeginDrag(NodeUnder(e.Source), null);
        }

        if (!started)
            return;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(MoveFormat, "lfme-move"));
        await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move).ConfigureAwait(true);
        Highlight(null);
        vm.EndDrag();
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var node = NodeUnder(e.Source);
        var allowed = e.DataTransfer.Contains(MoveFormat) && ViewModel?.CanDropOn(node) == true;
        e.DragEffects = allowed ? DragDropEffects.Move : DragDropEffects.None;
        Highlight(allowed ? node : null);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var node = NodeUnder(e.Source);
        Highlight(null);
        if (ViewModel is not { } vm || node is null || !e.DataTransfer.Contains(MoveFormat) || !vm.CanDropOn(node))
            return;
        e.Handled = true;
        await vm.DropAsync(node).ConfigureAwait(true);
    }

    private void Highlight(MoveTreeNode? node)
    {
        if (ReferenceEquals(_highlighted, node))
            return;
        if (_highlighted is not null)
            _highlighted.IsDropTarget = false;
        _highlighted = node;
        if (node is not null)
            node.IsDropTarget = true;
    }

    private static MoveTreeNode? NodeUnder(object? source) =>
        (source as Control)?.FindAncestorOfType<TreeViewItem>(includeSelf: true)?.DataContext as MoveTreeNode;
}
