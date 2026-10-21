// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Redaction;

public static class Luhn
{
    public static bool IsValid(string digits)
    {
        if (digits.Length is < 13 or > 19)
            return false;

        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if ((uint)n > 9)
                return false;
            if (alternate)
            {
                n *= 2;
                if (n > 9)
                    n -= 9;
            }

            sum += n;
            alternate = !alternate;
        }

        return sum % 10 == 0;
    }

    public static char CheckDigit(string payloadWithoutCheck)
    {
        if (string.IsNullOrEmpty(payloadWithoutCheck))
            throw new ArgumentException("Payload is required.", nameof(payloadWithoutCheck));

        var sum = 0;
        var alternate = true;
        for (var i = payloadWithoutCheck.Length - 1; i >= 0; i--)
        {
            var n = payloadWithoutCheck[i] - '0';
            if ((uint)n > 9)
                throw new ArgumentException("Payload must be digits.", nameof(payloadWithoutCheck));
            if (alternate)
            {
                n *= 2;
                if (n > 9)
                    n -= 9;
            }

            sum += n;
            alternate = !alternate;
        }

        var mod = sum % 10;
        var check = mod == 0 ? 0 : 10 - mod;
        return (char)('0' + check);
    }
}
