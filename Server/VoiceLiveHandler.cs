using System.Text;
using Azure;
using Azure.AI.VoiceLive;
using Azure.Core;
using Microsoft.Extensions.Logging;

namespace VoiceAgentWeb.Server;

/// <summary>
/// Bridges the browser WebSocket to the Azure Voice Live SDK.
/// Manages a single VoiceLive session for one WebSocket client.
/// </summary>
public class VoiceLiveHandler
{
    private readonly string _clientId;
    private readonly string _endpoint;
    private readonly object _credential;
    private readonly Func<Dictionary<string, object>, Task> _sendMessage;
    private readonly SessionConfig _config;
    private readonly HrInterviewAgent _agent;
    private readonly ILogger _logger;

    private VoiceLiveClient? _client;
    private volatile VoiceLiveSession? _session;
    private CancellationTokenSource? _cts;
    private Task? _eventTask;
    private volatile bool _running;
    private bool _greetingSent;
    private string _serviceSessionId = "";
    private readonly StringBuilder _assistantTranscript = new();

    public VoiceLiveHandler(
        string clientId,
        string endpoint,
        object credential,
        Func<Dictionary<string, object>, Task> sendMessage,
        SessionConfig config,
        HrInterviewAgent agent,
        ILogger logger)
    {
        _clientId = clientId;
        _endpoint = endpoint;
        _credential = credential;
        _sendMessage = sendMessage;
        _config = config;
        _agent = agent;
        _logger = logger;
    }

    public Task StartAsync()
    {
        _running = true;
        _cts = new CancellationTokenSource();
        _eventTask = RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task SendAudioAsync(string audioBase64)
    {
        var session = _session;
        if (session != null && _running)
        {
            try
            {
                var audioBytes = Convert.FromBase64String(audioBase64);
                await session.SendInputAudioAsync(audioBytes);
            }
            catch (Exception ex)
            {
                _logger.LogError("[{ClientId}] Error forwarding audio: {Error}", _clientId, ex.Message);
            }
        }
    }

    public async Task SendTextAsync(string text)
    {
        var session = _session;
        if (session != null && _running && !string.IsNullOrWhiteSpace(text))
        {
            try
            {
                await session.SendCommandAsync(BinaryData.FromObjectAsJson(new
                {
                    type = "conversation.item.create",
                    item = new
                    {
                        type = "message",
                        role = "user",
                        content = new[] { new { type = "input_text", text = text.Trim() } },
                    },
                }));
                await session.SendCommandAsync(BinaryData.FromObjectAsJson(new
                {
                    type = "response.create",
                }));
            }
            catch (Exception ex)
            {
                _logger.LogError("[{ClientId}] Error sending text: {Error}", _clientId, ex.Message);
            }
        }
    }

    public async Task InterruptAsync()
    {
        var session = _session;
        if (session != null)
        {
            try
            {
                await session.CancelResponseAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug("[{ClientId}] No response to cancel: {Error}", _clientId, ex.Message);
            }
        }
    }

    public async Task StopAsync()
    {
        _running = false;
        try { _cts?.Cancel(); } catch { }
        if (_eventTask != null)
        {
            try { await _eventTask; } catch { }
        }
        _logger.LogInformation("[{ClientId}] Handler stopped", _clientId);
    }

    public bool IsRunning => _running;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("[{ClientId}] Connecting in {Mode} mode (model={Model}, voice={Voice})",
                _clientId, _config.Mode, _config.Model, _config.Voice);

            _client = CreateClient();

            if (_config.Mode == "agent")
            {
                throw new NotSupportedException("Agent mode requires Azure.AI.VoiceLive 1.1.0-beta.3 or later.");
            }

            var sessionOptions = BuildSessionOptions();
            _session = await _client.StartSessionAsync(sessionOptions, ct);
            _logger.LogInformation("[{ClientId}] Started model session (model={Model}) with {ToolCount} tools",
                _clientId, _config.Model, _agent.Tools.Count);

            await ProcessEventsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation("[{ClientId}] Event loop cancelled", _clientId);
        }
        catch (Exception ex)
        {
            _logger.LogError("[{ClientId}] VoiceLive error: {Error}", _clientId, ex.Message);
            try
            {
                await _sendMessage(new Dictionary<string, object> { ["type"] = "error", ["message"] = ex.Message });
            }
            catch { }
        }
        finally
        {
            _running = false;
            try { _session?.Dispose(); } catch { }
            _session = null;
            _client = null;
        }
    }

    private VoiceLiveClient CreateClient()
    {
        return _credential switch
        {
            AzureKeyCredential keyCred => new VoiceLiveClient(new Uri(_endpoint), keyCred),
            TokenCredential tokenCred => new VoiceLiveClient(new Uri(_endpoint), tokenCred),
            _ => throw new InvalidOperationException($"Unsupported credential type: {_credential.GetType().Name}")
        };
    }

    private VoiceLiveSessionOptions BuildSessionOptions()
    {
        var options = new VoiceLiveSessionOptions
        {
            Model = _config.Model,
            Instructions = !string.IsNullOrWhiteSpace(_config.Instructions) ? _config.Instructions : _agent.Instructions,
            InputAudioFormat = InputAudioFormat.Pcm16,
            OutputAudioFormat = OutputAudioFormat.Pcm16,
            InputAudioNoiseReduction = new AudioNoiseReduction(AudioNoiseReductionType.FarField),
            InputAudioEchoCancellation = new AudioEchoCancellation(),
            InputAudioTranscription = new AudioInputTranscriptionOptions(AudioInputTranscriptionOptionsModel.Whisper1),
            TurnDetection = new ServerVadTurnDetection
            {
                Threshold = 0.5f,
                PrefixPadding = TimeSpan.FromMilliseconds(300),
                SilenceDuration = TimeSpan.FromMilliseconds(500)
            },
        };

        // Voice
        if (_config.VoiceType == "openai")
            options.Voice = new OpenAIVoice(new OAIVoice(_config.Voice));
        else
            options.Voice = new AzureStandardVoice(_config.Voice);

        // Modalities
        options.Modalities.Clear();
        options.Modalities.Add(InteractionModality.Text);
        options.Modalities.Add(InteractionModality.Audio);

        // Tools
        foreach (var toolDef in _agent.GetVoiceLiveToolDefinitions())
            options.Tools.Add(toolDef);

        return options;
    }

    private async Task SendLlmGeneratedGreetingAsync()
    {
        var session = _session;
        if (session == null) return;

        try
        {
            await session.StartResponseAsync();
            _logger.LogInformation("[{ClientId}] LLM-generated greeting triggered", _clientId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[{ClientId}] LLM-generated greeting failed: {Error}", _clientId, ex.Message);
        }
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        var session = _session!;
        await foreach (var serverEvent in session.GetUpdatesAsync(ct))
        {
            if (!_running) break;
            try
            {
                await HandleEventAsync(serverEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError("[{ClientId}] Event handling error: {Error}", _clientId, ex.Message);
            }
        }
    }

    private async Task HandleEventAsync(SessionUpdate serverEvent)
    {
        switch (serverEvent)
        {
            case SessionUpdateSessionCreated created:
                _serviceSessionId = created.Session?.Id ?? "";
                _logger.LogInformation("[{ClientId}] SESSION_CREATED — session_id: {SessionId}", _clientId, _serviceSessionId);
                break;

            case SessionUpdateSessionUpdated:
                if (string.IsNullOrEmpty(_serviceSessionId))
                    _serviceSessionId = _clientId;

                await _sendMessage(new Dictionary<string, object>
                {
                    ["type"] = "session_started",
                    ["session_id"] = _serviceSessionId,
                    ["config"] = new Dictionary<string, object>
                    {
                        ["mode"] = _config.Mode,
                        ["model"] = _config.Model,
                        ["voice"] = _config.Voice,
                    },
                });
                await _sendMessage(new Dictionary<string, object> { ["type"] = "status", ["state"] = "listening" });

                if (_config.ProactiveGreeting && !_greetingSent)
                {
                    _greetingSent = true;
                    await SendLlmGeneratedGreetingAsync();
                }
                break;

            case SessionUpdateInputAudioBufferSpeechStarted:
                await _sendMessage(new Dictionary<string, object> { ["type"] = "status", ["state"] = "listening" });
                await _sendMessage(new Dictionary<string, object> { ["type"] = "stop_playback" });
                try { await _session!.CancelResponseAsync(); } catch { }
                break;

            case SessionUpdateInputAudioBufferSpeechStopped:
                await _sendMessage(new Dictionary<string, object> { ["type"] = "status", ["state"] = "thinking" });
                break;

            case SessionUpdateResponseCreated:
                await _sendMessage(new Dictionary<string, object> { ["type"] = "status", ["state"] = "speaking" });
                break;

            case SessionUpdateResponseAudioDelta audioDelta:
                var delta = audioDelta.Delta;
                if (delta != null)
                {
                    var audioBytes = delta.ToArray();
                    if (audioBytes.Length > 0)
                    {
                        await _sendMessage(new Dictionary<string, object>
                        {
                            ["type"] = "audio_data",
                            ["data"] = Convert.ToBase64String(audioBytes),
                            ["format"] = "pcm16",
                            ["sampleRate"] = 24000,
                            ["channels"] = 1,
                        });
                    }
                }
                break;

            case SessionUpdateResponseAudioDone:
                _logger.LogDebug("[{ClientId}] Audio response complete", _clientId);
                break;

            case SessionUpdateResponseDone:
                if (_assistantTranscript.Length > 0)
                {
                    await _sendMessage(new Dictionary<string, object>
                    {
                        ["type"] = "transcript",
                        ["role"] = "assistant",
                        ["text"] = _assistantTranscript.ToString(),
                        ["isFinal"] = true,
                    });
                    _assistantTranscript.Clear();
                }
                await _sendMessage(new Dictionary<string, object> { ["type"] = "status", ["state"] = "listening" });
                break;

            case SessionUpdateConversationItemInputAudioTranscriptionCompleted transcription:
                var userText = transcription.Transcript;
                if (!string.IsNullOrWhiteSpace(userText))
                {
                    await _sendMessage(new Dictionary<string, object>
                    {
                        ["type"] = "transcript",
                        ["role"] = "user",
                        ["text"] = userText,
                        ["isFinal"] = true,
                    });
                }
                break;

            case SessionUpdateResponseAudioTranscriptDelta transcriptDelta:
                var deltaText = transcriptDelta.Delta;
                if (!string.IsNullOrEmpty(deltaText))
                {
                    _assistantTranscript.Append(deltaText);
                    await _sendMessage(new Dictionary<string, object>
                    {
                        ["type"] = "transcript",
                        ["role"] = "assistant",
                        ["text"] = _assistantTranscript.ToString(),
                        ["isFinal"] = false,
                    });
                }
                break;

            case SessionUpdateResponseFunctionCallArgumentsDone functionCall:
                await HandleToolCallAsync(functionCall);
                break;

            case SessionUpdateError errorEvent:
                var errorDetails = errorEvent.Error;
                var message = errorDetails?.Message ?? serverEvent.ToString() ?? "";
                var code = errorDetails?.Code ?? "";

                if (code == "response_cancel_not_active" ||
                    message.Contains("no active response", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("[{ClientId}] Benign cancel error: {Msg}", _clientId, message);
                    break;
                }
                _logger.LogError("[{ClientId}] VoiceLive error event: {Msg}", _clientId, message);
                await _sendMessage(new Dictionary<string, object> { ["type"] = "error", ["message"] = message });
                if (message.Contains("invalid state", StringComparison.OrdinalIgnoreCase) ||
                    message.Contains("Aborted", StringComparison.OrdinalIgnoreCase))
                {
                    _running = false;
                }
                break;
        }
    }

    private async Task HandleToolCallAsync(SessionUpdateResponseFunctionCallArgumentsDone functionCall)
    {
        var toolName = functionCall.Name;
        var callId = functionCall.CallId;
        var arguments = functionCall.Arguments ?? "{}";

        _logger.LogInformation("[{ClientId}] Tool call: {Tool} (callId={CallId})", _clientId, toolName, callId);

        await _sendMessage(new Dictionary<string, object>
        {
            ["type"] = "tool_call",
            ["tool"] = toolName,
            ["call_id"] = callId,
        });

        try
        {
            var result = await _agent.ExecuteToolAsync(toolName, arguments);

            _logger.LogInformation("[{ClientId}] Tool result for {Tool}: {Result}",
                _clientId, toolName, result.Length > 200 ? result[..200] + "..." : result);

            // Submit the tool output back to the session
            var outputItem = new FunctionCallOutputItem(callId, result);
            await _session!.AddItemAsync(outputItem);
            await _session!.StartResponseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{ClientId}] Tool execution failed: {Tool}", _clientId, toolName);
            var errorResult = System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message });
            var outputItem = new FunctionCallOutputItem(callId, errorResult);
            await _session!.AddItemAsync(outputItem);
            await _session!.StartResponseAsync();
        }
    }
}
