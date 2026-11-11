// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Test-only loader: starts the .NET WebAssembly runtime and exposes the C# test entry point.
import { dotnet } from "./_framework/dotnet.js";

const runtime = await dotnet.create();
const exports = await runtime.getAssemblyExports("InteropApp.dll");
const program = exports.Acornsoft.MessageExplorer.Credentials.Browser.InteropApp.Program;
globalThis.runInterop = (secret) => program.RunAsync(secret);
globalThis.readOutcome = (id, secret) => program.ReadOutcomeAsync(id, secret);
globalThis.interopReady = true;
