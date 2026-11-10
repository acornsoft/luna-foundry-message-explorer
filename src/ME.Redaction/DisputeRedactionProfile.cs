// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Redaction;

public static class DisputeRedactionProfile
{
    public const string ProfileId = "dispute";

    public static RedactionPipeline Create(ProjectTokenKey key, string salt)
    {
        RedactionRule[] rules =
        [
            new("member_name", FieldAction.Mask),
            new("email", FieldAction.Mask),
            new("phone", FieldAction.Mask),
            new("ssn", FieldAction.Mask),
            new("card_number", FieldAction.Mask),
            new("birth_date", FieldAction.Mask),
            new("diagnosis_code", FieldAction.Mask),
            new("loyalty_code", FieldAction.Hash),
            new("account_ref", FieldAction.Tokenize),
            new("case_id", FieldAction.Tokenize),
            new("maiden_name", FieldAction.Drop),
            new("notes", FieldAction.Keep, FreeText: true),
            new("subject", FieldAction.Keep, FreeText: true),
            new("status", FieldAction.Keep),
            new("message_type", FieldAction.Keep),
            new("occurred_at", FieldAction.Keep),
            new("entity", FieldAction.Keep),
        ];

        return new RedactionPipeline(new RedactionProfile(ProfileId, key, salt, rules));
    }
}
