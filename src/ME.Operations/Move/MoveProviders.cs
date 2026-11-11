// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Operations.Move;

/// <summary>Plain reasons for broker families Move mode can't work with. Shown instead of failing quietly.</summary>
public static class MoveProviders
{
    public static string Refusal(string providerId) => providerId switch
    {
        "apache-kafka" or "kafka" => "Kafka topics are append-only logs: a message can't be removed from a partition, so Move mode can't move it. Copying to another topic is planned separately.",
        "azure-event-hubs" or "event-hubs" => "Event Hubs keeps an append-only log: events can't be removed, so Move mode can't move them.",
        "azure-event-grid" or "event-grid" => "Event Grid doesn't store messages, so there is nothing to move.",
        "rabbitmq" => "Move mode for RabbitMQ isn't built yet.",
        "aws-sqs-sns" or "aws" => "Move mode for AWS SQS and SNS isn't built yet.",
        _ => "Move mode isn't available for this connection.",
    };
}
