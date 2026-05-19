# HR Interview Voice Agent (Blazor WASM + ASP.NET Core)

An AI-powered HR interview agent using **Azure Voice Live** service with a **Blazor WebAssembly** frontend and **ASP.NET Core** backend. Based on the [Voice Live Universal Assistant](https://github.com/microsoft-foundry/voicelive-samples/tree/main/voice-live-universal-assistant) sample.

## Architecture

```
┌─────────────────────────┐   WebSocket    ┌──────────────────────┐   Voice Live SDK   ┌──────────────┐
│  Blazor WASM Client     │◄──────────────►│  ASP.NET Core Server │◄─────────────────►│  Azure Voice  │
│  (C# + JS Interop)      │  JSON + PCM16  │  (VoiceLiveHandler)  │   PCM16 + events  │  Live Service │
│                          │               │                      │                    │              │
│  Audio: JS AudioWorklet  │               │  WebSocket middleware │                    │              │
│  UI: Razor components    │               │  Session management   │                    │              │
└─────────────────────────┘               └──────────────────────┘                    └──────────────┘
```

## Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- An Azure AI Services resource with Voice Live API access
- Azure CLI (`az login`) for DefaultAzureCredential, or an API key

## Quick Start

### 1. Configure the server

```bash
cd Server
cp .env.sample .env
# Edit .env with your Azure Voice Live endpoint
```

### 2. Build and run

```bash
# From the solution root
dotnet build
cd Server
dotnet run
```

Open [http://localhost:8000](http://localhost:8000) in your browser.

### 3. For development (hot-reload)

```bash
# Terminal 1: Run the server
cd Server
dotnet watch run

# Terminal 2: The Blazor WASM client is served from the Server's wwwroot.
# For client development, publish the client first:
cd Client
dotnet publish -o ../Server/wwwroot
```

## Project Structure

```
VoiceAgentWeb/
├── VoiceAgentWeb.sln              # Solution file
├── Server/                        # ASP.NET Core backend
│   ├── Program.cs                 # WebSocket server + REST endpoints
│   ├── VoiceLiveHandler.cs        # Azure Voice Live SDK bridge
│   ├── SessionConfig.cs           # Session configuration model
│   ├── .env.sample                # Environment variable template
│   └── VoiceAgentWeb.Server.csproj
├── Client/                        # Blazor WASM frontend
│   ├── Pages/
│   │   └── Interview.razor        # Main interview UI component
│   ├── Services/
│   │   └── VoiceSessionService.cs # WebSocket + audio coordination
│   ├── wwwroot/
│   │   ├── js/
│   │   │   ├── audio-interop.js   # JS interop for audio (ES module)
│   │   │   ├── audio-capture-worklet.js   # Mic capture AudioWorklet
│   │   │   └── audio-playback-worklet.js  # Playback AudioWorklet
│   │   └── css/app.css            # Interview UI styles
│   └── VoiceAgentWeb.Client.csproj
└── README.md
```

## How It Works

1. **Blazor WASM Client** renders the interview UI and manages:
   - Audio capture via JavaScript interop (`getUserMedia` + AudioWorklet at 24kHz PCM16)
   - WebSocket connection to the server (pure C# `ClientWebSocket`)
   - Real-time transcript display

2. **ASP.NET Core Server** handles:
   - WebSocket connections from the client
   - Bridging audio/text to Azure Voice Live SDK
   - Session lifecycle management
   - Serving the Blazor WASM static files

3. **Azure Voice Live** provides:
   - Real-time speech-to-text and text-to-speech
   - AI model integration (GPT-realtime)
   - Voice Activity Detection, echo cancellation, noise reduction

## Connection Modes

| Mode  | Description |
|-------|-------------|
| model | Direct model access (default) — uses gpt-realtime with HR interviewer instructions |
| agent | Foundry Agent Service integration — agent defines instructions and tools |

## Authentication

- **Recommended**: `DefaultAzureCredential` — run `az login` for local development
- **Fallback**: Set `AZURE_VOICELIVE_API_KEY` in `.env`

## Customization

Edit the HR interview instructions in `Server/HrInterviewAgent.cs` (the `DefaultInstructions` constant) or set `Instructions` in `appsettings.Development.json` to customize the interviewer's behavior.

## License

This project is licensed under the [MIT License](LICENSE).
