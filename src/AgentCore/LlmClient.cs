// Talks to the model server (which runs on the host, outside the container).
// Two wire formats, same behaviour for the rest of the program:
//   openai - llama-server (the 30B) and anything else on /v1/chat/completions
//   ollama - Ollama's native /api/chat
//
// The agent loop works with the neutral Msg/ToolCall types below; each
// provider converts them to its own JSON shape here and nowhere else.

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentCore;

public sealed record ToolCall(string Id, string Name, string ArgsJson);

public sealed class Msg
{
    public string Role { get; init; }               // user | assistant | tool
    public string Content { get; set; }
    public List<ToolCall>? ToolCalls { get; init; }  // assistant asking for tools
    public string? ToolCallId { get; init; }         // tool result: which call
    public string? ToolName { get; init; }

    public Msg(string role, string content, List<ToolCall>? toolCalls = null,
               string? toolCallId = null, string? toolName = null)
    {
        Role = role; Content = content; ToolCalls = toolCalls; ToolCallId = toolCallId; ToolName = toolName;
    }
}

public sealed record ChatResult(string Content, List<ToolCall> Calls, int? PromptTokens);

public sealed class LlmException(string message) : Exception(message);

public sealed class LlmClient
{
    private static readonly Regex ThinkBlock = new(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private readonly HttpClient _http;
    public ModelProfile Profile { get; }

    public LlmClient(HttpClient http, ModelProfile profile)
    {
        _http = http;
        Profile = profile;
    }

    private bool IsOllama => Profile.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);

    private string ChatUrl => Profile.BaseUrl.TrimEnd('/') + (IsOllama ? "/api/chat" : "/chat/completions");
    private string HealthUrl => Profile.BaseUrl.TrimEnd('/') + (IsOllama ? "/api/tags" : "/models");

    // null = reachable, otherwise a human-readable problem.
    public async Task<string?> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var response = await _http.GetAsync(HealthUrl, cts.Token);
            return response.IsSuccessStatusCode ? null : $"{HealthUrl} answered {(int)response.StatusCode}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return Unreachable();
        }
    }

    private string Unreachable() =>
        $"Can't reach the model server at {Profile.BaseUrl}. It runs on the host, not in the container." +
        (Profile.Launch.Length > 0 ? $" Start it with: {Profile.Launch}" : "");

    public async Task<ChatResult> ChatAsync(IReadOnlyList<Msg> history, string system,
                                            IReadOnlyList<Tool> tools, CancellationToken ct)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in history) messages.Add(IsOllama ? ToOllama(m) : ToOpenAi(m));

        var request = new JsonObject { ["model"] = Profile.Model, ["messages"] = messages, ["stream"] = false };

        if (IsOllama)
        {
            request["options"] = new JsonObject
            {
                ["num_ctx"] = Profile.ContextSize,
                ["temperature"] = Profile.Temperature,
                ["num_predict"] = Profile.MaxTokens,
            };
            if (Profile.Think is bool think) request["think"] = think;
        }
        else
        {
            request["temperature"] = Profile.Temperature;
            request["max_tokens"] = Profile.MaxTokens;
        }

        if (Profile.SupportsTools && tools.Count > 0)
        {
            var defs = new JsonArray();
            foreach (var t in tools)
            {
                defs.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["parameters"] = t.Schema.DeepClone(),
                    },
                });
            }
            request["tools"] = defs;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, ChatUrl) { Content = JsonContent.Create(request) };
        if (!string.IsNullOrEmpty(Profile.ApiKey))
            message.Headers.Authorization = new("Bearer", Profile.ApiKey);

        string body;
        try
        {
            using var response = await _http.SendAsync(message, ct);
            body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new LlmException($"Model server returned {(int)response.StatusCode}: {Text.Clip(body, 600)}");
        }
        catch (HttpRequestException)
        {
            throw new LlmException(Unreachable());
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException("The model took too long to answer (timeout).");
        }

        try
        {
            return IsOllama ? ParseOllama(body) : ParseOpenAi(body);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException)
        {
            throw new LlmException($"Couldn't understand the model's reply: {Text.Clip(body, 400)}");
        }
    }

    // ---- request side -------------------------------------------------

    private static JsonObject ToOpenAi(Msg m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
        {
            var calls = new JsonArray();
            foreach (var c in m.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["id"] = c.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.ArgsJson },
                });
            }
            o["tool_calls"] = calls;
        }
        if (m.Role == "tool") o["tool_call_id"] = m.ToolCallId;
        return o;
    }

    private static JsonObject ToOllama(Msg m)
    {
        var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
        if (m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
        {
            var calls = new JsonArray();
            foreach (var c in m.ToolCalls)
            {
                calls.Add(new JsonObject
                {
                    ["function"] = new JsonObject
                    {
                        ["name"] = c.Name,
                        ["arguments"] = SafeArgs(c.ArgsJson),
                    },
                });
            }
            o["tool_calls"] = calls;
        }
        if (m.Role == "tool") o["tool_name"] = m.ToolName;
        return o;
    }

    private static JsonNode SafeArgs(string json)
    {
        try { return JsonNode.Parse(json) ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    // ---- response side ------------------------------------------------

    private static ChatResult ParseOpenAi(string body)
    {
        var root = JsonNode.Parse(body)!;
        var message = root["choices"]![0]!["message"]!;
        var calls = new List<ToolCall>();
        if (message["tool_calls"] is JsonArray arr)
        {
            foreach (var c in arr)
            {
                var fn = c!["function"]!;
                var args = fn["arguments"];
                // Normally a JSON string; tolerate an already-parsed object.
                var argsJson = args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args?.ToJsonString() ?? "{}";
                var id = c["id"]?.GetValue<string>();
                calls.Add(new ToolCall(string.IsNullOrEmpty(id) ? NewId() : id, fn["name"]!.GetValue<string>(), argsJson));
            }
        }
        return new ChatResult(CleanContent(message["content"]?.GetValue<string>()), calls,
                              root["usage"]?["prompt_tokens"]?.GetValue<int>());
    }

    private static ChatResult ParseOllama(string body)
    {
        var root = JsonNode.Parse(body)!;
        var message = root["message"]!;
        var calls = new List<ToolCall>();
        if (message["tool_calls"] is JsonArray arr)
        {
            foreach (var c in arr)
            {
                var fn = c!["function"]!;
                var args = fn["arguments"];
                var argsJson = args is JsonValue v && v.TryGetValue<string>(out var s) ? s : args?.ToJsonString() ?? "{}";
                calls.Add(new ToolCall(NewId(), fn["name"]!.GetValue<string>(), argsJson));
            }
        }
        return new ChatResult(CleanContent(message["content"]?.GetValue<string>()), calls,
                              root["prompt_eval_count"]?.GetValue<int>());
    }

    private static string NewId() => "call_" + Guid.NewGuid().ToString("N")[..12];

    // Drops hidden reasoning some models inline as <think>...</think>.
    private static string CleanContent(string? content)
    {
        if (string.IsNullOrEmpty(content)) return "";
        content = ThinkBlock.Replace(content, "");
        var close = content.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (close >= 0) content = content[(close + 8)..];
        return content.Trim();
    }
}
