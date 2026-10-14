// Configuration: where things live, which agent this is, which models exist.
//
// Everything an agent needs comes from three mounted folders (see the agent's
// docker-compose.yml): config (read-only), workspace (her sandbox), memory.
// Nothing here is Windows-specific, so the same image runs on any PC.

using System.Text.Json;

namespace AgentCore;

public sealed class AgentConfig
{
    public string Name { get; set; } = "Ashley";
    public string Persona { get; set; } = "ashley";
    public string DefaultModel { get; set; } = "";
    public int MaxToolRounds { get; set; } = 30;
    public int HistoryTurns { get; set; } = 12;
    public int CommandTimeoutSeconds { get; set; } = 120;
    public int MaxNotesChars { get; set; } = 8000;

    // Author of the git commits she makes in the workspace.
    public string GitName { get; set; } = "Ashley";
    public string GitEmail { get; set; } = "ashley@ai-agents.local";

    // Tool names to switch off for this agent, e.g. ["run_command", "web_fetch"].
    public List<string> DisabledTools { get; set; } = new();
}

public sealed class ModelProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    // "openai" = llama-server / LM Studio / anything speaking /v1/chat/completions
    // "ollama" = Ollama's native /api/chat (lets us set num_ctx per request)
    public string Provider { get; set; } = "openai";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }

    public int ContextSize { get; set; } = 16384;
    public double Temperature { get; set; } = 0.3;
    public int MaxTokens { get; set; } = 4096;
    public bool SupportsTools { get; set; } = true;

    // Ollama only: false turns off Qwen3-style hidden reasoning (faster).
    public bool? Think { get; set; }

    // Shown when the model server can't be reached (it runs on the host,
    // outside the container, so the agent can't start it itself).
    public string Launch { get; set; } = "";
}

public sealed class Paths
{
    public string Config { get; }
    public string Workspace { get; }
    public string Memory { get; }

    public Paths()
    {
        Config = Full(Environment.GetEnvironmentVariable("AGENT_CONFIG") ?? "config");
        Workspace = Full(Environment.GetEnvironmentVariable("AGENT_WORKSPACE") ?? "workspace");
        Memory = Full(Environment.GetEnvironmentVariable("AGENT_MEMORY") ?? "memory");
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Memory);
    }

    private static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
}

public sealed class AgentState
{
    public string? Model { get; set; }
    public string? Persona { get; set; }

    // Piper voice the voice client should speak with (the agent only stores it).
    public string? Voice { get; set; }
}

public static class ConfigLoader
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static AgentConfig LoadAgent(Paths paths)
    {
        var file = Path.Combine(paths.Config, "agent.json");
        return File.Exists(file)
            ? JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(file), Options) ?? new AgentConfig()
            : new AgentConfig();
    }

    public static List<ModelProfile> LoadModels(Paths paths)
    {
        var file = Path.Combine(paths.Config, "models.json");
        if (!File.Exists(file)) throw new FileNotFoundException($"models.json not found in {paths.Config}");

        using var doc = JsonDocument.Parse(File.ReadAllText(file),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var models = doc.RootElement.GetProperty("models").Deserialize<List<ModelProfile>>(Options) ?? new();
        if (models.Count == 0) throw new InvalidOperationException("models.json lists no models.");
        return models;
    }

    public static string LoadPersona(Paths paths, string name)
    {
        var file = Path.Combine(paths.Config, "personas", name + ".md");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
    }

    public static IEnumerable<string> ListPersonas(Paths paths)
    {
        var dir = Path.Combine(paths.Config, "personas");
        return Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.md").Select(f => Path.GetFileNameWithoutExtension(f)!).OrderBy(n => n)
            : Enumerable.Empty<string>();
    }

    // The last model/persona picked with /model and /persona, kept in the
    // memory folder so it survives container restarts.
    public static AgentState LoadState(Paths paths)
    {
        try
        {
            var file = Path.Combine(paths.Memory, "state.json");
            if (File.Exists(file))
                return JsonSerializer.Deserialize<AgentState>(File.ReadAllText(file), Options) ?? new AgentState();
        }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        return new AgentState();
    }

    public static void SaveState(Paths paths, AgentState state)
    {
        try { File.WriteAllText(Path.Combine(paths.Memory, "state.json"), JsonSerializer.Serialize(state, Options)); }
        catch (IOException) { }
    }
}
