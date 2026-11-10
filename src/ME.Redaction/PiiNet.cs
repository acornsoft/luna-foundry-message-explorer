// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using System.Text.RegularExpressions;

namespace Acornsoft.MessageExplorer.Redaction;

public static partial class PiiNet
{
    public static string Scrub(string input)
    {
        var value = Email().Replace(input, "[redacted]");
        value = Ssn().Replace(value, "[redacted]");
        value = Phone().Replace(value, "[redacted]");
        value = Icd().Replace(value, "[redacted]");
        value = BareDate().Replace(value, "[redacted]");
        value = ScrubCards(value);
        value = Canary().Replace(value, "[redacted]");
        return value;
    }

    public static string ScrubLiterals(string input, IReadOnlyList<string> secrets)
    {
        var result = input;
        foreach (var secret in secrets.Distinct(StringComparer.Ordinal).OrderByDescending(static s => s.Length))
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 8)
                continue;

            result = ReplaceInsensitive(result, secret, "[redacted]");
            var compact = Compact(secret);
            if (compact.Length >= 8 && !compact.Equals(secret, StringComparison.OrdinalIgnoreCase))
                result = ReplaceInsensitive(result, compact, "[redacted]");

            var digits = DigitsOnly(secret);
            if (digits.Length >= 8 && !digits.Equals(secret, StringComparison.Ordinal))
                result = ReplaceInsensitive(result, digits, "[redacted]");
            if (digits.Length is >= 13 and <= 19)
            {
                result = ReplaceInsensitive(result, Group(digits, " "), "[redacted]");
                result = ReplaceInsensitive(result, Group(digits, "-"), "[redacted]");
            }
        }

        return result;
    }

    internal static string Compact(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is ' ' or '-' or '(' or ')')
                continue;
            builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string DigitsOnly(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsDigit(ch))
                builder.Append(ch);
        }

        return builder.ToString();
    }

    private static string Group(string digits, string separator)
    {
        var parts = new List<string>();
        for (var i = 0; i < digits.Length; i += 4)
            parts.Add(digits.Substring(i, Math.Min(4, digits.Length - i)));
        return string.Join(separator, parts);
    }

    private static string ReplaceInsensitive(string input, string search, string replacement)
    {
        var builder = new StringBuilder(input.Length);
        var index = 0;
        while (index < input.Length)
        {
            var found = input.IndexOf(search, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                builder.Append(input, index, input.Length - index);
                break;
            }

            builder.Append(input, index, found - index);
            builder.Append(replacement);
            index = found + search.Length;
        }

        return builder.ToString();
    }

    private static string ScrubCards(string input)
    {
        var result = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            if (!char.IsDigit(input[i]))
            {
                result.Append(input[i]);
                i++;
                continue;
            }

            var start = i;
            var digits = new StringBuilder();
            var j = i;
            while (j < input.Length)
            {
                var ch = input[j];
                if (char.IsDigit(ch))
                {
                    digits.Append(ch);
                    j++;
                    continue;
                }

                if (ch is ' ' or '-' && digits.Length > 0 && j + 1 < input.Length && char.IsDigit(input[j + 1]))
                {
                    j++;
                    continue;
                }

                break;
            }

            var digitText = digits.ToString();
            var boundedLeft = start == 0 || !char.IsDigit(input[start - 1]);
            var boundedRight = j >= input.Length || !char.IsDigit(input[j]);
            if (boundedLeft && boundedRight && digitText.Length is >= 13 and <= 19 && Luhn.IsValid(digitText))
            {
                result.Append("[redacted]");
                i = j;
                continue;
            }

            result.Append(input[i]);
            i++;
        }

        return result.ToString();
    }

    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ssn();

    [GeneratedRegex(@"(?<!\d)(?:\+?1[\s.\-]?)?\(?\d{3}\)?[\s.\-]\d{3}[\s.\-]\d{4}(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();

    [GeneratedRegex(@"\b[A-TV-Z][0-9][0-9A-Z](?:\.[0-9A-Z]{1,4})?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Icd();

    [GeneratedRegex(@"\b(?:19|20)\d{2}-\d{2}-\d{2}\b(?!T)", RegexOptions.CultureInvariant)]
    private static partial Regex BareDate();

    [GeneratedRegex(@"SYNTHETIC-[A-Za-z0-9\-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex Canary();
}
