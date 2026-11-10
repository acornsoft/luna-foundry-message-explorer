// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Azure;
using Acornsoft.MessageExplorer.Scenarios.Tests;

namespace Acornsoft.MessageExplorer.Cli.Tests;

public class CliArgsTests
{
    [Fact]
    public void Parses_command_subject_options_and_flags()
    {
        var args = CliArgs.Parse(["dlq", "resubmit", "gsl-reg-e-dispute", "--run", "r1", "--yes", "--sequence", "3, 7"]);
        Assert.Equal("dlq resubmit", args.Command);
        Assert.Equal("gsl-reg-e-dispute", args.Subject);
        Assert.Equal("r1", args.Options["run"]);
        Assert.Contains("yes", args.Flags);
        Assert.Equal(new long[] { 3, 7 }, args.Sequences().Order().ToArray());
    }

    [Theory]
    [InlineData("--run")]
    [InlineData("--")]
    public void Option_without_value_is_a_usage_error(string last) =>
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(["dlq", "peek", "x", last]));

    [Theory]
    [InlineData("a,b")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Bad_sequences_are_rejected(string value)
    {
        var args = CliArgs.Parse(["dlq", "resubmit", "x", "--sequence", value == "-3" ? "x-3" : value]);
        Assert.Throws<CliUsageException>(() => args.Sequences());
    }

    [Fact]
    public void Settle_seconds_must_be_positive()
    {
        Assert.Equal(90, CliArgs.Parse(["scenario", "run", "x"]).Int("settle-seconds", 90));
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(["scenario", "run", "x", "--settle-seconds", "0"]).Int("settle-seconds", 90));
    }

    [Fact]
    public void Too_many_words_is_an_error() =>
        Assert.Throws<CliUsageException>(() => CliArgs.Parse(["a", "b", "c", "d"]));
}

[Collection("environment")]
public class CliAppTests
{
    private static (CliApp App, FakeRegEBroker Broker, StringWriter Output, List<AzureQueueAddress> Addresses) Make()
    {
        var broker = new FakeRegEBroker();
        var output = new StringWriter();
        var clock = new StepClock();
        var addresses = new List<AzureQueueAddress>();
        var app = new CliApp((address, _) => { addresses.Add(address); return broker; }, output, clock, clock.Delay);
        return (app, broker, output, addresses);
    }

    private static readonly string[] Coordinates = ["--subscription", "00000000-0000-0000-0000-000000000001", "--resource-group", "rg-demo"];

    [Fact]
    public async Task No_arguments_prints_usage_with_exit_2()
    {
        var (app, _, output, _) = Make();
        Assert.Equal(2, await app.RunAsync([]));
        Assert.Contains("lfme scenario run", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_exits_0()
    {
        var (app, _, _, _) = Make();
        Assert.Equal(0, await app.RunAsync(["--help"]));
    }

    [Fact]
    public async Task List_shows_the_gsl_scenario()
    {
        var (app, _, output, _) = Make();
        Assert.Equal(0, await app.RunAsync(["scenario", "list"]));
        Assert.Contains("gsl-reg-e-dispute  seed 63101  15 messages  -> sbns-gsl-dev-eastus-001.servicebus.windows.net/sbq-gsl-dev-001", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_scenario_is_a_usage_error()
    {
        var (app, _, output, _) = Make();
        Assert.Equal(2, await app.RunAsync(["scenario", "run", "nope", .. Coordinates]));
        Assert.Contains("Unknown live scenario", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_coordinates_name_the_option_and_the_variable()
    {
        Environment.SetEnvironmentVariable("LFME_SUBSCRIPTION", null);
        var (app, _, output, _) = Make();
        Assert.Equal(2, await app.RunAsync(["scenario", "run", "gsl-reg-e-dispute"]));
        Assert.Contains("--subscription is required (or set LFME_SUBSCRIPTION)", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_then_peek_then_resubmit_end_to_end()
    {
        var (app, broker, output, addresses) = Make();
        Assert.Equal(0, await app.RunAsync(["scenario", "run", "gsl-reg-e-dispute", "--run-id", "t1", .. Coordinates]));
        Assert.Equal("sbns-gsl-dev-eastus-001.servicebus.windows.net", addresses[0].FullyQualifiedNamespace);
        Assert.Equal("sbq-gsl-dev-001", addresses[0].Queue);
        Assert.Equal("rg-demo", addresses[0].ResourceGroup);

        Assert.Equal(0, await app.RunAsync(["dlq", "peek", "gsl-reg-e-dispute", "--run", "t1", .. Coordinates]));
        Assert.Contains("Dead-letters for run t1 (peek, nothing locked): 3", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await app.RunAsync(["dlq", "resubmit", "gsl-reg-e-dispute", "--run", "t1", .. Coordinates]));
        Assert.Equal(3, broker.DeadLetters.Count);

        Assert.Equal(0, await app.RunAsync(["dlq", "resubmit", "gsl-reg-e-dispute", "--run", "t1", "--yes", .. Coordinates]));
        broker.Drain();
        Assert.Single(broker.DeadLetters);
    }

    [Fact]
    public async Task Real_subscription_must_be_allow_listed_through_the_environment()
    {
        var (app, broker, output, _) = Make();
        broker.Facts = broker.Facts with { SubscriptionId = "22222222-2222-2222-2222-222222222222" };
        string[] args = ["scenario", "run", "gsl-reg-e-dispute", "--subscription", "22222222-2222-2222-2222-222222222222", "--resource-group", "rg"];

        Environment.SetEnvironmentVariable("LFME_ALLOWED_SUBSCRIPTIONS", null);
        Assert.Equal(1, await app.RunAsync(args));
        Assert.Contains("Subscription is not on the allow-list", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(broker.Sent);

        try
        {
            Environment.SetEnvironmentVariable("LFME_ALLOWED_SUBSCRIPTIONS", "22222222-2222-2222-2222-222222222222");
            Assert.Equal(0, await app.RunAsync(args));
            Assert.Equal(15, broker.Sent.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LFME_ALLOWED_SUBSCRIPTIONS", null);
        }
    }
}
