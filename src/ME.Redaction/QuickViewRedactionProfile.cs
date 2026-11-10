// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;

namespace Acornsoft.MessageExplorer.Redaction;

/// <summary>
/// The quick view's default profile, for any broker and any entity. It keeps only message metadata (scrubbed
/// by the pattern net) and drops every body field and application property value: their names are listed as
/// unclassified, their values never reach a surface, and they are scrubbed out of the kept metadata too.
/// </summary>
public static class QuickViewRedactionProfile
{
    public const string ProfileId = "quickview-default";

    /// <summary>Prefix for a body field name passed to <see cref="RedactionPipeline.Redact"/>.</summary>
    public const string BodyPrefix = "body.";

    /// <summary>Prefix for an application property name passed to <see cref="RedactionPipeline.Redact"/>.</summary>
    public const string PropertyPrefix = "prop.";

    public static RedactionPipeline Create(ProjectTokenKey key, string salt)
    {
        RedactionRule[] rules =
        [
            new("subject", FieldAction.Keep, FreeText: true),
            new("dlq_reason", FieldAction.Keep, FreeText: true),
            new("dlq_description", FieldAction.Keep, FreeText: true),
            new("message_id", FieldAction.Keep),
            new("correlation_id", FieldAction.Keep),
            new("session_id", FieldAction.Keep),
            new("content_type", FieldAction.Keep),
        ];

        return new RedactionPipeline(new RedactionProfile(ProfileId, key, salt, rules));
    }

    /// <summary>A pipeline with a fresh random key and salt held in memory for one window: tokens never join across runs.</summary>
    public static RedactionPipeline ForThisSession()
    {
        var material = RandomNumberGenerator.GetBytes(32);
        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        return Create(new ProjectTokenKey("quickview-session", material), salt);
    }
}
