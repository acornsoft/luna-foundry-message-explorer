// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

public sealed class QueueRowViewModel
{
    public QueueRowViewModel(EntitySnapshot snapshot)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public EntitySnapshot Snapshot { get; }

    public string Name => Snapshot.Name;

    public long? Active => Snapshot.Counts.Active;

    public long? DeadLetter => Snapshot.Counts.DeadLetter;

    public long? Scheduled => Snapshot.Counts.Scheduled;

    public long? TransferDeadLetter => Snapshot.Counts.TransferDeadLetter;

    public string Status => Snapshot.Status ?? "Unknown";

    public long? SizeInBytes => Snapshot.SizeInBytes;

    public string ActiveText => Count(Active);

    public string DeadLetterText => Count(DeadLetter);

    public string ScheduledText => Count(Scheduled);

    public string TransferDeadLetterText => Count(TransferDeadLetter);

    public string SizeText => SizeInBytes switch
    {
        null => "-",
        < 1024 => $"{SizeInBytes.Value.ToString(CultureInfo.InvariantCulture)} B",
        < 1024 * 1024 => $"{(SizeInBytes.Value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB",
        _ => $"{(SizeInBytes.Value / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB",
    };

    /// <summary>True when the dead-letter or transfer dead-letter count is above zero. The grid highlights these.</summary>
    public bool HasDeadLetters => Snapshot.Counts.HasDeadLetters;

    /// <summary>Highlights the Dead-letter cell. True only when that count is above zero.</summary>
    public bool DeadLetterAboveZero => DeadLetter > 0;

    /// <summary>Highlights the Transfer DLQ cell. True only when that count is above zero.</summary>
    public bool TransferDeadLetterAboveZero => TransferDeadLetter > 0;

    private static string Count(long? value) => value?.ToString("N0", CultureInfo.InvariantCulture) ?? "-";
}
