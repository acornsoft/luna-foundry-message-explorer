// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Acornsoft.MessageExplorer.Operations.Move;

/// <summary>
/// One line per event, append-only. Entries carry IDs, endpoints, counts and outcomes. There is no field for a
/// body, an application property, a connection string, a key or a token, so none can be written.
/// </summary>
public sealed record MoveAuditEntry(
    string Event,
    DateTimeOffset At,
    string MoveId,
    string? Actor = null,
    string? Source = null,
    string? Target = null,
    string? Route = null,
    string? Mode = null,
    int? Requested = null,
    long? SequenceNumber = null,
    string? MessageId = null,
    string? SessionId = null,
    string? Outcome = null,
    int? Moved = null,
    int? Released = null,
    string? PossibleDuplicateMessageId = null,
    string? Reason = null);

public interface IMoveAuditLog
{
    void Write(MoveAuditEntry entry);
}

public sealed class MemoryMoveAuditLog : IMoveAuditLog
{
    private readonly List<MoveAuditEntry> _entries = [];

    public IReadOnlyList<MoveAuditEntry> Entries
    {
        get
        {
            lock (_entries)
                return _entries.ToList();
        }
    }

    public void Write(MoveAuditEntry entry)
    {
        lock (_entries)
            _entries.Add(entry);
    }
}

/// <summary>JSON Lines in a file only the user can read (0600 on macOS and Linux). Written by hand, so it trims safely.</summary>
public sealed class JsonlMoveAuditLog(string path) : IMoveAuditLog
{
    private static readonly object Gate = new();

    public string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

    public void Write(MoveAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = Serialize(entry) + "\n";
        lock (Gate)
        {
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);
            var isNew = !File.Exists(Path);
            File.AppendAllText(Path, line, new UTF8Encoding(false));
            if (isNew && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static string Serialize(MoveAuditEntry entry)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("event", entry.Event);
            json.WriteString("at", entry.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            json.WriteString("moveId", entry.MoveId);
            Optional(json, "actor", entry.Actor);
            Optional(json, "source", entry.Source);
            Optional(json, "target", entry.Target);
            Optional(json, "route", entry.Route);
            Optional(json, "mode", entry.Mode);
            if (entry.Requested is { } requested)
                json.WriteNumber("requested", requested);
            if (entry.SequenceNumber is { } sequence)
                json.WriteNumber("sequenceNumber", sequence);
            Optional(json, "messageId", entry.MessageId);
            Optional(json, "sessionId", entry.SessionId);
            Optional(json, "outcome", entry.Outcome);
            if (entry.Moved is { } moved)
                json.WriteNumber("moved", moved);
            if (entry.Released is { } released)
                json.WriteNumber("released", released);
            Optional(json, "possibleDuplicateMessageId", entry.PossibleDuplicateMessageId);
            Optional(json, "reason", entry.Reason);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Optional(Utf8JsonWriter json, string name, string? value)
    {
        if (value is not null)
            json.WriteString(name, value);
    }
}
