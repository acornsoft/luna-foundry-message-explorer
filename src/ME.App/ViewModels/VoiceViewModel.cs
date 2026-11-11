// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.ReadTools;
using Acornsoft.MessageExplorer.Voice;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>One line of the voice transcript.</summary>
public sealed class VoiceLine(VoiceSpeaker speaker, string text) : ViewModelBase
{
    private string _text = text;

    public VoiceSpeaker Speaker { get; } = speaker;

    public string Who => Speaker == VoiceSpeaker.User ? "You" : "Grok";

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }
}

/// <summary>
/// The Voice panel: push-to-talk Grok Voice over the connected namespace. The model's tools are the same four
/// read-only tools as the MCP server, so it can list and check counts and can never move anything. The xAI key
/// is read from the credential vault only when a session starts, and never shown or logged. Message bodies are
/// redacted by default. The voice picker offers xAI's built-in voices only.
/// </summary>
public sealed class VoiceViewModel : ViewModelBase
{
    private readonly ICredentialVault _vault;
    private readonly Func<IEntityInventory?> _inventory;
    private readonly Func<string> _sourceLabel;
    private readonly Func<IVoiceSocket> _socketFactory;
    private readonly IVoiceAudio _audio;
    private readonly IVoiceTokenMinter? _tokens;
    private readonly IUiScheduler _ui;
    private readonly object _appendGate = new();
    private Task _appends = Task.CompletedTask;
    private GrokVoiceSession? _session;
    private GrokVoice _selectedVoice;
    private bool _redactBodies = true;
    private string _status = "Not started.";
    private string _askText = string.Empty;
    private bool _talking;
    private string? _credential;

    public VoiceViewModel(
        ICredentialVault vault,
        Func<IEntityInventory?> inventory,
        Func<string> sourceLabel,
        Func<IVoiceSocket>? socketFactory = null,
        IVoiceAudio? audio = null,
        IUiScheduler? scheduler = null,
        IVoiceTokenMinter? tokens = null)
    {
        _tokens = tokens;
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _sourceLabel = sourceLabel ?? throw new ArgumentNullException(nameof(sourceLabel));
        _socketFactory = socketFactory ?? (() => new ClientVoiceSocket());
        _audio = audio ?? new NoVoiceAudio();
        _ui = scheduler ?? InlineUiScheduler.Instance;
        _selectedVoice = GrokVoices.Find(KeyLogin?.Field("voice"));
        StartCommand = new AsyncCommand(StartAsync, () => !IsRunning);
        StopCommand = new AsyncCommand(StopAsync, () => IsRunning);
        AskCommand = new AsyncCommand(AskAsync, () => IsRunning && !string.IsNullOrWhiteSpace(AskText));
        _vault.Changed += (_, _) => _ui.Post(() =>
        {
            OnPropertyChanged(nameof(KeyStatus));
            OnPropertyChanged(nameof(HasKey));
        });
    }

    /// <summary>xAI's built-in voices, and nothing else.</summary>
    public IReadOnlyList<GrokVoice> Voices => GrokVoices.BuiltIn;

    public GrokVoice SelectedVoice
    {
        get => _selectedVoice;
        set
        {
            var voice = GrokVoices.IsBuiltIn(value?.Id) ? value! : GrokVoices.Default;
            if (Set(ref _selectedVoice, voice))
                _ = SaveVoiceAsync(voice);
        }
    }

    /// <summary>On by default: message bodies and property bags never reach the voice model, and text goes through the PII net.</summary>
    public bool RedactBodies
    {
        get => _redactBodies;
        set => Set(ref _redactBodies, value);
    }

    public bool HasKey => KeyLogin is { HasSecret: true };

    public string KeyStatus => HasKey
        ? $"xAI key: saved in {_vault.SecureStoreName}. It is read only when a session starts."
        : "No xAI key yet. Add one in Saved logins > xAI (Grok Voice). It is kept only in the secure store.";

    public string ReadOnlyNote => "Read-only: the voice can list queues, topics and subscriptions and check their counts. It cannot read message bodies, and it cannot send, move, resubmit, purge or delete anything.";

    public string? AudioNote => _audio.UnavailableReason;

    public bool CanTalk => IsRunning && _audio.UnavailableReason is null;

    /// <summary>How the voice connection was authenticated (shown under the status).</summary>
    public string? Credential
    {
        get => _credential;
        private set => Set(ref _credential, value);
    }

    public bool IsRunning => _session is not null;

    public bool IsTalking
    {
        get => _talking;
        private set => Set(ref _talking, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string AskText
    {
        get => _askText;
        set
        {
            if (Set(ref _askText, value))
                AskCommand.RaiseCanExecuteChanged();
        }
    }

    public ObservableCollection<VoiceLine> Lines { get; } = [];

    public ObservableCollection<string> ToolCalls { get; } = [];

    public AsyncCommand StartCommand { get; }

    public AsyncCommand StopCommand { get; }

    public AsyncCommand AskCommand { get; }

    private LoginEntry? KeyLogin => _vault.List().FirstOrDefault(l => l.Kind == LoginKind.XaiApiKey);

    private async Task StartAsync()
    {
        if (KeyLogin is not { } login || !login.HasSecret)
        {
            Status = new VoiceKeyMissingException().Message;
            return;
        }

        var label = _sourceLabel();
        var tools = new ReadOnlyToolset(ReadInventoryAsync, label);
        var session = new GrokVoiceSession(_socketFactory(), tools, new VoiceOptions(SelectedVoice.Id, RedactBodies), ct => _vault.ReadSecretAsync(login.Id, ct), _tokens);
        session.Transcript += (_, line) => _ui.Post(() => AddTranscript(line.Speaker, line.Text));
        session.ToolCalled += (_, call) => _ui.Post(() => ToolCalls.Add($"{call.Name}: {call.Summary}"));
        session.Problem += (_, text) => _ui.Post(() => Status = text);
        session.Audio += (_, pcm) => _audio.Play(pcm);
        session.ResponseDone += (_, _) => _ui.Post(() => Status = $"Listening to {SelectedVoice.Id}. Ask about {label}.");
        try
        {
            Status = "Connecting...";
            await session.StartAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is VoiceKeyMissingException or VoiceKeyRejectedException or System.Net.WebSockets.WebSocketException or HttpRequestException or OperationCanceledException)
        {
            Status = exception is VoiceKeyMissingException or VoiceKeyRejectedException ? exception.Message : "Could not reach xAI: " + SecretRedactor.Redact(exception.Message);
            await session.DisposeAsync().ConfigureAwait(true);
            return;
        }

        _session = session;
        _vault.MarkUsed(login.Id);
        Credential = session.UsesShortLivedToken
            ? $"Signed in to the voice service with a short-lived token (until {session.TokenExpiresAt!.Value.ToLocalTime():HH:mm}); the API key stayed off the voice connection."
            : "Signed in to the voice service with the API key.";
        Status = $"Connected with voice {SelectedVoice.Id}. Ask about {label}.";
        RaiseRunning();
    }

    private Task<IReadOnlyList<EntitySnapshot>> ReadInventoryAsync(CancellationToken cancellationToken) =>
        _inventory() is { } inventory
            ? InventorySources.ReadAllAsync(inventory, cancellationToken)
            : throw new BrokerException(BrokerErrorKind.NotFound, "No namespace is connected. Choose one with the namespace switcher (top left) first.");

    private async Task StopAsync()
    {
        if (_session is { } session)
        {
            _session = null;
            _audio.StopCapture();
            _audio.StopPlayback();
            await session.DisposeAsync().ConfigureAwait(true);
        }

        IsTalking = false;
        Credential = null;
        Status = "Stopped.";
        RaiseRunning();
    }

    private async Task AskAsync()
    {
        if (_session is not { } session)
            return;
        var text = AskText;
        AskText = string.Empty;
        await session.AskAsync(text).ConfigureAwait(true);
    }

    /// <summary>Push-to-talk pressed (button held or Space).</summary>
    public async Task TalkPressedAsync()
    {
        if (!CanTalk || IsTalking || _session is not { } session)
            return;
        IsTalking = true;
        _audio.StopPlayback();
        await session.BeginTalkAsync().ConfigureAwait(true);
        lock (_appendGate)
            _appends = Task.CompletedTask;
        _audio.StartCapture(chunk => QueueAppend(session, chunk));
        if (_audio.UnavailableReason is { } reason)
        {
            IsTalking = false;
            Status = reason;
            OnPropertyChanged(nameof(AudioNote));
            OnPropertyChanged(nameof(CanTalk));
            return;
        }

        Status = "Listening... release to send.";
    }

    /// <summary>Push-to-talk released: the last partial chunk and every queued chunk go out before the commit.</summary>
    public async Task TalkReleasedAsync()
    {
        if (!IsTalking || _session is not { } session)
            return;
        IsTalking = false;
        _audio.StopCapture();
        Task pending;
        lock (_appendGate)
            pending = _appends;
        Status = "Thinking...";
        try
        {
            await pending.ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is System.Net.WebSockets.WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            Status = "Some audio did not reach xAI: " + SecretRedactor.Redact(exception.Message);
        }

        await session.EndTalkAsync().ConfigureAwait(true);
    }

    /// <summary>Microphone chunks arrive on the audio thread; they are sent one after another, in order.</summary>
    private void QueueAppend(GrokVoiceSession session, byte[] chunk)
    {
        lock (_appendGate)
            _appends = _appends.ContinueWith(_ => session.AppendAudioAsync(chunk), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    private void AddTranscript(VoiceSpeaker speaker, string text)
    {
        if (speaker == VoiceSpeaker.Assistant && Lines.Count > 0 && Lines[^1].Speaker == VoiceSpeaker.Assistant)
            Lines[^1].Text += text;
        else
            Lines.Add(new VoiceLine(speaker, text));
    }

    private async Task SaveVoiceAsync(GrokVoice voice)
    {
        if (KeyLogin is not { } login)
            return;
        var fields = new Dictionary<string, string>(login.Fields, StringComparer.Ordinal) { ["voice"] = voice.Id };
        try
        {
            await _vault.UpdateAsync(login.Id, new LoginDraft(login.DisplayName, login.Kind, fields, login.AccountId), null).ConfigureAwait(true);
        }
        catch (LoginRejectedException exception)
        {
            Status = exception.Message;
        }
    }

    private void RaiseRunning()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanTalk));
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        AskCommand.RaiseCanExecuteChanged();
    }
}
