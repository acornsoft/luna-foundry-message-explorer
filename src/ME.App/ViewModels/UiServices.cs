// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>Runs work on the UI thread. The app posts to the Avalonia dispatcher; tests run inline.</summary>
public interface IUiScheduler
{
    void Post(Action action);
}

/// <summary>Runs every action at once on the calling thread. For view-model tests and the CLI comparison.</summary>
public sealed class InlineUiScheduler : IUiScheduler
{
    public static InlineUiScheduler Instance { get; } = new();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}

/// <summary>File and folder pickers. The window supplies the real ones; tests supply answers.</summary>
public interface IJobsDialogs
{
    /// <summary>Pick a collection folder, starting in <paramref name="startFolder"/> when given.</summary>
    Task<string?> PickCollectionFolderAsync(string? startFolder);

    Task<string?> PickJUnitPathAsync(string suggestedFileName);
}
