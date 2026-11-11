// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Jobs.Bus;
using Acornsoft.MessageExplorer.Jobs.Hosting;
using Acornsoft.MessageExplorer.Jobs.Model;
using Acornsoft.MessageExplorer.Jobs.Running;

namespace Acornsoft.MessageExplorer.Jobs.Live;

/// <summary>
/// Generates a live run's messages once, before anything is sent. Only the job's generators start; they tick
/// over the simulated clock (instantly) and their messages are kept in timeline order. Simulators, assertions
/// and observers do not run: on a live bus nothing here consumes. Messages may go only to entities the job
/// names (target.send and each generator's send), never to a dead-letter subqueue.
/// </summary>
public static class LiveBatch
{
    public static async Task<IReadOnlyList<LiveMessage>> GenerateAsync(JobDefinition job, IPluginLauncher? launcher = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        launcher ??= new DefaultPluginLauncher();
        var allowed = new HashSet<string>(job.Plugins.Select(p => p.Send).OfType<string>().Append(job.TargetEntity), StringComparer.Ordinal);
        var planned = new List<(DateTimeOffset At, int Generator, int Order, LiveMessage Message)>();
        var generators = job.Plugins.Where(p => p.Roles.Contains(PluginRole.Generator)).ToList();
        var end = job.ClockStart + job.Duration;
        var order = 0;

        for (var g = 0; g < generators.Count; g++)
        {
            var binding = generators[g];
            await using var host = await launcher.LaunchAsync(binding, job, new InMemoryBus(), cancellationToken).ConfigureAwait(false);
            for (var now = job.ClockStart; ; now = now + job.Step > end ? end : now + job.Step)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var outbound in await host.TickAsync(now, cancellationToken).ConfigureAwait(false))
                {
                    var entity = outbound.To ?? binding.Send ?? job.TargetEntity;
                    if (entity.Contains("$deadletterqueue", StringComparison.OrdinalIgnoreCase) || !allowed.Contains(entity))
                        throw new LiveSendException($"Generator '{binding.Name}' sent to '{entity}', which this live job does not name. Nothing was sent.");
                    planned.Add((now.AddMilliseconds(outbound.DelayMs), g, order++, LiveMessage.Create(entity, outbound.CorrelationId, outbound.Body, binding.Name)));
                }

                if (now >= end)
                    break;
            }
        }

        return planned.OrderBy(p => p.At).ThenBy(p => p.Generator).ThenBy(p => p.Order).Select(p => p.Message).ToList();
    }
}
