// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Operations.Tests;

public class MoveRoutesTests
{
    private const string Ns = "sbns-demo-001.servicebus.windows.net";
    private const string Other = "sbns-demo-002.servicebus.windows.net";

    [Fact]
    public void The_four_supported_routes_classify()
    {
        Assert.Equal(MoveRouteKind.DeadLetterToParent, MoveRoutes.Classify(MoveEndpoint.Queue(Ns, "orders", true), MoveEndpoint.Queue(Ns, "orders")));
        Assert.Equal(MoveRouteKind.QueueToQueue, MoveRoutes.Classify(MoveEndpoint.Queue(Ns, "orders"), MoveEndpoint.Queue(Ns, "payments")));
        Assert.Equal(MoveRouteKind.QueueToQueue, MoveRoutes.Classify(MoveEndpoint.Queue(Ns, "orders"), MoveEndpoint.Queue(Other, "orders")));
        Assert.Equal(MoveRouteKind.SubscriptionToQueue, MoveRoutes.Classify(MoveEndpoint.Subscription(Ns, "order-events", "billing"), MoveEndpoint.Queue(Ns, "payments")));
        Assert.Equal(MoveRouteKind.SubscriptionDeadLetterToTopic, MoveRoutes.Classify(MoveEndpoint.Subscription(Ns, "order-events", "billing", true), MoveEndpoint.TopicOf(Ns, "order-events")));
    }

    public static TheoryData<MoveEndpoint, MoveEndpoint, string> Refused => new()
    {
        { MoveEndpoint.Queue(Ns, "orders", true), MoveEndpoint.Queue(Ns, "payments"), "own queue (orders)" },
        { MoveEndpoint.Queue(Ns, "orders", true), MoveEndpoint.Queue(Other, "orders"), "own queue (orders)" },
        { MoveEndpoint.Queue(Ns, "orders"), MoveEndpoint.Queue(Ns, "payments", true), "dead-letter queue can't be a target" },
        { MoveEndpoint.Queue(Ns, "orders"), MoveEndpoint.Queue(Ns, "orders"), "same queue" },
        { MoveEndpoint.Queue(Ns, "orders"), MoveEndpoint.TopicOf(Ns, "order-events"), "Move mode supports" },
        { MoveEndpoint.TopicOf(Ns, "order-events"), MoveEndpoint.Queue(Ns, "orders"), "A topic doesn't hold messages" },
        { MoveEndpoint.Subscription(Ns, "order-events", "billing", true), MoveEndpoint.TopicOf(Ns, "payment-events"), "own topic (order-events)" },
        { MoveEndpoint.Subscription(Ns, "order-events", "billing", true), MoveEndpoint.Queue(Ns, "orders"), "own topic" },
        { MoveEndpoint.Subscription(Ns, "order-events", "billing"), MoveEndpoint.TopicOf(Ns, "order-events"), "Move mode supports" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Other_routes_are_refused_in_plain_words(MoveEndpoint source, MoveEndpoint target, string expected)
    {
        var refused = Assert.Throws<MoveRefusedException>(() => MoveRoutes.Classify(source, target));
        Assert.Contains(expected, refused.Message, StringComparison.Ordinal);
        Assert.False(MoveRoutes.IsAllowed(source, target));
    }

    [Fact]
    public void Endpoints_describe_themselves_and_build_the_moved_from_value()
    {
        var dlq = MoveEndpoint.Subscription(Ns, "order-events", "billing", true);
        Assert.Equal("order-events/subscriptions/billing/$deadletterqueue", dlq.EntityPath);
        Assert.Equal("dead-letter queue of subscription billing of topic order-events", dlq.Describe());
        Assert.Equal($"{Ns}/order-events/subscriptions/billing/$deadletterqueue", dlq.MovedFromValue);
        Assert.Equal("queue orders", MoveEndpoint.Queue(Ns, "orders").Describe());
    }
}
