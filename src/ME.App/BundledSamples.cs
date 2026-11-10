// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell.ViewModels;

namespace Acornsoft.MessageExplorer.Shell;

/// <summary>
/// Sample test-job collections shipped with the app. In the macOS bundle they sit in
/// <c>Contents/Resources/samples/jobs</c>; elsewhere in <c>samples/jobs</c> next to the app.
/// </summary>
public static class BundledSamples
{
    /// <summary>Opened at startup when no collection is given. It needs no Python.</summary>
    public const string DefaultSample = "erp-standin";

    public static string? Locate(string appDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);
        string[] candidates =
        [
            Path.Combine(appDirectory, "..", "Resources", "samples", "jobs"),
            Path.Combine(appDirectory, "samples", "jobs"),
        ];
        return candidates.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
    }

    /// <summary>Folders under <paramref name="samplesFolder"/> that hold a collection.yaml, by name.</summary>
    public static IReadOnlyList<JobOption> List(string? samplesFolder) =>
        samplesFolder is null || !Directory.Exists(samplesFolder)
            ? []
            : Directory.GetDirectories(samplesFolder)
                .Where(d => File.Exists(Path.Combine(d, "collection.yaml")))
                .Order(StringComparer.Ordinal)
                .Select(d => new JobOption(Path.GetFileName(d), d))
                .ToList();
}
