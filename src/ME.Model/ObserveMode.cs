// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Model;

public enum BrokerOperation
{
    Peek,
    LogRead,
    Receive,
    Complete,
    Abandon,
    DeadLetter,
    Defer,
    RenewLock,
    ReceiveDeferred,
    Schedule,
    CancelScheduled,
    Checkpoint,
    EpochAcquire,
    CreateEventSubscription,
}

public sealed class ObserveModeViolationException : InvalidOperationException
{
    public ObserveModeViolationException(BrokerOperation operation)
        : base($"Observe mode refuses {operation}.")
    {
        Operation = operation;
    }

    public BrokerOperation Operation { get; }
}

public sealed class ReadRefusedException : InvalidOperationException
{
    public ReadRefusedException(string message)
        : base(message)
    {
    }
}

public static class ObserveMode
{
    public static void Allow(BrokerOperation operation)
    {
        var permitted = operation switch
        {
            BrokerOperation.Peek => true,
            BrokerOperation.LogRead => true,
            BrokerOperation.Receive => false,
            BrokerOperation.Complete => false,
            BrokerOperation.Abandon => false,
            BrokerOperation.DeadLetter => false,
            BrokerOperation.Defer => false,
            BrokerOperation.RenewLock => false,
            BrokerOperation.ReceiveDeferred => false,
            BrokerOperation.Schedule => false,
            BrokerOperation.CancelScheduled => false,
            BrokerOperation.Checkpoint => false,
            BrokerOperation.EpochAcquire => false,
            BrokerOperation.CreateEventSubscription => false,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown broker operation."),
        };

        if (!permitted)
            throw new ObserveModeViolationException(operation);
    }
}
