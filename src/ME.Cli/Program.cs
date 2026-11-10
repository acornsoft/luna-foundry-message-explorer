// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Azure;
using Acornsoft.MessageExplorer.Cli;
using Acornsoft.MessageExplorer.Model;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

// Entra only. Interactive browser sign-in stays off so the runner never pops a window in CI.
// Managed identity is off unless asked for: off Azure, the metadata-endpoint probe can stall for minutes.
// AZURE_TOKEN_CREDENTIALS (an Azure.Identity setting) still works, for example AZURE_TOKEN_CREDENTIALS=dev.
var credential = EntraAuth.CreateCredential();
var app = new CliApp(
    (address, webSockets) => new AzureScenarioBroker(address, credential, webSockets),
    Console.Out,
    new SystemClock(),
    Task.Delay,
    ct => EntraAuth.EnsureSignedInAsync(credential, ct));
return await app.RunAsync(args, cancel.Token);
