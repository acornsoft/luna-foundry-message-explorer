// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.ViewModels;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Acornsoft.MessageExplorer.Shell;

/// <summary>Posts to the Avalonia UI thread at background priority, so input stays responsive during a run.</summary>
public sealed class DispatcherUiScheduler : IUiScheduler
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
}

/// <summary>The window's folder and save pickers. Only local paths are used.</summary>
public sealed class WindowJobsDialogs(TopLevel owner) : IJobsDialogs
{
    public async Task<string?> PickCollectionFolderAsync()
    {
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a test-job collection folder (with collection.yaml)",
            AllowMultiple = false,
        }).ConfigureAwait(true);
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    public async Task<string?> PickJUnitPathAsync(string suggestedFileName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export JUnit XML",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "xml",
            FileTypeChoices = [new FilePickerFileType("JUnit XML") { Patterns = ["*.xml"] }],
        }).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }
}
