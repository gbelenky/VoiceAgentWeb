namespace VoiceAgentWeb.Server;

/// <summary>
/// Session configuration for the Voice Live connection.
/// </summary>
public class SessionConfig
{
    public string Mode { get; set; } = "model";
    public string Model { get; set; } = "gpt-realtime";
    public string Voice { get; set; } = "en-US-Ava:DragonHDLatestNeural";
    public string VoiceType { get; set; } = "azure-standard";
    public string Instructions { get; set; } = "";
    public float Temperature { get; set; } = 0.7f;

    public string VadType { get; set; } = "azure_semantic";
    public bool NoiseReduction { get; set; } = true;
    public bool EchoCancellation { get; set; } = true;



    public string? AgentName { get; set; }
    public string? ProjectName { get; set; }
    public string? AgentVersion { get; set; }
    public string? ConversationId { get; set; }
    public string? FoundryResourceOverride { get; set; }
    public string? AuthIdentityClientId { get; set; }

    public bool ProactiveGreeting { get; set; } = true;
    public string GreetingType { get; set; } = "llm";
    public string GreetingText { get; set; } = "";
}
