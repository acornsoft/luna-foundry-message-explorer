// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.Json.Nodes;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Credentials.Stores;
using Acornsoft.MessageExplorer.ReadTools;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Acornsoft.MessageExplorer.Voice;
using Acornsoft.MessageExplorer.Voice.Tests;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>The Voice tab: the xAI key only in the vault, built-in voices only, redaction on, read-only tools. Fake socket, synthetic data.</summary>
public sealed class VoiceTests
{
    private static readonly string FakeKey = "xai-" + Guid.NewGuid().ToString("N") + "-not-real";

    private readonly MemorySecretStore _store = new();
    private readonly MemoryLoginMetadataStore _metadata = new();
    private readonly CredentialVault _vault;
    private readonly FakeVoiceSocket _socket = new();
    private IEntityInventory? _inventory = new SyntheticNamespace();

    public VoiceTests() => _vault = new CredentialVault(_store, _metadata);

    private VoiceViewModel Voice(IVoiceAudio? audio = null) => new(_vault, () => _inventory, () => "synthetic", () => _socket, audio);

    private Task<LoginEntry> AddKeyAsync(string voice = "eve") =>
        _vault.AddAsync(new LoginDraft("Grok Voice", LoginKind.XaiApiKey, new Dictionary<string, string> { ["voice"] = voice }), new RuntimeSecret(FakeKey));

    [Fact]
    public void The_xai_key_is_a_vault_login_kind_with_a_required_secret()
    {
        var kind = LoginKinds.Of(LoginKind.XaiApiKey);

        Assert.Equal(LoginSystem.Xai, kind.System);
        Assert.True(kind.SecretRequired);
        Assert.Equal("xAI (Grok Voice)", LoginKinds.SystemLabel(LoginSystem.Xai));
    }

    [Fact]
    public async Task The_key_lives_in_the_secure_store_and_never_in_the_config_json()
    {
        await AddKeyAsync();

        Assert.DoesNotContain(FakeKey, _metadata.Load() ?? string.Empty, StringComparison.Ordinal);
        var login = Assert.Single(_vault.List());
        Assert.Equal(FakeKey, (await _vault.ReadSecretAsync(login.Id))!.Reveal());
    }

    [Fact]
    public void Without_a_key_start_says_where_to_add_one_and_connects_nothing()
    {
        var voice = Voice();

        voice.StartCommand.Execute(null);

        Assert.False(voice.HasKey);
        Assert.Contains("Saved logins", voice.KeyStatus, StringComparison.Ordinal);
        Assert.Contains("Saved logins", voice.Status, StringComparison.Ordinal);
        Assert.Null(_socket.Endpoint);
    }

    [Fact]
    public void The_picker_offers_only_built_in_voices_and_redaction_starts_on()
    {
        var voice = Voice();

        Assert.Equal(GrokVoices.BuiltIn, voice.Voices);
        Assert.Equal("eve", voice.SelectedVoice.Id);
        Assert.True(voice.RedactBodies);
        voice.SelectedVoice = new GrokVoice("my-cloned-voice", "custom");
        Assert.Equal("eve", voice.SelectedVoice.Id);
    }

    [Fact]
    public async Task Choosing_a_voice_is_saved_on_the_xai_login_and_used_next_time()
    {
        await AddKeyAsync();
        var voice = Voice();

        voice.SelectedVoice = GrokVoices.Find("ara");
        await Task.Yield();

        Assert.Equal("ara", Assert.Single(_vault.List()).Field("voice"));
        Assert.Equal("ara", Voice().SelectedVoice.Id);
        Assert.DoesNotContain(FakeKey, _metadata.Load() ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_starts_with_the_vault_key_the_chosen_voice_and_the_read_only_tools()
    {
        await AddKeyAsync("rex");
        var voice = Voice();

        await voice.StartCommand.ExecuteAsync();

        Assert.True(voice.IsRunning);
        Assert.Equal(FakeKey, _socket.BearerKey);
        var update = _socket.SentJson.First();
        Assert.Equal("rex", update["session"]!["voice"]!.GetValue<string>());
        Assert.Equal(4, update["session"]!["tools"]!.AsArray().Count);
        Assert.All(_socket.Sent, s => Assert.DoesNotContain(FakeKey, s, StringComparison.Ordinal));
        Assert.DoesNotContain(FakeKey, voice.Status, StringComparison.Ordinal);
        await voice.StopCommand.ExecuteAsync();
        Assert.False(voice.IsRunning);
    }

    [Fact]
    public async Task A_typed_question_goes_out_and_the_answer_and_tool_use_show_in_the_panel()
    {
        await AddKeyAsync();
        var voice = Voice();
        await voice.StartCommand.ExecuteAsync();
        var session = (GrokVoiceSession)typeof(VoiceViewModel).GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(voice)!;

        voice.AskText = "Which queues have dead letters?";
        await voice.AskCommand.ExecuteAsync();
        await session.HandleEventAsync(new JsonObject { ["type"] = "response.function_call_arguments.done", ["name"] = "summarize_dead_letters", ["call_id"] = "c1", ["arguments"] = "{}" }.ToJsonString());
        await session.HandleEventAsync("""{"type":"response.output_audio_transcript.delta","delta":"Payments has three, "}""");
        await session.HandleEventAsync("""{"type":"response.output_audio_transcript.delta","delta":"billing has one."}""");

        Assert.Equal(["You", "Grok"], voice.Lines.Select(l => l.Who));
        Assert.Equal("Payments has three, billing has one.", voice.Lines[1].Text);
        Assert.StartsWith("summarize_dead_letters: 4 dead-lettered messages", Assert.Single(voice.ToolCalls), StringComparison.Ordinal);
        Assert.Equal(string.Empty, voice.AskText);
        await voice.StopCommand.ExecuteAsync();
    }

    [Fact]
    public async Task With_no_namespace_connected_a_tool_call_says_to_connect_first()
    {
        _inventory = null;
        await AddKeyAsync();
        var voice = Voice();
        await voice.StartCommand.ExecuteAsync();
        var session = (GrokVoiceSession)typeof(VoiceViewModel).GetField("_session", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(voice)!;

        await session.HandleEventAsync("""{"type":"response.function_call_arguments.done","name":"list_entities","call_id":"c1","arguments":"{}"}""");

        Assert.Contains("No namespace is connected", _socket.Sent.Last(s => s.Contains("function_call_output", StringComparison.Ordinal)), StringComparison.Ordinal);
        await voice.StopCommand.ExecuteAsync();
    }

    [Fact]
    public async Task Push_to_talk_needs_audio_and_sends_only_while_held()
    {
        await AddKeyAsync();
        var audio = new RecordingAudio();
        var voice = Voice(audio);
        await voice.StartCommand.ExecuteAsync();
        Assert.True(voice.CanTalk);

        await voice.TalkPressedAsync();
        audio.Speak([1, 2]);
        await voice.TalkReleasedAsync();
        audio.Speak([3, 4]);

        Assert.Equal(["session.update", "input_audio_buffer.clear", "input_audio_buffer.append", "input_audio_buffer.commit", "response.create"], _socket.SentTypes);
        await voice.StopCommand.ExecuteAsync();

        var silent = Voice();
        Assert.False(silent.CanTalk);
        Assert.NotNull(silent.AudioNote);
    }

    [AvaloniaFact]
    public void The_voice_tab_shows_the_picker_redaction_and_the_read_only_note()
    {
        var main = new MainViewModel(MainViewModel.DefaultCatalog(false)) { SelectedTab = MainViewModel.VoiceTab };
        var window = new MainWindow { DataContext = main };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var picker = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "VoicePicker");
        var redact = window.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "VoiceRedactBodies");
        var note = window.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Name == "VoiceReadOnlyText");

        Assert.Equal(GrokVoices.BuiltIn.Count, picker.ItemCount);
        Assert.True(redact.IsChecked);
        Assert.Contains("cannot send, move", note.Text, StringComparison.Ordinal);
    }

    private sealed class RecordingAudio : IVoiceAudio
    {
        private Action<byte[]>? _capture;

        public string? UnavailableReason => null;

        public void StartCapture(Action<byte[]> onChunk) => _capture = onChunk;

        public void StopCapture() => _capture = null;

        public void Play(byte[] pcm16)
        {
        }

        public void StopPlayback()
        {
        }

        public void Speak(byte[] chunk) => _capture?.Invoke(chunk);
    }
}
