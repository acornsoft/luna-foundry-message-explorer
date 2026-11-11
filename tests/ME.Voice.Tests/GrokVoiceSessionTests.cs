// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.ReadTools;

namespace Acornsoft.MessageExplorer.Voice.Tests;

/// <summary>Grok Voice over the read-only tools, against a fake socket. Synthetic data and a fake key built at run time.</summary>
public sealed class GrokVoiceSessionTests
{
    private static readonly string FakeKey = "xai-" + new string('k', 8) + "-not-real-" + Guid.NewGuid().ToString("N");

    private readonly FakeVoiceSocket _socket = new();

    private GrokVoiceSession Session(VoiceOptions? options = null, string? key = null) =>
        new(_socket, ReadOnlyToolset.Over(new OpenedSource(new SyntheticNamespace(), "synthetic")), options ?? new VoiceOptions(),
            _ => Task.FromResult<RuntimeSecret?>(new RuntimeSecret(key ?? FakeKey)));

    private static string Event(JsonObject json) => json.ToJsonString();

    private static string FunctionCall(string name, string arguments = "{}", string callId = "call_1") =>
        Event(new JsonObject { ["type"] = "response.function_call_arguments.done", ["name"] = name, ["call_id"] = callId, ["arguments"] = arguments });

    [Fact]
    public async Task Start_connects_to_the_realtime_endpoint_with_the_key_only_in_the_connect_header()
    {
        await using var session = Session();

        await session.StartAsync();

        Assert.Equal("wss://api.x.ai/v1/realtime?model=grok-voice-latest", _socket.Endpoint!.ToString());
        Assert.Equal(FakeKey, _socket.BearerKey);
        Assert.All(_socket.Sent, s => Assert.DoesNotContain(FakeKey, s, StringComparison.Ordinal));
        Assert.Equal("session.update", _socket.SentTypes.First());
    }

    [Fact]
    public async Task Without_a_key_nothing_connects()
    {
        await using var session = new GrokVoiceSession(_socket, ReadOnlyToolset.Over(new OpenedSource(new SyntheticNamespace(), "synthetic")), new VoiceOptions(), _ => Task.FromResult<RuntimeSecret?>(null));

        var error = await Assert.ThrowsAsync<VoiceKeyMissingException>(() => session.StartAsync());

        Assert.Null(_socket.Endpoint);
        Assert.Contains("Saved logins", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_update_offers_exactly_the_four_read_only_tools_push_to_talk_and_pcm16()
    {
        var update = GrokVoiceSession.SessionUpdate(new VoiceOptions("ara"));
        var session = update["session"]!;

        Assert.Equal("ara", session["voice"]!.GetValue<string>());
        Assert.Null(session["turn_detection"]!["type"]);
        Assert.Equal("audio/pcm", session["audio"]!["input"]!["format"]!["type"]!.GetValue<string>());
        Assert.Equal(24000, session["audio"]!["output"]!["format"]!["rate"]!.GetValue<int>());
        var tools = session["tools"]!.AsArray();
        Assert.Equal(ReadOnlyToolset.Tools.Select(t => t.Name), tools.Select(t => t!["name"]!.GetValue<string>()));
        Assert.All(tools, t => Assert.Equal("function", t!["type"]!.GetValue<string>()));
        Assert.Contains("cannot send, receive, resubmit, move, purge or delete", session["instructions"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("custom-voice-123")]
    [InlineData("Eve")]
    [InlineData("")]
    public void Only_built_in_voices_are_accepted(string voice)
    {
        Assert.Throws<ArgumentException>(() => Session(new VoiceOptions(voice)));
    }

    [Fact]
    public async Task A_read_only_tool_call_runs_and_its_result_goes_back_then_a_response_is_requested()
    {
        await using var session = Session();
        var calls = new List<VoiceToolCall>();
        session.ToolCalled += (_, c) => calls.Add(c);

        await session.HandleEventAsync(FunctionCall("check_limits", """{"maxDeadLetter":0}"""));

        var sent = _socket.SentJson.ToList();
        Assert.Equal(["conversation.item.create", "response.create"], sent.Select(s => s["type"]!.GetValue<string>()));
        var item = sent[0]["item"]!;
        Assert.Equal("function_call_output", item["type"]!.GetValue<string>());
        Assert.Equal("call_1", item["call_id"]!.GetValue<string>());
        var output = JsonNode.Parse(item["output"]!.GetValue<string>())!;
        Assert.Equal(2, output["result"]!["breaches"]!.AsArray().Count);
        Assert.True(Assert.Single(calls).Ran);
    }

    [Theory]
    [InlineData("move_message")]
    [InlineData("purge_queue")]
    [InlineData("send_message")]
    [InlineData("resubmit_dead_letters")]
    public async Task A_tool_that_would_change_anything_is_refused_and_nothing_runs(string name)
    {
        var reads = 0;
        var tools = new ReadOnlyToolset(_ =>
        {
            reads++;
            return Task.FromResult<IReadOnlyList<EntitySnapshot>>([]);
        }, "synthetic");
        await using var session = new GrokVoiceSession(_socket, tools, new VoiceOptions(), _ => Task.FromResult<RuntimeSecret?>(new RuntimeSecret(FakeKey)));
        VoiceToolCall? call = null;
        session.ToolCalled += (_, c) => call = c;

        await session.HandleEventAsync(FunctionCall(name));

        Assert.Equal(0, reads);
        Assert.False(call!.Ran);
        var output = _socket.SentJson.First()["item"]!["output"]!.GetValue<string>();
        Assert.Contains("can only read", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Push_to_talk_clears_appends_base64_audio_then_commits_and_asks()
    {
        await using var session = Session();

        await session.BeginTalkAsync();
        await session.AppendAudioAsync([1, 2, 3, 4]);
        await session.EndTalkAsync();

        Assert.Equal(["input_audio_buffer.clear", "input_audio_buffer.append", "input_audio_buffer.commit", "response.create"], _socket.SentTypes);
        Assert.Equal(Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }), _socket.SentJson.ElementAt(1)["audio"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_typed_question_is_scrubbed_by_default_before_it_leaves()
    {
        await using var session = Session();

        await session.AskAsync("Why is payments failing for alice@contoso.example?");

        var text = _socket.SentJson.First()["item"]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.DoesNotContain("alice@contoso.example", text, StringComparison.Ordinal);
        Assert.Contains("payments", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_redaction_off_a_typed_question_goes_as_typed()
    {
        await using var session = Session(new VoiceOptions(RedactBodies: false));

        await session.AskAsync("Why is payments failing for alice@contoso.example?");

        Assert.Contains("alice@contoso.example", _socket.Sent[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transcripts_and_audio_are_raised_and_server_errors_never_show_the_key()
    {
        await using var session = Session();
        var lines = new List<(VoiceSpeaker, string)>();
        var audio = new List<byte[]>();
        var problems = new List<string>();
        session.Transcript += (_, l) => lines.Add(l);
        session.Audio += (_, a) => audio.Add(a);
        session.Problem += (_, p) => problems.Add(p);
        await session.StartAsync();

        await session.HandleEventAsync(Event(new JsonObject { ["type"] = "response.output_audio_transcript.delta", ["delta"] = "Payments has 3." }));
        await session.HandleEventAsync(Event(new JsonObject { ["type"] = "conversation.item.input_audio_transcription.completed", ["transcript"] = "Any dead letters?" }));
        await session.HandleEventAsync(Event(new JsonObject { ["type"] = "response.output_audio.delta", ["delta"] = Convert.ToBase64String(new byte[] { 9, 9 }) }));
        await session.HandleEventAsync(Event(new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["message"] = "bad key " + FakeKey } }));
        await session.HandleEventAsync("not json");
        await session.HandleEventAsync("""{"type":"response.output_audio_transcript.delta","delta":42}""");

        Assert.Equal([(VoiceSpeaker.Assistant, "Payments has 3."), (VoiceSpeaker.User, "Any dead letters?")], lines);
        Assert.Equal([9, 9], Assert.Single(audio));
        Assert.DoesNotContain(FakeKey, Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_receive_loop_handles_server_events()
    {
        await using var session = Session();
        var done = new TaskCompletionSource();
        session.ResponseDone += (_, _) => done.TrySetResult();
        await session.StartAsync();

        _socket.Push("""{"type":"response.done"}""");

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void The_voice_library_reaches_no_send_receive_settle_or_management_write()
    {
        var violations = MutationScanner.Scan(typeof(GrokVoiceSession).Assembly.Location, _ => true);
        Assert.True(violations.Count == 0, string.Join("\n", violations));
        Assert.DoesNotContain("Acornsoft.MessageExplorer.Operations", typeof(GrokVoiceSession).Assembly.GetReferencedAssemblies().Select(a => a.Name));
    }
}
