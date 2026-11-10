// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

/// <summary>
/// Reads compiled IL and lists every Service Bus send, receive, lock, settle, create, update or delete it can
/// reach, and every Azure Resource Manager call that isn't an allow-listed read. Shared by the adapter guard
/// and the all-assemblies guard (only Acornsoft.MessageExplorer.Operations may mutate).
/// </summary>
internal static class MutationScanner
{
    private static readonly HashSet<string> ForbiddenTypes = new(StringComparer.Ordinal)
    {
        "ServiceBusClient",
        "ServiceBusSender",
        "ServiceBusReceiver",
        "ServiceBusSessionReceiver",
        "ServiceBusProcessor",
        "ServiceBusSessionProcessor",
        "ServiceBusRuleManager",
        "ServiceBusMessageBatch",
    };

    private static readonly HashSet<string> AllowedAdministrationMethods = new(StringComparer.Ordinal)
    {
        ".ctor",
        "GetQueuesRuntimePropertiesAsync",
        "GetQueuesAsync",
        "GetTopicsRuntimePropertiesAsync",
        "GetTopicsAsync",
        "GetSubscriptionsRuntimePropertiesAsync",
        "GetSubscriptionsAsync",
    };

    /// <summary>Azure Resource Manager methods the adapter may call. Each one is an HTTP GET or builds a client locally.</summary>
    private static readonly HashSet<string> AllowedArmMethods = new(StringComparer.Ordinal)
    {
        ".ctor",
        "GetTenants",
        "GetSubscriptions",
        "GetSubscriptionResource",
        "CreateResourceIdentifier",
        "GetAllAsync",
        "GetAsync",
        "GetServiceBusNamespacesAsync",
        "GetServiceBusNamespaceResource",
        "GetServiceBusQueues",
        "GetServiceBusTopics",
        "GetServiceBusSubscriptions",
        "ToString",
        "Equals",
        "GetHashCode",
    };

    private static readonly string[] MutatingPrefixes =
    [
        "Create", "Delete", "Update", "Send", "Receive", "Complete", "Abandon", "DeadLetter", "Defer",
        "Renew", "Schedule", "Cancel", "Purge", "Accept", "Peek", "Set",
    ];

    public static IReadOnlyList<string> Scan(string assemblyPath, Func<TypeDefinition, bool> include)
    {
        var violations = new List<string>();
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        foreach (var type in assembly.MainModule.GetTypes().Where(include))
        {
            foreach (var field in type.Fields)
                CheckType(field.FieldType, $"{type.FullName}.{field.Name}", violations);

            foreach (var method in type.Methods.Where(method => method.HasBody))
            {
                foreach (var variable in method.Body.Variables)
                    CheckType(variable.VariableType, $"{type.FullName}::{method.Name}", violations);
                foreach (var instruction in method.Body.Instructions)
                    Check(instruction, $"{type.FullName}::{method.Name}", violations);
            }
        }

        return violations.Distinct(StringComparer.Ordinal).ToList();
    }

    public static IReadOnlySet<string> AdministrationCalls(string assemblyPath)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        foreach (var method in assembly.MainModule.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference called && Root(called.DeclaringType).Name == "ServiceBusAdministrationClient")
                    calls.Add(called.Name);
            }
        }

        return calls;
    }

    public static IReadOnlySet<string> ArmCalls(string assemblyPath)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        foreach (var method in assembly.MainModule.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.Operand is MethodReference called && IsResourceManager(Root(called.DeclaringType)))
                    calls.Add(called.Name);
            }
        }

        return calls;
    }

    private static void Check(Instruction instruction, string where, List<string> violations)
    {
        switch (instruction.Operand)
        {
            case MethodReference method:
                CheckType(method.DeclaringType, where, violations);
                CheckMethod(method, where, violations);
                break;
            case FieldReference field:
                CheckType(field.DeclaringType, where, violations);
                break;
            case TypeReference type:
                CheckType(type, where, violations);
                break;
        }
    }

    private static void CheckMethod(MethodReference method, string where, List<string> violations)
    {
        var declaring = Root(method.DeclaringType);
        if (IsResourceManager(declaring))
        {
            // Picking the cloud (ARM endpoint and audience) is a local client option, not a resource write.
            var localOption = declaring.Name == "ArmClientOptions" && method.Name == "set_Environment";
            if (!localOption && !method.Name.StartsWith("get_", StringComparison.Ordinal) && !AllowedArmMethods.Contains(method.Name))
                violations.Add($"{where} calls {declaring.Name}.{method.Name} (not an allow-listed ARM read)");
            return;
        }

        if (!IsServiceBus(declaring))
            return;

        if (declaring.Name == "ServiceBusAdministrationClient" && !AllowedAdministrationMethods.Contains(method.Name))
            violations.Add($"{where} calls ServiceBusAdministrationClient.{method.Name}");

        if (MutatingPrefixes.Any(prefix => method.Name.StartsWith(prefix, StringComparison.Ordinal)))
            violations.Add($"{where} calls {declaring.Name}.{method.Name}");
    }

    private static void CheckType(TypeReference type, string where, List<string> violations)
    {
        if (type is GenericInstanceType generic)
        {
            foreach (var argument in generic.GenericArguments)
                CheckType(argument, where, violations);
        }

        var root = Root(type);
        if (IsServiceBus(root) && ForbiddenTypes.Contains(root.Name))
            violations.Add($"{where} references {root.Name}");
    }

    private static TypeReference Root(TypeReference type)
    {
        var element = type.GetElementType();
        return element;
    }

    private static bool IsServiceBus(TypeReference type) =>
        Outermost(type).Namespace.StartsWith("Azure.Messaging.ServiceBus", StringComparison.Ordinal);

    private static bool IsResourceManager(TypeReference type) =>
        Outermost(type).Namespace.StartsWith("Azure.ResourceManager", StringComparison.Ordinal);

    private static TypeReference Outermost(TypeReference type)
    {
        var current = type;
        while (current.IsNested)
            current = current.DeclaringType;
        return current;
    }
}
