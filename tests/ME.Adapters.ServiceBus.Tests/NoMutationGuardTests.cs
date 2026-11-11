// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;
using Acornsoft.MessageExplorer.Brokers;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Azure.ResourceManager.ServiceBus;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

/// <summary>
/// The no-mutation guard. It reads the compiled adapter IL and fails if any Service Bus send, receive,
/// lock, settle, create, update, or delete API is referenced anywhere in it, including async state machines
/// and lambdas. Azure Resource Manager calls must be property reads or an allow-listed list or get (HTTP GET);
/// create, update, delete, key listing and key regeneration fail the build. Negative controls prove the
/// scanner catches planted calls of both kinds.
/// </summary>
public class NoMutationGuardTests
{
    [Fact]
    public void Compiled_adapter_reaches_no_mutating_service_bus_api()
    {
        var path = typeof(ServiceBusEntityInventory).Assembly.Location;
        var violations = MutationScanner.Scan(path, _ => true);
        Assert.True(violations.Count == 0, "Mutating API reachable:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Adapter_does_call_the_paged_read_apis()
    {
        var path = typeof(ServiceBusEntityInventory).Assembly.Location;
        var calls = MutationScanner.AdministrationCalls(path);
        foreach (var expected in new[]
        {
            "GetQueuesRuntimePropertiesAsync", "GetQueuesAsync",
            "GetTopicsRuntimePropertiesAsync", "GetTopicsAsync",
            "GetSubscriptionsRuntimePropertiesAsync", "GetSubscriptionsAsync",
        })
        {
            Assert.Contains(expected, calls);
        }
    }

    [Fact]
    public void Adapter_does_call_the_arm_list_reads()
    {
        var path = typeof(ServiceBusEntityInventory).Assembly.Location;
        var calls = MutationScanner.ArmCalls(path);
        foreach (var expected in new[]
        {
            "GetTenants", "GetSubscriptions", "GetAllAsync", "GetServiceBusNamespacesAsync",
            "GetServiceBusQueues", "GetServiceBusTopics", "GetServiceBusSubscriptions", "GetGenericResourcesAsync",
        })
        {
            Assert.Contains(expected, calls);
        }
    }

    [Fact]
    public void Scanner_catches_planted_arm_writes_and_key_reads()
    {
        var path = typeof(PlantedArmViolation).Assembly.Location;
        var violations = MutationScanner.Scan(path, type => type.FullName.Contains(nameof(PlantedArmViolation), StringComparison.Ordinal));

        Assert.Contains(violations, v => v.Contains("CreateOrUpdateAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("DeleteAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("GetKeysAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("RegenerateKeysAsync", StringComparison.Ordinal));
    }

    [Fact]
    public void Scanner_catches_a_planted_mutation()
    {
        var path = typeof(PlantedViolation).Assembly.Location;
        var violations = MutationScanner.Scan(path, type => type.FullName.Contains(nameof(PlantedViolation), StringComparison.Ordinal));

        Assert.Contains(violations, v => v.Contains("CreateQueueAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("DeleteQueueAsync", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("ServiceBusClient", StringComparison.Ordinal));
        Assert.Contains(violations, v => v.Contains("CreateSender", StringComparison.Ordinal));
    }

    [Fact]
    public void Inventory_type_exposes_only_list_members()
    {
        var names = typeof(ServiceBusEntityInventory)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName)
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["DisposeAsync", "ListQueuesAsync", "ListSubscriptionsAsync", "ListTopicsAsync"], names);
    }

    [Fact]
    public void Reader_seam_exposes_only_page_reads()
    {
        Assert.All(typeof(IServiceBusAdminReader).GetMethods(), method => Assert.EndsWith("PagesAsync", method.Name, StringComparison.Ordinal));
    }

    /// <summary>Never called. It exists so the scanner has something to catch.</summary>
    private static class PlantedViolation
    {
        public static async Task Plant(ServiceBusAdministrationClient admin)
        {
            await admin.CreateQueueAsync("planted").ConfigureAwait(false);
            await admin.DeleteQueueAsync("planted").ConfigureAwait(false);
            await using var client = new ServiceBusClient("planted.servicebus.windows.net", new Azure.Identity.DefaultAzureCredential());
            await using var sender = client.CreateSender("planted");
        }
    }

    /// <summary>Never called. ARM writes and secret reads the scanner must catch.</summary>
    private static class PlantedArmViolation
    {
        public static async Task Plant(ServiceBusNamespaceResource ns, ServiceBusNamespaceAuthorizationRuleResource rule)
        {
            await ns.GetServiceBusQueues().CreateOrUpdateAsync(Azure.WaitUntil.Completed, "planted", new ServiceBusQueueData()).ConfigureAwait(false);
            await ns.DeleteAsync(Azure.WaitUntil.Completed).ConfigureAwait(false);
            await rule.GetKeysAsync().ConfigureAwait(false);
            await rule.RegenerateKeysAsync(new Azure.ResourceManager.ServiceBus.Models.ServiceBusRegenerateAccessKeyContent(Azure.ResourceManager.ServiceBus.Models.ServiceBusAccessKeyType.PrimaryKey)).ConfigureAwait(false);
        }
    }
}
