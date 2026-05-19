using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;

namespace VoiceAgentWeb.Client.Services;

public record TranscriptMessage(string Role, string Text, bool IsFinal);

public class VoiceSessionService : IAsyncDisposable
{
    private readonly IJSRuntime _jsRuntime;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private IJSObjectReference? _audioModule;
    private DotNetObjectReference<VoiceSessionService>? _dotNetRef;

    public event Action<string>? OnStatusChanged;
    public event Action<TranscriptMessage>? OnTranscriptReceived;
    public event Action<string>? OnError;
    public event Action? OnSessionStarted;
    public event Action? OnSessionStopped;

    public string Status { get; private set; } = "idle";
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public VoiceSessionService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task StartSessionAsync(string serverUrl, string? accessToken = null)
    {
        var clientId = Guid.NewGuid().ToString("N")[..8];
        var wsUri = accessToken != null
            ? new Uri($"{serverUrl}/ws/{clientId}?access_token={Uri.EscapeDataString(accessToken)}")
            : new Uri($"{serverUrl}/ws/{clientId}");

        _cts = new CancellationTokenSource();
        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(wsUri, _cts.Token);

        // Start audio capture via JS interop
        _dotNetRef = DotNetObjectReference.Create(this);
        _audioModule = await _jsRuntime.InvokeAsync<IJSObjectReference>(
            "import", "/js/audio-interop.js");
        var micStarted = await _audioModule.InvokeAsync<bool>("startAudioCapture", _dotNetRef);

        if (!micStarted)
        {
            OnError?.Invoke("Failed to access microphone. Please allow microphone access.");
            return;
        }

        // Send start_session message
        var startMsg = JsonSerializer.Serialize(new { type = "start_session" });
        await SendAsync(startMsg);

        // Start receiving messages
        _ = Task.Run(() => ReceiveLoopAsync(_cts.Token));

        SetStatus("connecting");
    }

    public async Task StopSessionAsync()
    {
        if (_audioModule != null)
        {
            await _audioModule.InvokeVoidAsync("stopAudioCapture");
        }

        if (_webSocket?.State == WebSocketState.Open)
        {
            var stopMsg = JsonSerializer.Serialize(new { type = "stop_session" });
            await SendAsync(stopMsg);
        }

        _cts?.Cancel();

        if (_webSocket?.State == WebSocketState.Open)
        {
            try
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch { }
        }

        SetStatus("idle");
        OnSessionStopped?.Invoke();
    }

    public async Task SendTextAsync(string text)
    {
        if (!IsConnected) return;
        var msg = JsonSerializer.Serialize(new { type = "send_text", text });
        await SendAsync(msg);
    }

    [JSInvokable]
    public async Task OnAudioCaptured(string base64Audio)
    {
        if (!IsConnected) return;
        var msg = JsonSerializer.Serialize(new { type = "audio_chunk", data = base64Audio });
        await SendAsync(msg);
    }

    private async Task SendAsync(string message)
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                try
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                ms.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var json = Encoding.UTF8.GetString(ms.ToArray());
                ms.SetLength(0);

                await HandleMessageAsync(json);
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Connection lost: {ex.Message}");
        }
    }

    private async Task HandleMessageAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();

        switch (type)
        {
            case "session_started":
                SetStatus("listening");
                OnSessionStarted?.Invoke();
                break;

            case "status":
                var state = root.GetProperty("state").GetString() ?? "idle";
                SetStatus(state);
                break;

            case "transcript":
                var role = root.GetProperty("role").GetString() ?? "";
                var text = root.GetProperty("text").GetString() ?? "";
                var isFinal = root.GetProperty("isFinal").GetBoolean();
                OnTranscriptReceived?.Invoke(new TranscriptMessage(role, text, isFinal));
                break;

            case "audio_data":
                var audioData = root.GetProperty("data").GetString();
                if (audioData != null && _audioModule != null)
                {
                    await _audioModule.InvokeVoidAsync("playAudio", audioData);
                }
                break;

            case "stop_playback":
                if (_audioModule != null)
                {
                    await _audioModule.InvokeVoidAsync("stopPlayback");
                }
                break;

            case "session_stopped":
                SetStatus("idle");
                OnSessionStopped?.Invoke();
                break;

            case "error":
                var message = root.GetProperty("message").GetString() ?? "Unknown error";
                OnError?.Invoke(message);
                break;
        }
    }

    private void SetStatus(string status)
    {
        Status = status;
        OnStatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_audioModule != null)
        {
            try { await _audioModule.InvokeVoidAsync("stopAudioCapture"); } catch { }
            await _audioModule.DisposeAsync();
        }
        _dotNetRef?.Dispose();
        _webSocket?.Dispose();
        _cts?.Dispose();
    }
}
