// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Jobs.Live;

/// <summary>
/// The namespaces a live send may target, by name. Anything not listed is refused, including connection
/// strings and hosts outside servicebus.windows.net. The list is a local file kept outside the repo
/// (one namespace per line, # for comments); an empty or missing list refuses everything.
/// </summary>
public sealed class NamespaceAllowList
{
    public const string AzureSuffix = ".servicebus.windows.net";

    private readonly HashSet<string> _names;

    public NamespaceAllowList(IEnumerable<string> namespaces)
    {
        _names = new HashSet<string>(namespaces.Select(NameOf).OfType<string>(), StringComparer.OrdinalIgnoreCase);
        Namespaces = _names.Order(StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Namespaces { get; }

    /// <summary>Where the list lives by default: the user's config folder, never the repo.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "lfme", "live-namespaces.txt");

    public static NamespaceAllowList Load(string path)
    {
        if (!File.Exists(path))
            return new NamespaceAllowList([]);
        return new NamespaceAllowList(File.ReadAllLines(path)
            .Select(line => (line.IndexOf('#', StringComparison.Ordinal) is var hash and >= 0 ? line[..hash] : line).Trim())
            .Where(line => line.Length > 0));
    }

    public bool IsAllowed(string fullyQualifiedOrName) => NameOf(fullyQualifiedOrName) is { } name && _names.Contains(name);

    public void Check(string fullyQualifiedOrName)
    {
        if (!IsAllowed(fullyQualifiedOrName))
            throw new LiveSendException($"Namespace '{Shown(fullyQualifiedOrName)}' is not on the live allow-list. Nothing was sent.");
    }

    /// <summary>The fully qualified host for a listed name.</summary>
    public static string FullyQualified(string fullyQualifiedOrName) =>
        (NameOf(fullyQualifiedOrName) ?? throw new LiveSendException($"'{Shown(fullyQualifiedOrName)}' is not a Service Bus namespace name.")) + AzureSuffix;

    /// <summary>The bare namespace name, or null for anything that is not a plain name or a servicebus.windows.net host.</summary>
    private static string? NameOf(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        if (text.EndsWith(AzureSuffix, StringComparison.Ordinal))
            text = text[..^AzureSuffix.Length];
        return text.Length is >= 6 and <= 50 && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') && char.IsAsciiLetter(text[0]) ? text : null;
    }

    private static string Shown(string value) => value.Contains('=', StringComparison.Ordinal) ? "(a connection string; not shown)" : value.Trim();
}
