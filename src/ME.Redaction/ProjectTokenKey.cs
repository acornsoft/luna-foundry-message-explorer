// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Redaction;

public sealed class ProjectTokenKey
{
    public ProjectTokenKey(string projectId, ReadOnlyMemory<byte> material)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new ArgumentException("Project id is required.", nameof(projectId));
        if (material.Length < 16)
            throw new ArgumentException("Token key material must be at least 16 bytes.", nameof(material));

        ProjectId = projectId;
        Material = material.ToArray();
    }

    public string ProjectId { get; }
    public byte[] Material { get; }

    public override string ToString() => ProjectId;
}
