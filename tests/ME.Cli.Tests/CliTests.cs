// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Azure;
using Acornsoft.MessageExplorer.Scenarios.Tests;
using Azure.Core;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Cli.Tests;

public class CliArgsTests
{
    [Fact]
    public void Parses_command_subject_options_and_flags()
    {
        var args = CliArgs.Parse(["dlq", "resubmit", "gsl-reg-e-dispute", "--run", "r1", "--yes", "--send", "--sequence", "3, 7"]);
        Assert.Equal("dlq resubmit", args.Command);
        Assert.Equal("gsl-reg-e-dispute", args.Subject);
        Assert.Equal("r1", args.Options["run"]);
        Assert.Contains("yes", args.Flags);
        Assert.Contains("send", args.Flags);
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
        var app = new CliApp((address, _) => { addresses.Add(address); return broker; }, output, clock, clock.Delay, _ => Task.CompletedTask);
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
    public async Task Run_without_send_is_a_dry_run()
    {
        var (app, broker, output, _) = Make();
        Assert.Equal(0, await app.RunAsync(["scenario", "run", "gsl-reg-e-dispute", "--run-id", "t1", .. Coordinates]));
        Assert.Empty(broker.Sent);
        Assert.Contains("Dry run: nothing sent.", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_and_dry_run_together_is_a_usage_error()
    {
        var (app, broker, output, _) = Make();
        Assert.Equal(2, await app.RunAsync(["scenario", "run", "gsl-reg-e-dispute", "--send", "--dry-run", .. Coordinates]));
        Assert.Empty(broker.Sent);
        Assert.Contains("Do not pass both", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_then_peek_then_resubmit_end_to_end()
    {
        var (app, broker, output, addresses) = Make();
        Assert.Equal(0, await app.RunAsync(["scenario", "run", "gsl-reg-e-dispute", "--run-id", "t1", "--send", .. Coordinates]));
        Assert.Equal("sbns-gsl-dev-eastus-001.servicebus.windows.net", addresses[0].FullyQualifiedNamespace);
        Assert.Equal("sbq-gsl-dev-001", addresses[0].Queue);
        Assert.Equal("rg-demo", addresses[0].ResourceGroup);
        Assert.Equal(15, broker.Sent.Count);

        Assert.Equal(0, await app.RunAsync(["dlq", "peek", "gsl-reg-e-dispute", "--run", "t1", .. Coordinates]));
        Assert.Contains("Dead-letters for run t1 (peek, nothing locked): 3", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await app.RunAsync(["dlq", "resubmit", "gsl-reg-e-dispute", "--run", "t1", .. Coordinates]));
        Assert.Equal(3, broker.DeadLetters.Count);

        Assert.Equal(0, await app.RunAsync(["dlq", "resubmit", "gsl-reg-e-dispute", "--run", "t1", "--yes", .. Coordinates]));
        Assert.Equal(3, broker.DeadLetters.Count);
        Assert.Contains("Add --send with --yes", output.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, await app.RunAsync(["dlq", "resubmit", "gsl-reg-e-dispute", "--run", "t1", "--yes", "--send", .. Coordinates]));
        broker.Drain();
        Assert.Single(broker.DeadLetters);
    }

    [Fact]
    public async Task Peek_without_run_is_a_usage_error_and_prints_no_foreign_dead_letters()
    {
        const string foreignId = "GSL-DSP-FOREIGN-99";
        const string foreignDescription = "secret-from-another-producer: body text in the description";
        var (app, broker, output, _) = Make();
        broker.DeadLetters.Add(new(
            999,
            "foreign-msg",
            foreignId,
            "reg-e.deadlines.computed",
            "MaxDeliveryCountExceeded",
            foreignDescription,
            10,
            DateTimeOffset.UnixEpoch,
            "{}",
            new Dictionary<string, object>()));

        Assert.Equal(2, await app.RunAsync(["dlq", "peek", "gsl-reg-e-dispute", .. Coordinates]));
        var text = output.ToString();
        Assert.Contains("needs --run", text, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignDescription, text, StringComparison.Ordinal);
        Assert.DoesNotContain("seq 999", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Peek_with_run_hides_dead_letters_from_other_runs()
    {
        const string foreignId = "GSL-DSP-FOREIGN-99";
        const string foreignDescription = "secret-from-another-producer: body text in the description";
        var (app, broker, output, _) = Make();
        Assert.Equal(0, await app.RunAsync(["scenario", "run", "gsl-reg-e-dispute", "--run-id", "t1", "--send", .. Coordinates]));
        broker.DeadLetters.Add(new(
            999,
            "foreign-msg",
            foreignId,
            "reg-e.deadlines.computed",
            "MaxDeliveryCountExceeded",
            foreignDescription,
            10,
            DateTimeOffset.UnixEpoch,
            "{}",
            new Dictionary<string, object> { ["lfmeRunId"] = "other-run" }));

        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.RunAsync(["dlq", "peek", "gsl-reg-e-dispute", "--run", "t1", .. Coordinates]));
        var text = output.ToString();
        Assert.Contains("Dead-letters for run t1 (peek, nothing locked): 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignId, text, StringComparison.Ordinal);
        Assert.DoesNotContain(foreignDescription, text, StringComparison.Ordinal);
        Assert.DoesNotContain("seq 999", text, StringComparison.Ordinal);
        Assert.DoesNotContain("other-run", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_subscription_must_be_allow_listed_through_the_environment()
    {
        var (app, broker, output, _) = Make();
        broker.Facts = broker.Facts with { SubscriptionId = "22222222-2222-2222-2222-222222222222" };
        string[] args = ["scenario", "run", "gsl-reg-e-dispute", "--send", "--subscription", "22222222-2222-2222-2222-222222222222", "--resource-group", "rg"];

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

    [Theory]
    [InlineData("scenario", "list")]
    [InlineData("scenario", "run")]
    [InlineData("dlq", "peek")]
    [InlineData("dlq", "resubmit")]
    public async Task Every_command_requires_entra_sign_in(string verb, string noun)
    {
        var broker = new FakeRegEBroker();
        var output = new StringWriter();
        var clock = new StepClock();
        var app = new CliApp((_, _) => broker, output, clock, clock.Delay, _ => throw new EntraSignInException(EntraAuth.SignInMessage));
        string[] args = verb == "scenario" && noun == "list"
            ? ["scenario", "list"]
            : [verb, noun, "gsl-reg-e-dispute", "--run-id", "t1", "--run", "t1", "--send", .. Coordinates];

        Assert.Equal(1, await app.RunAsync(args));
        Assert.Contains("Sign in with Microsoft Entra first", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(broker.Sent);
        Assert.Empty(broker.DeadLetters);
    }

    [Fact]
    public async Task Help_does_not_require_sign_in()
    {
        var broker = new FakeRegEBroker();
        var output = new StringWriter();
        var clock = new StepClock();
        var app = new CliApp((_, _) => broker, output, clock, clock.Delay, _ => throw new EntraSignInException(EntraAuth.SignInMessage));
        Assert.Equal(0, await app.RunAsync(["--help"]));
        Assert.DoesNotContain("Sign in with Microsoft Entra first", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--connection-string", "Endpoint=sb://example.servicebus.windows.net/;KeyName=Root")]
    [InlineData("--sas-key", "not-a-secret")]
    [InlineData("--shared-access-key", "not-a-secret")]
    [InlineData("--sas-token", "not-a-secret")]
    public async Task Non_entra_credentials_are_refused(string option, string value)
    {
        var (app, broker, output, _) = Make();
        Assert.Equal(2, await app.RunAsync(["scenario", "list", option, value]));
        Assert.Contains("Keys, SAS, and connection strings are refused", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(broker.Sent);
    }

    [Fact]
    public async Task Skip_auth_environment_is_refused()
    {
        try
        {
            Environment.SetEnvironmentVariable("LFME_SKIP_AUTH", "1");
            var (app, broker, output, _) = Make();
            Assert.Equal(2, await app.RunAsync(["scenario", "list"]));
            Assert.Contains("does not allow skipping Microsoft Entra sign-in", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(broker.Sent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LFME_SKIP_AUTH", null);
        }
    }

    [Fact]
    public async Task Lfme_connection_string_environment_is_refused()
    {
        try
        {
            Environment.SetEnvironmentVariable("LFME_CONNECTION_STRING", "Endpoint=sb://example.servicebus.windows.net/;KeyName=Root");
            var (app, broker, output, _) = Make();
            Assert.Equal(2, await app.RunAsync(["scenario", "list"]));
            Assert.Contains("LFME_CONNECTION_STRING", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Keys, SAS, and connection strings are refused", output.ToString(), StringComparison.Ordinal);
            Assert.Empty(broker.Sent);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LFME_CONNECTION_STRING", null);
        }
    }
}

public class EntraAuthTests
{
    [Fact]
    public void Managed_identity_stays_off_by_default()
    {
        var off = EntraAuth.CredentialOptions(useManagedIdentity: false);
        Assert.True(off.ExcludeInteractiveBrowserCredential);
        Assert.True(off.ExcludeManagedIdentityCredential);

        var on = EntraAuth.CredentialOptions(useManagedIdentity: true);
        Assert.True(on.ExcludeInteractiveBrowserCredential);
        Assert.False(on.ExcludeManagedIdentityCredential);
    }

    [Fact]
    public async Task Ensure_signed_in_accepts_a_token()
    {
        await EntraAuth.EnsureSignedInAsync(new FakeTokenCredential("token"), CancellationToken.None);
    }

    [Fact]
    public async Task Ensure_signed_in_refuses_a_missing_account()
    {
        var error = await Assert.ThrowsAsync<EntraSignInException>(
            () => EntraAuth.EnsureSignedInAsync(new FakeTokenCredential(exception: new CredentialUnavailableException("none")), CancellationToken.None));
        Assert.Equal(EntraAuth.SignInMessage, error.Message);
    }

    [Fact]
    public async Task Ensure_signed_in_refuses_an_empty_token()
    {
        var error = await Assert.ThrowsAsync<EntraSignInException>(
            () => EntraAuth.EnsureSignedInAsync(new FakeTokenCredential(""), CancellationToken.None));
        Assert.Equal(EntraAuth.SignInMessage, error.Message);
    }

    [Fact]
    public void Connection_string_shapes_are_detected_without_storing_a_key()
    {
        Assert.True(EntraAuth.LooksLikeConnectionString("Endpoint=sb://example.servicebus.windows.net/;KeyName=Root"));
        Assert.True(EntraAuth.LooksLikeConnectionString("UseDevelopmentStorage=true"));
        Assert.True(EntraAuth.LooksLikeConnectionString("prefix " + "Shared" + "AccessKey" + "=abcd"));
        Assert.False(EntraAuth.LooksLikeConnectionString("rg-gsl-dev-eastus-001"));
        Assert.False(EntraAuth.LooksLikeConnectionString("00000000-0000-0000-0000-000000000001"));
    }
}

internal sealed class FakeTokenCredential : TokenCredential
{
    private readonly string? _token;
    private readonly Exception? _exception;

    public FakeTokenCredential(string? token = "token", Exception? exception = null)
    {
        _token = token;
        _exception = exception;
    }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        Request(requestContext);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        new(Request(requestContext));

    private AccessToken Request(TokenRequestContext requestContext)
    {
        Assert.Contains(EntraAuth.ManagementScope, requestContext.Scopes);
        if (_exception is not null)
            throw _exception;
        return new AccessToken(_token ?? "", DateTimeOffset.UtcNow.AddHours(1));
    }
}
