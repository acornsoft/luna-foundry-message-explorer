// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;

namespace Acornsoft.MessageExplorer.Cli;

/// <summary>Tiny argument parser: two command words, then --name value options and bare --flags.</summary>
internal sealed record CliArgs(string Command, string? Subject, IReadOnlyDictionary<string, string> Options, IReadOnlySet<string> Flags)
{
    private static readonly HashSet<string> KnownFlags = new(StringComparer.Ordinal) { "yes", "dry-run", "send", "tcp", "help" };

    public static CliArgs Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var words = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "-h" or "--help")
            {
                flags.Add("help");
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                if (name.Length == 0)
                    throw new CliUsageException("Empty option name.");
                if (KnownFlags.Contains(name))
                {
                    flags.Add(name);
                    continue;
                }

                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new CliUsageException("--" + name + " needs a value.");
                options[name] = args[++i];
                continue;
            }

            words.Add(arg);
        }

        var command = words.Count >= 2 ? words[0] + " " + words[1] : words.Count == 1 ? words[0] : "";
        var subject = words.Count >= 3 ? words[2] : null;
        if (words.Count > 3)
            throw new CliUsageException("Too many words: " + string.Join(" ", words.Skip(3)));
        return new CliArgs(command, subject, options, flags);
    }

    public string Require(string name, string? environmentFallback = null)
    {
        if (Options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        if (environmentFallback is not null)
        {
            var env = Environment.GetEnvironmentVariable(environmentFallback);
            if (!string.IsNullOrWhiteSpace(env))
                return env;
        }

        throw new CliUsageException("--" + name + " is required" + (environmentFallback is null ? "." : " (or set " + environmentFallback + ")."));
    }

    public int Int(string name, int fallback)
    {
        if (!Options.TryGetValue(name, out var text))
            return fallback;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 1)
            throw new CliUsageException("--" + name + " must be a positive whole number.");
        return value;
    }

    public IReadOnlySet<long> Sequences()
    {
        if (!Options.TryGetValue("sequence", out var text))
            return new HashSet<long>();
        var result = new HashSet<long>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 1)
                throw new CliUsageException("--sequence takes comma-separated sequence numbers, for example 3,7,9.");
            result.Add(sequence);
        }

        return result;
    }
}

internal sealed class CliUsageException : Exception
{
    public CliUsageException(string message)
        : base(message)
    {
    }
}
