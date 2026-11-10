// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
//
// Tap setup (write, owner-approved).
// The explorer never deploys this file. The topic owner reviews it and applies it.
// Target: an explorer-owned Service Bus queue, delivered with a managed identity.
// TTL and max size bound the tap so it cannot grow forever.
// Docs: https://learn.microsoft.com/azure/event-grid/handler-service-bus

@description('Name of the explorer-owned Service Bus namespace. The owner creates or selects it.')
param serviceBusNamespaceName string

@description('Explorer-owned queue that receives the diagnostic copy.')
param destinationQueueName string = 'q-message-explorer-diag'

@description('Existing Event Grid topic that the owner taps.')
param topicName string

@description('Diagnostic subscription name.')
param subscriptionName string = 'diag-message-explorer'

@description('User-assigned identity resource id that delivers to the queue.')
param deliveryIdentityResourceId string

@description('Queue TTL. Bounds how long a tapped copy is kept.')
param queueMessageTtl string = 'PT1H'

@description('Queue max size in megabytes.')
param queueMaxSizeInMegabytes int = 1024

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: serviceBusNamespaceName
}

resource destinationQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: serviceBusNamespace
  name: destinationQueueName
  properties: {
    maxSizeInMegabytes: queueMaxSizeInMegabytes
    defaultMessageTimeToLive: queueMessageTtl
    deadLetteringOnMessageExpiration: true
    lockDuration: 'PT1M'
  }
}

resource topic 'Microsoft.EventGrid/topics@2024-06-01-preview' existing = {
  name: topicName
}

resource diagnosticSubscription 'Microsoft.EventGrid/topics/eventSubscriptions@2024-06-01-preview' = {
  parent: topic
  name: subscriptionName
  properties: {
    destination: {
      endpointType: 'ServiceBusQueue'
      properties: {
        resourceId: destinationQueue.id
      }
    }
    deliveryWithResourceIdentity: {
      identity: {
        type: 'UserAssigned'
        userAssignedIdentity: deliveryIdentityResourceId
      }
      destination: {
        endpointType: 'ServiceBusQueue'
        properties: {
          resourceId: destinationQueue.id
        }
      }
    }
    eventDeliverySchema: 'CloudEventSchemaV1_0'
    retryPolicy: {
      eventTimeToLive: 60
      maxDeliveryAttempts: 3
    }
  }
}
