// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Credentials;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>
/// One row of the switcher button's quick switch: the namespace on top, "account · cloud" below, and a check
/// when it is the namespace open now. Opens either a recent namespace or a saved login. Holds no secret.
/// </summary>
public sealed record QuickSwitchItem(string Title, string Detail, bool IsCurrent, string Icon)
{
    /// <summary>The recent entry this row opens, if it came from Recent.</summary>
    public RecentItemViewModel? Recent { get; init; }

    /// <summary>The saved login this row connects, if it came from Saved.</summary>
    public LoginEntry? Login { get; init; }

    /// <summary>The saved login's own name, for the tooltip.</summary>
    public string? LoginName { get; init; }

    /// <summary>The spoken name: "Open sbns-demo-001, alice@contoso.example · Azure public, open now".</summary>
    public string Name => $"Open {Title}, {Detail}{(IsCurrent ? ", open now" : string.Empty)}";

    /// <summary>The tooltip: the saved login's name when there is one, else the row's two lines.</summary>
    public string Tip => LoginName is { Length: > 0 } name ? $"{name}: {Title} ({Detail})" : $"{Title} ({Detail})";
}
