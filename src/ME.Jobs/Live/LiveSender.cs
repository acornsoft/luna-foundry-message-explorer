// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;

namespace Acornsoft.MessageExplorer.Jobs.Live;

/// <summary>
/// Hands generated messages to a sink, one at a time, under the run's limits: the hard cap, rate.perMinute on
/// the wall clock, the TTL on every message, and the stop switch (cancellation, Ctrl+C in the CLI, or a stop
/// file). A dry run (the default) sends nothing and reports what would go. Only sends: nothing here receives.
/// </summary>
public sealed class LiveSender(ILiveClock? clock = null)
{
    private readonly ILiveClock _clock = clock ?? new SystemLiveClock();

    public async Task<LiveSendReport> SendAsync(
        IReadOnlyList<LiveMessage> messages,
        IMessageSink sink,
        LiveSendOptions options,
        TextWriter? log = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        var problems = options.Problems();
        if (problems.Count > 0)
            throw new LiveSendException("The live send was refused: " + string.Join("; ", problems));

        var counts = new Dictionary<(string Entity, MessageKind Kind), int>();
        void Count(LiveMessage message) => counts[(message.Entity, message.Kind)] = counts.GetValueOrDefault((message.Entity, message.Kind)) + 1;

        if (options.DryRun)
        {
            foreach (var message in messages.Take(options.MaxMessages))
                Count(message);
            var dry = new LiveSendReport(messages.Count, 0, true, messages.Count > options.MaxMessages ? LiveStopReason.CapReached : LiveStopReason.Completed, counts);
            await WriteAsync(log, dry, sink.Name).ConfigureAwait(false);
            return dry;
        }

        var limiter = new RateLimiter(options.PerMinute, _clock);
        var sent = 0;
        var reason = LiveStopReason.Completed;
        try
        {
            foreach (var message in messages)
            {
                if (sent >= options.MaxMessages)
                {
                    reason = LiveStopReason.CapReached;
                    break;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    reason = LiveStopReason.Stopped;
                    break;
                }

                if (options.StopFile is { } stop && File.Exists(stop))
                {
                    reason = LiveStopReason.StopFile;
                    break;
                }

                await limiter.WaitTurnAsync(cancellationToken).ConfigureAwait(false);
                await sink.SendAsync(message, options.TimeToLive, cancellationToken).ConfigureAwait(false);
                sent++;
                Count(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reason = LiveStopReason.Stopped;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Stop at the first broker or network failure and still report what went. Only the first line of the
            // message is logged: SDK messages can run long, and nothing here holds a secret to leak.
            reason = LiveStopReason.Failed;
            var first = exception.Message.Split('\n', 2)[0].TrimEnd('\r');
            if (log is not null)
                await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Send failed after {sent}: {exception.GetType().Name}: {first}")).ConfigureAwait(false);
        }

        var report = new LiveSendReport(messages.Count, sent, false, reason, counts);
        await WriteAsync(log, report, sink.Name).ConfigureAwait(false);
        return report;
    }

    private static async Task WriteAsync(TextWriter? log, LiveSendReport report, string sink)
    {
        if (log is null)
            return;
        var verb = report.DryRun ? "would send" : "sent";
        var total = report.Counts.Values.Sum();
        await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"{(report.DryRun ? "DRY RUN: " : string.Empty)}{verb} {total} of {report.Planned} to {sink} ({report.StopReason}).")).ConfigureAwait(false);
        foreach (var entity in report.Counts.Keys.Select(k => k.Entity).Distinct().Order(StringComparer.Ordinal))
        {
            await log.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"  {entity}: {report.Count(entity, MessageKind.Good)} good, {report.Count(entity, MessageKind.Poison)} poison")).ConfigureAwait(false);
        }
    }
}
