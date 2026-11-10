// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Operations.ServiceBus;
using Azure.Messaging.ServiceBus;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Acornsoft.MessageExplorer.Operations.Tests;

/// <summary>
/// The quick view's Service Bus peek. It sits in this assembly only so the all-assemblies guard stays exact;
/// this test reads its IL (with its async state machines and lambdas) and allows peek and nothing else.
/// </summary>
public class ServiceBusMessageBrowserTests
{
    private static readonly HashSet<string> AllowedServiceBusCalls = new(StringComparer.Ordinal)
    {
        ".ctor",
        "CreateReceiver",
        "PeekMessagesAsync",
        "DisposeAsync",
        "set_ReceiveMode",
        "set_SubQueue",
    };

    [Fact]
    public void The_browser_reaches_peek_and_no_receive_lock_renew_defer_or_settle()
    {
        var calls = ServiceBusCalls(typeof(ServiceBusMessageBrowser));

        Assert.Contains("PeekMessagesAsync", calls);
        var forbidden = calls.Where(c => !c.StartsWith("get_", StringComparison.Ordinal) && !AllowedServiceBusCalls.Contains(c)).ToList();
        Assert.True(forbidden.Count == 0, "Not a peek: " + string.Join(", ", forbidden));
        foreach (var verb in new[] { "Receive", "Complete", "Abandon", "Defer", "Renew", "DeadLetter", "Send", "Schedule", "Accept" })
            Assert.DoesNotContain(calls, c => c.StartsWith(verb, StringComparison.Ordinal));
    }

    [Fact]
    public void The_guard_sees_a_receive_when_one_is_there()
    {
        var calls = ServiceBusCalls(typeof(ServiceBusMoveTransport));
        Assert.Contains("ReceiveMessagesAsync", calls);
        Assert.Contains("CompleteMessageAsync", calls);
    }

    [Fact]
    public void A_scheduled_message_on_the_main_entity_goes_to_the_scheduled_lane()
    {
        var due = new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero);
        var message = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: new BinaryData(Encoding.UTF8.GetBytes("{\"ref\":\"REF-1\",\"amount\":12.5}")),
            messageId: "msg-1",
            subject: "order.created",
            contentType: "application/json",
            sequenceNumber: 77,
            scheduledEnqueueTime: due,
            serviceBusMessageState: ServiceBusMessageState.Scheduled,
            properties: new Dictionary<string, object> { ["tenant"] = "synthetic", ["DeadLetterReason"] = "x" });

        var frame = ServiceBusMessageBrowser.Frame(message, PeekSource.Main);

        Assert.Equal(MessageLane.Scheduled, frame.Lane);
        Assert.Equal(due, frame.ScheduledFor);
        Assert.Equal(77, frame.SequenceNumber);
        Assert.Equal("REF-1", frame.Body["ref"]);
        Assert.Equal(["tenant"], frame.ApplicationProperties.Keys);
    }

    [Fact]
    public void Deferred_stays_active_and_sub_queues_set_their_lane()
    {
        var deferred = ServiceBusModelFactory.ServiceBusReceivedMessage(sequenceNumber: 5, serviceBusMessageState: ServiceBusMessageState.Deferred);
        var dead = ServiceBusModelFactory.ServiceBusReceivedMessage(
            sequenceNumber: 6,
            properties: new Dictionary<string, object> { ["DeadLetterReason"] = "MaxDeliveryCountExceeded", ["DeadLetterErrorDescription"] = "gave up" });

        var active = ServiceBusMessageBrowser.Frame(deferred, PeekSource.Main);
        var dlq = ServiceBusMessageBrowser.Frame(dead, PeekSource.DeadLetter);
        var transfer = ServiceBusMessageBrowser.Frame(dead, PeekSource.TransferDeadLetter);

        Assert.Equal((MessageLane.Active, true), (active.Lane, active.Deferred));
        Assert.Equal(MessageLane.DeadLetter, dlq.Lane);
        Assert.Equal("MaxDeliveryCountExceeded", dlq.DeadLetterReason);
        Assert.Equal("gave up", dlq.DeadLetterDescription);
        Assert.Equal(MessageLane.TransferDeadLetter, transfer.Lane);
    }

    [Fact]
    public async Task A_topic_is_refused_before_anything_connects()
    {
        var opened = 0;
        await using var browser = new ServiceBusMessageBrowser(() =>
        {
            opened++;
            throw new InvalidOperationException("no network in tests");
        });

        Assert.False(browser.CanPeek(new BrowseEntity(EntityKind.Topic, "events"), PeekSource.Main));
        Assert.True(browser.CanPeek(BrowseEntity.Subscription("events", "audit"), PeekSource.TransferDeadLetter));
        await Assert.ThrowsAsync<BrokerException>(() => browser.PeekAsync(new BrowseEntity(EntityKind.Topic, "events"), PeekSource.Main, 0, 25));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => browser.PeekAsync(BrowseEntity.Queue("orders"), PeekSource.Main, 0, 101));
        Assert.Equal(0, opened);
    }

    /// <summary>Every Azure.Messaging.ServiceBus method the type (and its nested state machines) calls.</summary>
    private static IReadOnlySet<string> ServiceBusCalls(Type type)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(type.Assembly.Location);
        var root = assembly.MainModule.GetType(type.FullName!.Replace('+', '/'));
        var calls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var definition in WithNested(root))
        {
            foreach (var method in definition.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.OperandType == OperandType.InlineMethod && instruction.Operand is MethodReference called
                        && Outermost(called.DeclaringType).Namespace.StartsWith("Azure.Messaging.ServiceBus", StringComparison.Ordinal))
                        calls.Add(called.Name);
                }
            }
        }

        return calls;
    }

    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(WithNested))
            yield return nested;
    }

    private static TypeReference Outermost(TypeReference type)
    {
        var current = type.GetElementType();
        while (current.IsNested)
            current = current.DeclaringType;
        return current;
    }
}
