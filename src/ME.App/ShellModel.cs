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

    public string EmptyState => "No messages in this view yet.";

    public bool ShowAdminControls { get; }

    public IReadOnlyList<string> States { get; } = ["loading", "empty", "error", "offline", "read-only"];

    public static ShellModel Observe() => new(adminEnabled: false);

    public static ShellModel WithAdmin() => new(adminEnabled: true);
}
