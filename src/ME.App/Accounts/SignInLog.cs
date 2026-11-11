// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>
/// A local, append-only record of each sign-in attempt and how it ended, so a failure can be read after the
/// fact. Never holds a token, code, key or connection string: every line goes through <see cref="LogScrubber"/>.
/// </summary>
public interface ISignInLog
{
    /// <summary>Where the log is, for the error message. Null when it is kept in memory.</summary>
    string? Location { get; }

    void Write(string message);
}

/// <summary>Writes to logs/signin.log in the app's config folder (~/Library/Application Support/LunaFoundryMessageExplorer on macOS).</summary>
public sealed class FileSignInLog : ISignInLog
{
    public const string FileName = "signin.log";
    private const long MaxBytes = 1024 * 1024;
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _now;

    public FileSignInLog(string configDirectory, Func<DateTimeOffset>? now = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(configDirectory);
        Location = Path.Combine(configDirectory, "logs", FileName);
        _now = now ?? (() => DateTimeOffset.Now);
    }

    public string Location { get; }

    public void Write(string message)
    {
        var line = _now().ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture) + " " + LogScrubber.Scrub(message).Replace('\n', ' ').Replace('\r', ' ') + "\n";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
                if (File.Exists(Location) && new FileInfo(Location).Length > MaxBytes)
                    File.Move(Location, Location + ".1", overwrite: true);
                File.AppendAllText(Location, line, new UTF8Encoding(false));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop sign-in. The UI still shows the error.
                System.Diagnostics.Trace.WriteLine("lfme sign-in log not written: " + exception.GetType().Name);
            }
        }
    }
}

/// <summary>Sign-in log lines kept in memory (tests, the synthetic demo).</summary>
public sealed class MemorySignInLog : ISignInLog
{
    public List<string> Lines { get; } = [];

    public string? Location => null;

    public void Write(string message)
    {
        lock (Lines)
            Lines.Add(LogScrubber.Scrub(message));
    }
}

/// <summary>Removes anything that could be a credential before a line is logged or shown.</summary>
public static partial class LogScrubber
{
    public static string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var value = Jwt().Replace(text, "[token]");
        value = Bearer().Replace(value, "Bearer [token]");
        value = SecretPair().Replace(value, m => m.Groups[1].Value + "=[removed]");
        value = QueryString().Replace(value, "?[query removed]");
        return value;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]{4,}\.[A-Za-z0-9_\-]*", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"Bearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"\b(code|access_token|refresh_token|id_token|client_secret|password|sig|SharedAccess(?:Key|Signature)|AccountKey)\s*=\s*[^;&\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretPair();

    [GeneratedRegex(@"\?[^\s""']*=[^\s""']*", RegexOptions.CultureInvariant)]
    private static partial Regex QueryString();
}
