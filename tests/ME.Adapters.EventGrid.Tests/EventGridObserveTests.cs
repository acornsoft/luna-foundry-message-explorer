// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.EventGrid;
using Acornsoft.MessageExplorer.Model;

namespace Acornsoft.MessageExplorer.Adapters.EventGrid.Tests;

public class EventGridObserveTests
{
    [Fact]
    public async Task Observe_mode_refuses_a_read()
    {
        var adapter = new EventGridObserveAdapter();
        var refused = await Assert.ThrowsAsync<ReadRefusedException>(() => adapter.PeekAsync("disputes"));
        Assert.Contains("admin template", refused.Message, StringComparison.Ordinal);
        Assert.Contains("does not create", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_say_the_tap_is_a_write()
    {
        var adapter = new EventGridObserveAdapter();
        Assert.False(adapter.Capabilities.PeekSideEffectFree);
        Assert.False(adapter.Capabilities.TruePeek);
        Assert.True(adapter.Capabilities.WriteNeededToSetUp);
    }

    [Fact]
    public void Adapter_source_does_not_name_the_management_resource()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MessageExplorer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "ME.Adapters.EventGrid"), "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            Assert.DoesNotContain("Microsoft.EventGrid", text, StringComparison.Ordinal);
        }
    }
}
