// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Redaction;
using Acornsoft.MessageExplorer.Shell.Demo;
using Acornsoft.MessageExplorer.Shell.QuickView;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>The quick view's cursor rules: top N per page, one cursor per sub-queue, peek only.</summary>
public class QuickPeekSessionTests
{
    private static readonly BrowseEntity Orders = BrowseEntity.Queue("orders");

    internal static RedactionPipeline Redaction() =>
        QuickViewRedactionProfile.Create(new ProjectTokenKey("quickview-tests", new byte[32]), "synthetic-salt-quickview");

    private static (QuickPeekSession Session, SyntheticMessageBrowser Browser) Open(int active = 60, int dead = 30, int scheduled = 6, int transfer = 3, int maxPerCall = int.MaxValue)
    {
        var browser = new SyntheticMessageBrowser { MaxPerCall = maxPerCall }.Generate(Orders, active, dead, scheduled, transfer, firstSequence: 400);
        return (new QuickPeekSession(browser, Orders, Redaction()), browser);
    }

    [Fact]
    public async Task First_page_is_top_n_active_messages_from_the_main_entity()
    {
        var (session, browser) = Open();

        var page = await session.LoadAsync(MessageLane.Active, 25);

        Assert.Equal(25, page.Messages.Count);
        Assert.All(page.Messages, m => Assert.Equal(MessageLane.Active, m.Lane));
        Assert.True(page.HasMore);
        Assert.All(browser.Calls, c => Assert.Equal(PeekSource.Main, c.Source));
        Assert.Equal(0, browser.Calls[0].From);
        Assert.Equal(page.Messages.OrderBy(m => m.SequenceNumber).Select(m => m.SequenceNumber), page.Messages.Select(m => m.SequenceNumber));
    }

    [Fact]
    public async Task Load_more_continues_from_the_cursor_and_stops_at_the_end()
    {
        var (session, browser) = Open(active: 60, scheduled: 0);

        var first = await session.LoadAsync(MessageLane.Active, 25);
        var next = session.NextSequence(MessageLane.Active);
        var second = await session.LoadAsync(MessageLane.Active, 25);
        var third = await session.LoadAsync(MessageLane.Active, 25);

        Assert.Equal(first.Messages.Max(m => m.SequenceNumber) + 1, next);
        Assert.Equal(next, browser.Calls[1].From);
        Assert.True(second.Messages.Min(m => m.SequenceNumber) >= next);
        Assert.Equal(10, third.Messages.Count);
        Assert.False(third.HasMore);
        Assert.Equal(60, first.Messages.Concat(second.Messages).Concat(third.Messages).Select(m => m.SequenceNumber).Distinct().Count());
    }

    [Fact]
    public async Task Short_batches_keep_peeking_until_the_page_is_full()
    {
        var (session, browser) = Open(active: 60, scheduled: 0, maxPerCall: 7);

        var page = await session.LoadAsync(MessageLane.Active, 25);

        Assert.Equal(25, page.Messages.Count);
        Assert.Equal(4, page.PeekCalls);
        Assert.Equal([25, 18, 11, 4], browser.Calls.Select(c => c.Max));
    }

    [Fact]
    public async Task Scheduled_messages_met_while_peeking_active_are_kept_for_the_scheduled_lane()
    {
        var (session, browser) = Open(active: 20, scheduled: 5);

        var active = await session.LoadAsync(MessageLane.Active, 25);
        var calls = browser.Calls.Count;
        var scheduled = await session.LoadAsync(MessageLane.Scheduled, 25);

        Assert.Equal(20, active.Messages.Count);
        Assert.Equal(5, scheduled.Messages.Count);
        Assert.All(scheduled.Messages, m => Assert.Equal(MessageLane.Scheduled, m.Lane));
        Assert.All(scheduled.Messages, m => Assert.StartsWith("due ", m.Reason, StringComparison.Ordinal));
        Assert.Equal(calls, browser.Calls.Count);
        Assert.False(scheduled.HasMore);
    }

    [Fact]
    public async Task Dead_letter_and_transfer_dead_letter_each_have_their_own_cursor()
    {
        var (session, browser) = Open();

        await session.LoadAsync(MessageLane.Active, 10);
        var dead = await session.LoadAsync(MessageLane.DeadLetter, 10);
        var transfer = await session.LoadAsync(MessageLane.TransferDeadLetter, 10);

        Assert.Equal(10, dead.Messages.Count);
        Assert.Equal(3, transfer.Messages.Count);
        Assert.Contains(browser.Calls, c => c.Source == PeekSource.DeadLetter && c.From == 0);
        Assert.Contains(browser.Calls, c => c.Source == PeekSource.TransferDeadLetter && c.From == 0);
        Assert.All(dead.Messages, m => Assert.True(m.HasReason));
        Assert.NotEqual(session.NextSequence(MessageLane.Active), session.NextSequence(MessageLane.DeadLetter));
    }

    [Fact]
    public async Task A_page_stops_after_the_call_cap_when_the_lane_is_rare()
    {
        // 500 scheduled messages and no active ones, peeked 5 at a time: the Active page gives up after the cap.
        var browser = new SyntheticMessageBrowser { MaxPerCall = 5 }.Generate(Orders, active: 0, scheduled: 500);
        var session = new QuickPeekSession(browser, Orders, Redaction());

        var page = await session.LoadAsync(MessageLane.Active, 25);

        Assert.Empty(page.Messages);
        Assert.Equal(QuickPeekSession.MaxCallsPerPage, page.PeekCalls);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task Reset_starts_the_sub_queue_again()
    {
        var (session, browser) = Open();
        await session.LoadAsync(MessageLane.Active, 25);

        var before = browser.Calls.Count;
        session.Reset(MessageLane.Active);
        var again = await session.LoadAsync(MessageLane.Active, 25);

        Assert.Equal(0, browser.Calls[before].From);
        Assert.Equal(25, again.Messages.Count);
    }

    [Fact]
    public async Task A_frame_below_the_cursor_is_refused()
    {
        var browser = new SyntheticMessageBrowser().Generate(Orders, active: 30);
        var session = new QuickPeekSession(new RewindingBrowser(browser), Orders, Redaction());
        await session.LoadAsync(MessageLane.Active, 10);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.LoadAsync(MessageLane.Active, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task A_page_is_one_to_one_hundred(int count)
    {
        var (session, _) = Open();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.LoadAsync(MessageLane.Active, count));
    }

    [Fact]
    public async Task A_topic_is_not_peeked()
    {
        var browser = new SyntheticMessageBrowser();
        var session = new QuickPeekSession(browser, new BrowseEntity(EntityKind.Topic, "order-events"), Redaction());

        var page = await session.LoadAsync(MessageLane.Scheduled, 25);

        Assert.Empty(page.Messages);
        Assert.False(page.HasMore);
        Assert.Empty(browser.Calls);
    }

    [Fact]
    public void The_browser_contract_has_peek_and_nothing_that_changes_a_message()
    {
        var names = typeof(IMessageBrowser).GetMethods().Select(m => m.Name).ToList();
        Assert.Equal(["get_ProviderName", "CanPeek", "PeekAsync"], names);
        foreach (var verb in new[] { "Receive", "Complete", "Abandon", "Defer", "Renew", "Settle", "DeadLetter", "Send", "Delete" })
            Assert.DoesNotContain(names, n => n.Contains(verb, StringComparison.Ordinal));
    }

    [Fact]
    public async Task One_hundred_messages_redact_quickly()
    {
        var (session, _) = Open(active: 100, scheduled: 0);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var page = await session.LoadAsync(MessageLane.Active, 100);
        clock.Stop();

        Assert.Equal(100, page.Messages.Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"100 messages took {clock.Elapsed.TotalMilliseconds:0} ms");
    }

    /// <summary>Returns the first frames again on the second call: a broker bug the session must refuse.</summary>
    private sealed class RewindingBrowser(IMessageBrowser inner) : IMessageBrowser
    {
        private int _calls;

        public string ProviderName => inner.ProviderName;

        public bool CanPeek(BrowseEntity entity, PeekSource source) => inner.CanPeek(entity, source);

        public Task<IReadOnlyList<PeekedFrame>> PeekAsync(BrowseEntity entity, PeekSource source, long fromSequenceNumber, int max, CancellationToken cancellationToken = default) =>
            inner.PeekAsync(entity, source, ++_calls == 1 ? fromSequenceNumber : 0, max, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
