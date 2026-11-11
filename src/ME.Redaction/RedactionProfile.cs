// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;

namespace Acornsoft.MessageExplorer.Redaction;

public enum FieldAction
{
    Mask,
    Hash,
    Tokenize,
    Drop,
    Keep,
}

public sealed record RedactionRule(string Field, FieldAction Action, bool FreeText = false);

public sealed class RedactionProfile
{
    private readonly Dictionary<string, RedactionRule> _rules;
    private readonly byte[] _saltBytes;

    public RedactionProfile(string profileId, ProjectTokenKey key, string salt, IEnumerable<RedactionRule> rules)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            throw new ArgumentException("Profile id is required.", nameof(profileId));
        ArgumentNullException.ThrowIfNull(key);
        if (string.IsNullOrWhiteSpace(salt) || salt.Length < 16)
            throw new ArgumentException("Salt must be at least 16 characters.", nameof(salt));
        ArgumentNullException.ThrowIfNull(rules);

        ProfileId = profileId;
        Key = key;
        _saltBytes = Encoding.UTF8.GetBytes(salt);
        _rules = new Dictionary<string, RedactionRule>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Field))
                throw new ArgumentException("A rule needs a field name.", nameof(rules));
            _rules[rule.Field] = rule;
        }
    }

    public string ProfileId { get; }
    public ProjectTokenKey Key { get; }

    public bool TryGetRule(string field, out RedactionRule rule) => _rules.TryGetValue(field, out rule!);

    public string Mask(string field) => "[masked:" + field + "]";

    public string Hash(string value)
    {
        var payload = new byte[_saltBytes.Length + 1 + Encoding.UTF8.GetByteCount(value)];
        Buffer.BlockCopy(_saltBytes, 0, payload, 0, _saltBytes.Length);
        var written = Encoding.UTF8.GetBytes(value, payload.AsSpan(_saltBytes.Length + 1));
        if (written != Encoding.UTF8.GetByteCount(value))
            throw new InvalidOperationException("Hash input length changed.");
        var hash = SHA256.HashData(payload);
        return "hash_" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    public string Tokenize(string value)
    {
        using var hmac = new HMACSHA256(Key.Material);
        var prefix = Encoding.UTF8.GetBytes(Key.ProjectId + "\0" + ProfileId + "\0");
        var body = Encoding.UTF8.GetBytes(value);
        var input = new byte[prefix.Length + body.Length];
        Buffer.BlockCopy(prefix, 0, input, 0, prefix.Length);
        Buffer.BlockCopy(body, 0, input, prefix.Length, body.Length);
        var mac = hmac.ComputeHash(input);
        return "tok_" + Base64Url(mac);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
