// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Correlation;

public sealed record TraceParent(string Version, string TraceId, string ParentId, string Flags)
{
    public string ToHeader() => Version + "-" + TraceId + "-" + ParentId + "-" + Flags;

    public static bool TryParse(string? value, out TraceParent? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split('-');
        if (parts.Length != 4)
            return false;
        if (parts[0] != "00")
            return false;
        if (!IsHex(parts[1], 32) || IsAllZero(parts[1]))
            return false;
        if (!IsHex(parts[2], 16) || IsAllZero(parts[2]))
            return false;
        if (!IsHex(parts[3], 2))
            return false;

        parsed = new TraceParent(
            parts[0],
            parts[1].ToLowerInvariant(),
            parts[2].ToLowerInvariant(),
            parts[3].ToLowerInvariant());
        return true;
    }

    private static bool IsHex(string value, int length)
    {
        if (value.Length != length)
            return false;
        foreach (var ch in value)
        {
            var hex = ch is >= '0' and <= '9' || ch is >= 'a' and <= 'f' || ch is >= 'A' and <= 'F';
            if (!hex)
                return false;
        }

        return true;
    }

    private static bool IsAllZero(string value)
    {
        foreach (var ch in value)
        {
            if (ch != '0')
                return false;
        }

        return true;
    }
}
