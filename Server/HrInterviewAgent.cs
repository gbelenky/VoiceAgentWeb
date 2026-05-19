using System.Text.Json;
using Azure.AI.VoiceLive;

namespace VoiceAgentWeb.Server;

/// <summary>
/// Encapsulates the HR Interview agent logic: persona instructions, tool definitions,
/// and tool execution. This is the single place to modify interview behavior.
/// </summary>
public class HrInterviewAgent
{
    public string Instructions { get; }
    public string GreetingPrompt { get; }
    public IReadOnlyList<ToolDefinition> Tools => _tools;

    private readonly List<ToolDefinition> _tools = new();

    public HrInterviewAgent(string? instructionsOverride = null, string? greetingOverride = null)
    {
        Instructions = instructionsOverride ?? DefaultInstructions;
        GreetingPrompt = greetingOverride ?? DefaultGreetingPrompt;
        RegisterTools();
    }

    // =========================================================================
    // System prompt
    // =========================================================================
    private const string DefaultInstructions = """
        You are a professional HR interviewer conducting a structured job interview.
        Your role is to:
        1. Greet the candidate warmly and make them comfortable.
        2. Ask behavioral and situational interview questions one at a time.
        3. Listen carefully to responses and ask relevant follow-up questions.
        4. Cover topics like: experience, teamwork, problem-solving, leadership, and motivation.
        5. Be professional, encouraging, and conversational.
        6. Keep responses concise — this is a spoken conversation.
        7. After 5-7 questions, wrap up the interview professionally.
        8. When wrapping up, call the "submit_evaluation" tool with your assessment.

        Start by introducing yourself and asking the candidate to tell you about themselves.

        You have access to tools. Use them when appropriate:
        - "get_job_description": Retrieve the job description for context on what to ask.
        - "submit_evaluation": Submit your evaluation of the candidate after the interview.
        """;

    private const string DefaultGreetingPrompt =
        "Greet the candidate warmly and introduce yourself as Alex, the HR interviewer. Briefly explain the interview process.";

    // =========================================================================
    // Tool definitions
    // =========================================================================
    private void RegisterTools()
    {
        _tools.Add(new ToolDefinition
        {
            Name = "get_job_description",
            Description = "Retrieve the job description for the position being interviewed. Call this at the start to tailor your questions.",
            Parameters = new ToolParameters
            {
                Properties = new Dictionary<string, ToolParameterProperty>
                {
                    ["role_id"] = new() { Type = "string", Description = "Optional role identifier. If empty, returns the default job description." }
                },
                Required = Array.Empty<string>(),
            },
        });

        _tools.Add(new ToolDefinition
        {
            Name = "submit_evaluation",
            Description = "Submit your evaluation of the candidate after completing the interview. Include scores and notes for each competency area.",
            Parameters = new ToolParameters
            {
                Properties = new Dictionary<string, ToolParameterProperty>
                {
                    ["overall_score"] = new() { Type = "number", Description = "Overall score from 1-5" },
                    ["communication"] = new() { Type = "number", Description = "Communication skills score from 1-5" },
                    ["technical"] = new() { Type = "number", Description = "Technical competency score from 1-5" },
                    ["teamwork"] = new() { Type = "number", Description = "Teamwork and collaboration score from 1-5" },
                    ["problem_solving"] = new() { Type = "number", Description = "Problem-solving ability score from 1-5" },
                    ["summary"] = new() { Type = "string", Description = "Brief summary of the candidate's performance" },
                    ["recommendation"] = new() { Type = "string", Description = "One of: strong_hire, hire, no_hire, strong_no_hire" },
                },
                Required = new[] { "overall_score", "summary", "recommendation" },
            },
        });
    }

    // =========================================================================
    // Tool execution
    // =========================================================================

    /// <summary>
    /// Executes a tool call and returns the result as a string (JSON).
    /// </summary>
    public Task<string> ExecuteToolAsync(string toolName, string argumentsJson)
    {
        return toolName switch
        {
            "get_job_description" => HandleGetJobDescriptionAsync(argumentsJson),
            "submit_evaluation" => HandleSubmitEvaluationAsync(argumentsJson),
            _ => Task.FromResult(JsonSerializer.Serialize(new { error = $"Unknown tool: {toolName}" })),
        };
    }

    private Task<string> HandleGetJobDescriptionAsync(string argumentsJson)
    {
        // TODO: Look up from database/API based on role_id
        var jobDescription = new
        {
            title = "Software Engineer",
            department = "Engineering",
            level = "Senior",
            requirements = new[]
            {
                "5+ years of software development experience",
                "Proficiency in C# or similar languages",
                "Experience with cloud services (Azure preferred)",
                "Strong communication and collaboration skills",
                "Experience leading technical projects",
            },
            key_competencies = new[]
            {
                "Technical depth and breadth",
                "System design and architecture",
                "Teamwork and mentoring",
                "Problem-solving under ambiguity",
                "Communication with stakeholders",
            },
        };

        return Task.FromResult(JsonSerializer.Serialize(jobDescription));
    }

    private Task<string> HandleSubmitEvaluationAsync(string argumentsJson)
    {
        // TODO: Persist to database / send to evaluation pipeline
        var evaluation = JsonSerializer.Deserialize<JsonElement>(argumentsJson);

        // For now, log and acknowledge
        var result = new
        {
            status = "submitted",
            message = "Evaluation recorded successfully.",
            timestamp = DateTime.UtcNow,
        };

        return Task.FromResult(JsonSerializer.Serialize(result));
    }

    // =========================================================================
    // Serialization helpers for Voice Live tool format
    // =========================================================================

    /// <summary>
    /// Returns the tools as the JSON array format expected by Voice Live session config.
    /// </summary>
    public string GetToolsJson()
    {
        var toolDefs = _tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = new
                {
                    type = "object",
                    properties = t.Parameters.Properties.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new { type = kvp.Value.Type, description = kvp.Value.Description }),
                    required = t.Parameters.Required,
                },
            },
        });

        return JsonSerializer.Serialize(toolDefs);
    }

    /// <summary>
    /// Returns SDK-native tool definitions for use with VoiceLiveSessionOptions.Tools.
    /// </summary>
    public IEnumerable<VoiceLiveFunctionDefinition> GetVoiceLiveToolDefinitions()
    {
        foreach (var tool in _tools)
        {
            var def = new VoiceLiveFunctionDefinition(tool.Name)
            {
                Description = tool.Description,
                Parameters = BinaryData.FromObjectAsJson(new
                {
                    type = "object",
                    properties = tool.Parameters.Properties.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new { type = kvp.Value.Type, description = kvp.Value.Description }),
                    required = tool.Parameters.Required,
                }),
            };
            yield return def;
        }
    }
}

// =========================================================================
// Supporting types
// =========================================================================

public class ToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required ToolParameters Parameters { get; init; }
}

public class ToolParameters
{
    public required Dictionary<string, ToolParameterProperty> Properties { get; init; }
    public required string[] Required { get; init; }
}

public class ToolParameterProperty
{
    public required string Type { get; init; }
    public required string Description { get; init; }
}
