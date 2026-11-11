// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Shell;

public sealed class ShellModel
{
    private ShellModel(bool adminEnabled)
    {
        ShowAdminControls = adminEnabled;
    }

    public string Title => "Acornsoft Message Explorer";

    public string Banner => "Observe mode. This view does not receive, renew, defer, or settle messages.";

    public string DataPolicy => "Synthetic data only. Bodies are redacted before they are shown.";

    /// <summary>
    /// The one place the read-only rules are spelled out: the mode chip's tooltip while it reads "Read-only".
    /// </summary>
    public string ReadOnlyTip =>
        "Read-only. This app peeks: it never receives, renews, defers, settles or changes a message. " +
        "Counts come from runtime properties; each section peeks only when you open it. " +
        "Bodies and property values are masked before they are shown. Synthetic data only. " +
        "Click to switch to Move mode (off every time the app starts; each move asks you to confirm).";

    public string EmptyState => "No messages in this view yet.";

    public bool ShowAdminControls { get; }

    public IReadOnlyList<string> States { get; } = ["loading", "empty", "error", "offline", "read-only"];

    public static ShellModel Observe() => new(adminEnabled: false);

    public static ShellModel WithAdmin() => new(adminEnabled: true);
}
