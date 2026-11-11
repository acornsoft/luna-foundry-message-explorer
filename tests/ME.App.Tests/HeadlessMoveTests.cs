// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Operations.Move;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Shell.Views;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public class HeadlessMoveTests
{
    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"No visible {typeof(T).Name} named {name}.");

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task The_mode_chip_starts_read_only_and_the_move_banner_shows_only_while_on()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync(turnOn: false);
        var window = new MainWindow { DataContext = main };
        window.Show();
        Pump(window);

        var chip = Find<ToggleButton>(window, "ModeChip");
        Assert.False(chip.IsChecked);
        Assert.Equal("Read-only", Find<TextBlock>(window, "ModeChipText").Text);
        Assert.False(window.FindControl<Border>("MoveModeBanner")!.IsEffectivelyVisible);

        chip.IsChecked = true;
        Pump(window);

        Assert.True(main.MoveModeOn);
        Assert.True(window.FindControl<Border>("MoveModeBanner")!.IsEffectivelyVisible);
        Assert.Equal(main.MoveBannerText, window.FindControl<TextBlock>("MoveModeBannerText")!.Text);
        Assert.StartsWith("Move mode is on for ", main.MoveBannerText, StringComparison.Ordinal);
        Assert.Equal("Move", Find<TextBlock>(window, "ModeChipText").Text);
    }

    [AvaloniaFact]
    public async Task The_move_workspace_explains_the_off_state_and_shows_the_tree_and_list_when_on()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync(turnOn: false);
        var window = new MainWindow { DataContext = main };
        window.Show();
        Pump(window);

        // Off: the Move workspace isn't shown at all; the resources view is.
        Assert.False(window.FindControl<DockPanel>("MoveWorkspace")!.IsVisible);
        Assert.False(main.Move.CanWork);

        main.MoveModeOn = true;
        await main.Move.TreeLoad;
        await MoveDemo.SelectAsync(main, "orders/$deadletterqueue");
        Pump(window);

        Assert.False(Find<TextBlock>(window, "MoveOffText").IsVisible);
        Assert.True(Find<TreeView>(window, "MoveTree").IsEffectivelyEnabled);
        Assert.True(DragDrop.GetAllowDrop(Find<TreeView>(window, "MoveTree")));
        var list = Find<ListBox>(window, "MoveMessageList");
        Assert.Equal(4, list.ItemCount);
        Assert.Equal(SelectionMode.Multiple | SelectionMode.Toggle, list.SelectionMode);
        var moveTo = Find<Button>(window, "MoveToButton");
        Assert.Equal(new KeyGesture(Key.M, KeyModifiers.Control), moveTo.HotKey);
        Assert.True(moveTo.IsEffectivelyEnabled);

        list.SelectedItems!.Add(main.Move.Messages[0]);
        list.SelectedItems.Add(main.Move.Messages[2]);
        Pump(window);
        Assert.Equal(2, main.Move.SelectedMessages.Count);
        Assert.Equal("2 picked. Dropping moves only these, oldest first.", Find<TextBlock>(window, "SelectionText").Text);
    }

    [AvaloniaFact]
    public async Task The_confirm_dialog_shows_the_plan_and_defaults_to_not_moving()
    {
        var (main, _, _, _) = await MoveDemo.OpenAsync();
        var plan = await new MoveEngine(main.MoveMode, new MemoryMoveAuditLog()).PrepareAsync(
            new MoveRequest(MoveEndpoint.Subscription("sbns-demo-001", "order-events", "billing", true), MoveEndpoint.TopicOf("sbns-demo-001", "order-events"), MoveSelection.OldestFirst(2)),
            new Operations.InMemory.InMemoryMoveTransport(Demo.SyntheticDemoProvider.CreateBroker()));

        var dialog = new MoveConfirmWindow(plan);
        dialog.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Move 2 messages?", dialog.FindControl<TextBlock>("ConfirmTitle")!.Text);
        Assert.Equal("From: dead-letter queue of subscription billing of topic order-events in sbns-demo-001", dialog.FindControl<TextBlock>("ConfirmFrom")!.Text);
        Assert.Equal("To: topic order-events in sbns-demo-001", dialog.FindControl<TextBlock>("ConfirmTo")!.Text);
        Assert.Contains("one transaction per message", dialog.FindControl<TextBlock>("ConfirmHow")!.Text, StringComparison.Ordinal);
        Assert.Contains(plan.Warnings, w => w.Contains("Every subscription", StringComparison.Ordinal));
        var cancel = dialog.FindControl<Button>("ConfirmCancelButton")!;
        Assert.True(cancel.IsDefault);
        Assert.True(cancel.IsCancel);
        Assert.Equal("Move 2 messages", dialog.FindControl<Button>("ConfirmMoveButton")!.Content);
        dialog.Close();
    }
}
