// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Shell;

namespace Acornsoft.MessageExplorer.Shell.Tests;

public class ShellModelTests
{
    [Fact]
    public void Observe_mode_hides_admin_controls()
    {
        var shell = ShellModel.Observe();
        Assert.False(shell.ShowAdminControls);
        Assert.Contains("Observe mode", shell.Banner, StringComparison.Ordinal);
        Assert.Contains("Synthetic data only", shell.DataPolicy, StringComparison.Ordinal);
        Assert.Equal("Acornsoft Message Explorer", shell.Title);
    }

    [Fact]
    public void Admin_shell_shows_the_separate_controls()
    {
        Assert.True(ShellModel.WithAdmin().ShowAdminControls);
    }

    [Fact]
    public void The_same_states_are_named()
    {
        Assert.Equal(["loading", "empty", "error", "offline", "read-only"], ShellModel.Observe().States);
    }
}
