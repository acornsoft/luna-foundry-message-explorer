// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Shell;

/// <summary>Keyboard hints in the platform's own words: ⌘F on macOS, Ctrl+F elsewhere.</summary>
public static class Shortcuts
{
    /// <summary>True on macOS, where the command key stands in for Ctrl.</summary>
    public static bool IsMac { get; } = OperatingSystem.IsMacOS();

    public static string Format(string key, bool mac) => mac ? "⌘" + key : "Ctrl+" + key;

    public static string Format(string key) => Format(key, IsMac);
}
