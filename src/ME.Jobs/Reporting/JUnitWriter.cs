// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using System.Xml;
using Acornsoft.MessageExplorer.Jobs.Protocol;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Jobs.Reporting;

/// <summary>
/// JUnit XML for CI. Times are simulated seconds, and there is no wall-clock timestamp or host name,
/// so the same job and seed give byte-identical output.
/// </summary>
public static class JUnitWriter
{
    public static string Write(RunResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var job = result.Job;
        return Build(writer =>
        {
            writer.WriteStartElement("testsuites");
            writer.WriteAttributeString("name", "lfme-jobs");
            writer.WriteAttributeString("tests", Count(result.Records.Count));
            writer.WriteAttributeString("failures", Count(result.Failed));
            writer.WriteAttributeString("errors", "0");

            writer.WriteStartElement("testsuite");
            writer.WriteAttributeString("name", job.SuiteName);
            writer.WriteAttributeString("tests", Count(result.Records.Count));
            writer.WriteAttributeString("failures", Count(result.Failed));
            writer.WriteAttributeString("errors", "0");
            writer.WriteAttributeString("skipped", "0");
            writer.WriteAttributeString("time", Seconds(result.SimulatedEnd - job.ClockStart));

            writer.WriteStartElement("properties");
            Property(writer, "runId", job.RunId);
            Property(writer, "seed", job.Seed.ToString(CultureInfo.InvariantCulture));
            Property(writer, "environment", job.EnvironmentName);
            Property(writer, "clockStart", PluginProtocol.FormatTime(job.ClockStart));
            Property(writer, "time", "simulated seconds");
            Property(writer, "sent", Count(result.SentCount));
            Property(writer, "busMessages", result.BusMessages.ToString(CultureInfo.InvariantCulture));
            foreach (var (name, version) in result.PluginVersions)
                Property(writer, "plugin." + name, version);
            foreach (var (name, count) in result.Events)
                Property(writer, "events." + name, Count(count));
            writer.WriteEndElement();

            foreach (var record in result.Records)
            {
                writer.WriteStartElement("testcase");
                writer.WriteAttributeString("classname", job.SuiteName);
                writer.WriteAttributeString("name", record.CorrelationId);
                writer.WriteAttributeString("time", Seconds(record.SimulatedDuration));
                if (record.Verdict.Status != VerdictStatus.Pass)
                {
                    writer.WriteStartElement("failure");
                    writer.WriteAttributeString("type", "assertion");
                    writer.WriteAttributeString("message", record.Verdict.Reason);
                    writer.WriteString(Describe(record));
                    writer.WriteEndElement();
                }

                writer.WriteElementString("system-out", Describe(record));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
        });
    }

    /// <summary>A report for a run that could not finish, for example when a plugin crashed.</summary>
    public static string WriteError(string suiteName, string message) => Build(writer =>
    {
        writer.WriteStartElement("testsuites");
        writer.WriteAttributeString("name", "lfme-jobs");
        writer.WriteAttributeString("tests", "1");
        writer.WriteAttributeString("failures", "0");
        writer.WriteAttributeString("errors", "1");
        writer.WriteStartElement("testsuite");
        writer.WriteAttributeString("name", suiteName);
        writer.WriteAttributeString("tests", "1");
        writer.WriteAttributeString("failures", "0");
        writer.WriteAttributeString("errors", "1");
        writer.WriteAttributeString("skipped", "0");
        writer.WriteAttributeString("time", "0");
        writer.WriteStartElement("testcase");
        writer.WriteAttributeString("classname", suiteName);
        writer.WriteAttributeString("name", "run");
        writer.WriteAttributeString("time", "0");
        writer.WriteStartElement("error");
        writer.WriteAttributeString("type", "run");
        writer.WriteAttributeString("message", message);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    });

    public static string Describe(CorrelationRecord record)
    {
        var builder = new StringBuilder();
        builder.Append("sent ").Append(PluginProtocol.FormatTime(record.SentAt)).Append('\n');
        foreach (var expectation in record.Expected)
            builder.Append("expected: ").Append(expectation["description"]?.ToString() ?? expectation.ToJsonString()).Append('\n');
        foreach (var observation in record.Actual)
        {
            builder.Append("actual: ").Append(PluginProtocol.FormatTime(observation.At)).Append(' ').Append(observation.Sink);
            if (observation.Body["code"] is { } code)
                builder.Append(" code=").Append(code.ToString());
            builder.Append('\n');
        }

        builder.Append("verdict: ").Append(record.Verdict.Status.ToString().ToLowerInvariant());
        if (record.Verdict.Reason.Length > 0)
            builder.Append(" (").Append(record.Verdict.Reason).Append(')');
        return builder.ToString();
    }

    private static string Build(Action<XmlWriter> write)
    {
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "  ", Encoding = new UTF8Encoding(false), NewLineChars = "\n" };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartDocument();
            write(writer);
            writer.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void Property(XmlWriter writer, string name, string value)
    {
        writer.WriteStartElement("property");
        writer.WriteAttributeString("name", name);
        writer.WriteAttributeString("value", value);
        writer.WriteEndElement();
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
