// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Jobs.Loading;

/// <summary>One or more problems in a job, collection, or environment file. Each line names where and what.</summary>
public sealed class JobValidationException : Exception
{
    public JobValidationException(string fileName, IReadOnlyList<string> errors)
        : base(Format(fileName, errors))
    {
        FileName = fileName;
        Errors = errors;
    }

    public string FileName { get; }

    public IReadOnlyList<string> Errors { get; }

    private static string Format(string fileName, IReadOnlyList<string> errors) =>
        $"{fileName} has {errors.Count} problem{(errors.Count == 1 ? string.Empty : "s")}:" + string.Concat(errors.Select(error => $"{Environment.NewLine}  - {error}"));
}
