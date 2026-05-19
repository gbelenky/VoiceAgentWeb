using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.FileProviders;
using Microsoft.Identity.Web;
using VoiceAgentWeb.Server;

// ---------------------------------------------------------------------------
// Environment
// ---------------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://0.0.0.0:8000");

var config = builder.Configuration;

// ---------------------------------------------------------------------------
// Entra ID (Azure AD) Authentication
// ---------------------------------------------------------------------------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(options =>
    {
        options.Events = new JwtBearerEvents
        {
            // Allow token from query string for WebSocket connections
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/ws"))
                {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    },
    options =>
    {
        var tenantId = config["AzureAd:TenantId"] ?? "common";
        var clientId = config["AzureAd:ClientId"] ?? "";
        options.Instance = "https://login.microsoftonline.com/";
        options.TenantId = tenantId;
        options.ClientId = clientId;
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("VoiceLive");

// ---------------------------------------------------------------------------
// Shared state
// ---------------------------------------------------------------------------
var handlers = new ConcurrentDictionary<string, VoiceLiveHandler>();
object? sharedCredential = null;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = false,
};

// HR Interview Agent — encapsulates persona, instructions, and tools
var vlConfig = config.GetSection("VoiceLive");
var hrAgent = new HrInterviewAgent(
    instructionsOverride: NullIfEmpty(vlConfig["Instructions"]),
    greetingOverride: NullIfEmpty(vlConfig["GreetingText"]));

// ---------------------------------------------------------------------------
// Credential
// ---------------------------------------------------------------------------
object GetCredential()
{
    if (sharedCredential != null) return sharedCredential;
    var apiKey = NullIfEmpty(vlConfig["ApiKey"]);
    if (apiKey != null)
    {
        sharedCredential = new AzureKeyCredential(apiKey);
        logger.LogInformation("Using API key credential");
    }
    else
    {
        sharedCredential = new DefaultAzureCredential();
        logger.LogInformation("Using DefaultAzureCredential");
    }
    return sharedCredential;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
string CfgOrDefault(string key, string defaultValue)
{
    var v = vlConfig[key];
    return !string.IsNullOrWhiteSpace(v) ? v : defaultValue;
}

string? NullIfEmpty(string? value)
    => string.IsNullOrWhiteSpace(value) ? null : value;

// ---------------------------------------------------------------------------
// Middleware
// ---------------------------------------------------------------------------
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();

// WebSocket endpoint (requires authentication)
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/ws") && context.WebSockets.IsWebSocketRequest)
    {
        // Validate authentication for WebSocket connections
        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = 401;
            return;
        }

        var pathSegments = context.Request.Path.Value?.TrimEnd('/').Split('/');
        var clientId = pathSegments?.LastOrDefault() ?? "unknown";
        var userName = context.User.FindFirst("name")?.Value
                    ?? context.User.FindFirst("preferred_username")?.Value
                    ?? "unknown";

        using var ws = await context.WebSockets.AcceptWebSocketAsync();
        logger.LogInformation("Client {ClientId} connected (user: {User})", clientId, userName);

        var sendLock = new SemaphoreSlim(1, 1);
        async Task SendToClient(Dictionary<string, object> msg)
        {
            await sendLock.WaitAsync();
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    var json = JsonSerializer.Serialize(msg, jsonOptions);
                    var bytes = Encoding.UTF8.GetBytes(json);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                logger.LogError("Failed to send to {ClientId}: {Error}", clientId, ex.Message);
            }
            finally
            {
                sendLock.Release();
            }
        }

        try
        {
            await HandleWebSocketAsync(ws, clientId, SendToClient);
        }
        finally
        {
            await CleanupClientAsync(clientId);
            sendLock.Dispose();
            logger.LogInformation("Client {ClientId} disconnected", clientId);
        }
    }
    else
    {
        await next();
    }
});

// Static files — serve Blazor WASM from wwwroot
string? staticDir = null;
foreach (var candidate in new[]
{
    Path.Combine(AppContext.BaseDirectory, "wwwroot"),
    Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
})
{
    if (Directory.Exists(candidate))
    {
        staticDir = Path.GetFullPath(candidate);
        break;
    }
}

PhysicalFileProvider? fileProvider = null;
if (staticDir != null)
{
    fileProvider = new PhysicalFileProvider(staticDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = fileProvider,
        ServeUnknownFileTypes = true
    });
    logger.LogInformation("Serving static files from {Dir}", staticDir);
}

// ---------------------------------------------------------------------------
// REST endpoints
// ---------------------------------------------------------------------------
app.MapGet("/health", () => new { status = "healthy", service = "voicelive-hr-interview" });

app.MapGet("/me", (HttpContext ctx) => new
{
    name = ctx.User.FindFirst("name")?.Value ?? ctx.User.FindFirst("preferred_username")?.Value,
    email = ctx.User.FindFirst("preferred_username")?.Value,
}).RequireAuthorization();

app.MapGet("/config", () =>
{
    var apiKey = NullIfEmpty(vlConfig["ApiKey"]);
    return new
    {
        mode = CfgOrDefault("Mode", "model"),
        model = CfgOrDefault("Model", "gpt-realtime"),
        voice = CfgOrDefault("Voice", "en-US-Ava:DragonHDLatestNeural"),
        voiceType = CfgOrDefault("VoiceType", "azure-standard"),
        instructions = hrAgent.Instructions,
        tools = hrAgent.Tools.Select(t => t.Name).ToArray(),
        agentName = CfgOrDefault("AgentName", ""),
        project = CfgOrDefault("Project", ""),
        authMethod = apiKey != null ? "api_key" : "default_credential",
    };
}).RequireAuthorization();

// SPA fallback
if (fileProvider != null)
{
    app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fileProvider });
}

// ---------------------------------------------------------------------------
// Graceful shutdown
// ---------------------------------------------------------------------------
app.Lifetime.ApplicationStopping.Register(() =>
{
    logger.LogInformation("Shutting down — cleaning up {Count} active handlers", handlers.Count);
    foreach (var handler in handlers.Values)
    {
        handler.StopAsync().GetAwaiter().GetResult();
    }
    handlers.Clear();
});

logger.LogInformation("Starting HR Interview Voice Agent on http://localhost:8000");
app.Run();

// ===========================================================================
// WebSocket handling
// ===========================================================================
async Task HandleWebSocketAsync(WebSocket ws, string clientId, Func<Dictionary<string, object>, Task> sendToClient)
{
    var buffer = new byte[8192];
    using var ms = new MemoryStream();

    while (ws.State == WebSocketState.Open)
    {
        WebSocketReceiveResult result;
        try
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        }
        catch (WebSocketException)
        {
            break;
        }

        if (result.MessageType == WebSocketMessageType.Close)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); }
            catch { }
            break;
        }

        ms.Write(buffer, 0, result.Count);
        if (!result.EndOfMessage) continue;

        var text = Encoding.UTF8.GetString(ms.ToArray());
        ms.SetLength(0);

        try
        {
            var node = JsonNode.Parse(text);
            var msgType = node?["type"]?.GetValue<string>();

            switch (msgType)
            {
                case "start_session":
                    await StartSessionAsync(clientId, node!, sendToClient);
                    break;
                case "stop_session":
                    await StopSessionAsync(clientId, sendToClient);
                    break;
                case "audio_chunk":
                    if (handlers.TryGetValue(clientId, out var audioHandler))
                    {
                        var data = node?["data"]?.GetValue<string>();
                        if (data != null)
                            await audioHandler.SendAudioAsync(data);
                    }
                    break;
                case "interrupt":
                    if (handlers.TryGetValue(clientId, out var interruptHandler))
                        await interruptHandler.InterruptAsync();
                    break;
                case "send_text":
                    if (handlers.TryGetValue(clientId, out var textHandler))
                    {
                        var messageText = node?["text"]?.GetValue<string>();
                        if (messageText != null)
                            await textHandler.SendTextAsync(messageText);
                    }
                    break;
                default:
                    logger.LogWarning("Unknown message type from {ClientId}: {Type}", clientId, msgType);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError("Error handling message from {ClientId}: {Error}", clientId, ex.Message);
            await sendToClient(new Dictionary<string, object> { ["type"] = "error", ["message"] = ex.Message });
        }
    }
}

// ===========================================================================
// Session lifecycle
// ===========================================================================
async Task StartSessionAsync(string clientId, JsonNode msg, Func<Dictionary<string, object>, Task> sendToClient)
{
    try
    {
        var endpoint = vlConfig["Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new InvalidOperationException("Missing VoiceLive:Endpoint in configuration");

        var credential = GetCredential();
        var config = BuildSessionConfig(msg);

        var handlerLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger<VoiceLiveHandler>();
        var handler = new VoiceLiveHandler(clientId, endpoint, credential, sendToClient, config, hrAgent, handlerLogger);

        if (handlers.TryRemove(clientId, out var prev))
            await prev.StopAsync();

        handlers[clientId] = handler;
        await handler.StartAsync();
        logger.LogInformation("Session started for {ClientId} in {Mode} mode", clientId, config.Mode);
    }
    catch (Exception ex)
    {
        logger.LogError("Failed to start session for {ClientId}: {Error}", clientId, ex.Message);
        await sendToClient(new Dictionary<string, object> { ["type"] = "error", ["message"] = ex.Message });
    }
}

async Task StopSessionAsync(string clientId, Func<Dictionary<string, object>, Task> sendToClient)
{
    if (handlers.TryRemove(clientId, out var handler))
        await handler.StopAsync();
    await sendToClient(new Dictionary<string, object> { ["type"] = "session_stopped" });
    logger.LogInformation("Session stopped for {ClientId}", clientId);
}

async Task CleanupClientAsync(string clientId)
{
    if (handlers.TryRemove(clientId, out var handler))
        await handler.StopAsync();
}

// ===========================================================================
// Session config builder
// ===========================================================================
SessionConfig BuildSessionConfig(JsonNode msg)
{
    return new SessionConfig
    {
        Mode = GetStringOrCfg(msg, "mode", "Mode", "model"),
        Model = GetStringOrCfg(msg, "model", "Model", "gpt-realtime"),
        Voice = GetStringOrCfg(msg, "voice", "Voice", "en-US-Ava:DragonHDLatestNeural"),
        VoiceType = GetStringOrCfg(msg, "voice_type", "VoiceType", "azure-standard"),
        Instructions = GetStringOrCfg(msg, "instructions", "Instructions", hrAgent.Instructions),
        Temperature = GetFloatOrDefault(msg, "temperature", float.Parse(CfgOrDefault("Temperature", "0.7"))),
        VadType = GetStringOrCfg(msg, "vad_type", "VadType", "azure_semantic"),
        NoiseReduction = GetBoolOrDefault(msg, "noise_reduction", true),
        EchoCancellation = GetBoolOrDefault(msg, "echo_cancellation", true),
        AgentName = GetStringOrCfg(msg, "agent_name", "AgentName", null) ?? "",
        ProjectName = GetStringOrCfg(msg, "project", "Project", null) ?? "",
        AgentVersion = GetStringOrCfg(msg, "agent_version", "AgentVersion", null),
        ConversationId = GetStringOrDefault(msg, "conversation_id", null),
        FoundryResourceOverride = GetStringOrCfg(msg, "foundry_resource_override", "FoundryResourceOverride", null),
        AuthIdentityClientId = GetStringOrCfg(msg, "auth_identity_client_id", "AuthIdentityClientId", null),
        ProactiveGreeting = GetBoolOrDefault(msg, "proactive_greeting", true),
        GreetingType = GetStringOrDefault(msg, "greeting_type", "llm"),
        GreetingText = GetStringOrDefault(msg, "greeting_text", "") ?? "",
    };
}

// ===========================================================================
// JSON extraction helpers
// ===========================================================================
string GetStringOrDefault(JsonNode msg, string key, string? defaultValue)
{
    var v = msg[key];
    if (v != null)
    {
        var s = v.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(s)) return s;
    }
    return defaultValue ?? "";
}

string GetStringOrCfg(JsonNode msg, string key, string cfgKey, string? defaultValue)
{
    var v = msg[key];
    if (v != null)
    {
        try
        {
            var s = v.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        catch { }
    }
    return CfgOrDefault(cfgKey, defaultValue ?? "");
}

bool GetBoolOrDefault(JsonNode msg, string key, bool defaultValue)
{
    var v = msg[key];
    if (v != null)
    {
        try { return v.GetValue<bool>(); } catch { }
    }
    return defaultValue;
}

float GetFloatOrDefault(JsonNode msg, string key, float defaultValue)
{
    var v = msg[key];
    if (v != null)
    {
        try { return v.GetValue<float>(); } catch { }
        try { return float.Parse(v.GetValue<string>()); } catch { }
    }
    return defaultValue;
}

app.Run();
