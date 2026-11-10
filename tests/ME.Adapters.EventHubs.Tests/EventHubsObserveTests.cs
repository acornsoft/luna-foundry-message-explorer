// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Adapters.EventHubs;
using Acornsoft.MessageExplorer.Model;
using Acornsoft.MessageExplorer.Redaction;

namespace Acornsoft.MessageExplorer.Adapters.EventHubs.Tests;

public class EventHubsObserveTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Latest_read_does_not_replay_history_or_write_a_checkpoint()
    {
        var log = LogWith("1", "2");
        var reader = Reader(log, EventHubsObserveOptions.Default);

        var first = await reader.ReadAsync("0");
        log.Append("3", ObservedAt);
        var second = await reader.ReadAsync("0");

        Assert.Empty(first);
        Assert.Equal(["3"], second.Select(message => message.Position.Value).ToArray());
        Assert.Equal(0, log.CheckpointCalls);
        Assert.Equal(0, log.EpochCalls);
        Assert.Null(log.CommittedOffset);
        Assert.Equal("3", reader.Positions["0"]);
    }

    [Fact]
    public async Task Earliest_read_is_capped_and_keeps_the_cursor_in_memory()
    {
        var log = new SyntheticHubLog();
        for (var offset = 1; offset <= 150; offset++)
            log.Append(offset.ToString(), ObservedAt.AddMinutes(offset));
        var reader = Reader(log, new EventHubsObserveOptions(
            EventHubsObserveReader.ConsumerGroup,
            null,
            false,
            EventHubsStart.Earliest));

        var first = await reader.ReadAsync("0");
        var second = await reader.ReadAsync("0");

        Assert.Equal(100, first.Count);
        Assert.Equal(50, second.Count);
        Assert.Equal(100, log.LastRequestedMax);
        Assert.Equal(0, log.CheckpointCalls);
        Assert.Null(log.CommittedOffset);
        Assert.Equal("150", reader.Positions["0"]);
    }

    [Fact]
    public async Task Timestamp_lookback_skips_older_events_without_a_checkpoint()
    {
        var log = new SyntheticHubLog();
        log.Append("1", ObservedAt.AddMinutes(-10));
        log.Append("2", ObservedAt.AddMinutes(-5));
        log.Append("3", ObservedAt);
        var reader = Reader(log, new EventHubsObserveOptions(
            EventHubsObserveReader.ConsumerGroup,
            null,
            false,
            EventHubsStart.AtTimestamp(ObservedAt.AddMinutes(-6))));

        var read = await reader.ReadAsync("0");

        Assert.Equal(["2", "3"], read.Select(message => message.Position.Value).ToArray());
        Assert.Equal(0, log.CheckpointCalls);
    }

    [Fact]
    public void Owner_level_and_checkpoint_store_are_refused()
    {
        var log = new SyntheticHubLog();
        Assert.Throws<ObserveModeViolationException>(() => Reader(log, new EventHubsObserveOptions(
            EventHubsObserveReader.ConsumerGroup, 1, false, EventHubsStart.Latest)));
        Assert.Throws<ObserveModeViolationException>(() => Reader(log, new EventHubsObserveOptions(
            EventHubsObserveReader.ConsumerGroup, null, true, EventHubsStart.Latest)));
        Assert.Throws<ReadRefusedException>(() => Reader(log, new EventHubsObserveOptions(
            "$Default", null, false, EventHubsStart.Latest)));
        Assert.Equal(0, log.CheckpointCalls);
        Assert.Equal(0, log.EpochCalls);
    }

    [Fact]
    public void Readers_share_one_group_name()
    {
        Assert.Equal("cg-message-explorer", EventHubsObserveReader.ConsumerGroup);
        Assert.Equal(EventHubsObserveReader.ConsumerGroup, EventHubsObserveOptions.Default.ConsumerGroup);
        Assert.Null(EventHubsObserveOptions.Default.OwnerLevel);
        Assert.False(EventHubsObserveOptions.Default.UsesCheckpointStore);
        Assert.Equal(EventHubsStartKind.Latest, EventHubsObserveOptions.Default.Start.Kind);
    }

    [Fact]
    public void Adapter_source_does_not_name_processor_or_checkpoint_apis()
    {
        var forbidden = new[] { "EventProcessorClient", "BlobCheckpointStore", "UpdateCheckpoint" };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "MessageExplorer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "ME.Adapters.EventHubs"), "*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    private static EventHubsObserveReader Reader(SyntheticHubLog log, EventHubsObserveOptions options)
    {
        var material = Encoding.UTF8.GetBytes("synthetic-key-eventhubs-0001");
        var pipeline = DisputeRedactionProfile.Create(new ProjectTokenKey("eventhubs", material), "synthetic-salt-dispute");
        return new EventHubsObserveReader(options, log, pipeline, ObservedAt);
    }

    private static SyntheticHubLog LogWith(params string[] offsets)
    {
        var log = new SyntheticHubLog();
        foreach (var offset in offsets)
            log.Append(offset, ObservedAt.AddMinutes(int.Parse(offset, System.Globalization.CultureInfo.InvariantCulture)));
        return log;
    }
}

internal sealed class SyntheticHubLog : IEventHubPartitionLog
{
    private readonly List<EventHubFrame> _frames = [];

    public int CheckpointCalls { get; private set; }
    public int EpochCalls { get; private set; }
    public string? CommittedOffset { get; private set; }
    public int LastRequestedMax { get; private set; }

    public void Append(string offset, DateTimeOffset enqueuedAt)
    {
        _frames.Add(new EventHubFrame(offset, enqueuedAt, null, null, new Dictionary<string, string> { ["status"] = "open" }));
    }

    public void Checkpoint(string offset)
    {
        CheckpointCalls++;
        CommittedOffset = offset;
    }

    public void AcquireEpoch() => EpochCalls++;

    public string TailOffset(string partitionId)
    {
        return _frames.Count == 0 ? "0" : _frames[^1].Offset;
    }

    public IReadOnlyList<EventHubFrame> ReadForward(string partitionId, string fromOffsetExclusive, int max)
    {
        LastRequestedMax = max;
        return _frames
            .Where(frame => After(frame.Offset, fromOffsetExclusive))
            .Take(max)
            .ToList();
    }

    private static bool After(string offset, string cursor)
    {
        if (cursor.Length == 0)
            return true;
        if (long.TryParse(offset, out var left) && long.TryParse(cursor, out var right))
            return left > right;
        return string.Compare(offset, cursor, StringComparison.Ordinal) > 0;
    }
}
