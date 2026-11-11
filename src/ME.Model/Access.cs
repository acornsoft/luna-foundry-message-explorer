// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Model;

public static class Roles
{
    public const string Viewer = "viewer";
    public const string ExplorerAdmin = "explorer-admin";
    public const string ProfileOwner = "profile-owner";
}

public static class Scopes
{
    public const string ReadMetadata = "explorer.read.metadata";
    public const string ReadBodies = "explorer.read.bodies";
    public const string AdminRequest = "explorer.admin.request";
}

public sealed class Caller
{
    public Caller(string id, IEnumerable<string> roles, IEnumerable<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Caller id is required.", nameof(id));
        Id = id;
        Roles = new HashSet<string>(roles, StringComparer.Ordinal);
        Scopes = new HashSet<string>(scopes, StringComparer.Ordinal);
    }

    public string Id { get; }
    public IReadOnlySet<string> Roles { get; }
    public IReadOnlySet<string> Scopes { get; }

    public bool HasRole(string role) => Roles.Contains(role);
    public bool HasScope(string scope) => Scopes.Contains(scope);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
