// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Redaction.Tests;

// The redaction property harness: made-up people, cards and codes planted in every field, with the spellings
// a leak could take. Shared with the quick view tests (linked into ME.App.Tests).
internal sealed class SyntheticSample
{
    public required Dictionary<string, string> Fields { get; init; }
    public required List<string> Variants { get; init; }
    public required string AccountRef { get; init; }
    public required string UnknownField { get; init; }
}

internal static class SyntheticMessageFactory
{
    public static SyntheticSample Create(int seed)
    {
        var safeSeed = seed == int.MinValue ? 0 : seed;
        var rng = new Random(safeSeed);
        var stamp = Math.Abs(safeSeed % 90000) + 10000;
        var name = "Soren" + rng.Next(10000, 99999).ToString() + " Pell" + rng.Next(10000, 99999).ToString();
        var email = "person" + stamp.ToString() + "@example.test";
        var phone = "+1-202-555-" + rng.Next(0, 10000).ToString("0000");
        var area = 200 + Math.Abs(safeSeed % 400);
        if (area == 666)
            area = 665;
        var group = 10 + Math.Abs(safeSeed % 80);
        var serial = 1000 + Math.Abs(safeSeed / 3 % 8000);
        var ssn = area.ToString("000") + "-" + group.ToString("00") + "-" + serial.ToString("0000");
        var payload = new char[15];
        payload[0] = '4';
        for (var i = 1; i < payload.Length; i++)
            payload[i] = (char)('0' + rng.Next(0, 10));
        var payloadText = new string(payload);
        var card = payloadText + Luhn.CheckDigit(payloadText);
        var dob = "19" + (70 + Math.Abs(safeSeed % 20)).ToString("00") + "-06-17";
        var diagnosis = "Z" + rng.Next(10, 100).ToString("00") + "." + rng.Next(1, 10).ToString();
        var loyalty = "LOYAL" + stamp.ToString() + "CODE";
        var account = "ACCT" + stamp.ToString() + "REF";
        var caseId = "CASE" + stamp.ToString() + "ID";
        var maiden = "Maiden" + stamp.ToString() + "Name";
        var unknown = "LockerSecret" + stamp.ToString();
        var secrets = new[] { name, email, phone, ssn, card, dob, diagnosis, loyalty, account, caseId, maiden, unknown };
        var notes = "Note " + string.Join(" ", secrets) + " card " + Group(card);

        var fields = new Dictionary<string, string>
        {
            ["member_name"] = name,
            ["email"] = email,
            ["phone"] = phone,
            ["ssn"] = ssn,
            ["card_number"] = card,
            ["birth_date"] = dob,
            ["diagnosis_code"] = diagnosis,
            ["loyalty_code"] = loyalty,
            ["account_ref"] = account,
            ["case_id"] = caseId,
            ["maiden_name"] = maiden,
            ["notes"] = notes,
            ["subject"] = "Question from " + email,
            ["status"] = "open",
            ["message_type"] = "reg-e-dispute",
            ["occurred_at"] = "2026-03-04T15:04:05Z",
            ["entity"] = "disputes/case-sync",
            ["locker_note"] = unknown,
        };

        var variants = new List<string>();
        foreach (var secret in secrets)
            AddVariants(variants, secret);
        AddVariants(variants, Group(card));
        AddVariants(variants, Group(card, "-"));

        return new SyntheticSample
        {
            Fields = fields,
            Variants = variants,
            AccountRef = account,
            UnknownField = "locker_note",
        };
    }

    private static void AddVariants(List<string> variants, string secret)
    {
        if (secret.Length < 8)
            return;
        variants.Add(secret);
        var compact = new string(secret.Where(ch => ch is not (' ' or '-' or '(' or ')')).ToArray());
        if (compact.Length >= 8 && !compact.Equals(secret, StringComparison.OrdinalIgnoreCase))
            variants.Add(compact);
    }

    private static string Group(string digits, string separator = " ")
    {
        var parts = new List<string>();
        for (var i = 0; i < digits.Length; i += 4)
            parts.Add(digits.Substring(i, Math.Min(4, digits.Length - i)));
        return string.Join(separator, parts);
    }
}
