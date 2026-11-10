// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Model;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Correlation;

public static class CorrelationKeys
{
    public static readonly string[] AllowList = ["account_ref", "case_id"];
}

public static class EnvelopeComposer
{
    public static CloudEventEnvelope Compose(
        string id,
        string source,
        DateTimeOffset time,
        RedactionResult redacted,
        string? traceParentHeader)
    {
        ArgumentNullException.ThrowIfNull(redacted);
        var extensions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["redactionprofile"] = "dispute",
            ["envtag"] = "unspecified",
        };

        if (TraceParent.TryParse(traceParentHeader, out var trace) && trace is not null)
            extensions["traceparent"] = trace.ToHeader();

        return new CloudEventEnvelope(
            id,
            source,
            "com.acornsoft.messageexplorer.observed.v1",
            time,
            subject: redacted.Fields.TryGetValue("subject", out var subject) ? subject : null,
            extensions,
            redacted.Fields);
    }
}
