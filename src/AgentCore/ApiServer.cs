// --serve mode: the same agent, driven over HTTP instead of the console.
// Used by the voice client (and anything else that wants to talk to the agent).
//
//   GET  /health            -> {"ok":true,"agent":"Ashley","model":"..."}
//   POST /chat              {"text":"...","voice":true} -> {"reply":"...","spoken":"...","tools":["..."]}
//   POST /reset             forget the conversation
//   GET  /personas          -> {"current":"ashley","personas":["ashley","mentor",...]}
//   POST /persona           {"name":"mentor"}   switch persona (kept for next time)
//   GET  /voice             -> {"voice":"en_US-hfc_female-medium"|null}   voice the client speaks with
//   POST /voice             {"name":"en_GB-alba-medium"}   remember the chosen voice
//
// One conversation, one request at a time (extra requests wait their turn).
// The agent has a shell, so this API must never be exposed beyond the local
// machine: the compose file publishes it on 127.0.0.1 only.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore;

public sealed record ChatRequest(string? Text, bool Voice = false);
public sealed record NameRequest(string? Name);

public static class ApiServer
{
    public static async Task RunAsync(AgentLoop agent, AgentConfig config, int port, Paths paths, AgentState state)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();

        var gate = new SemaphoreSlim(1, 1);

        app.MapGet("/health", () => Results.Json(new
        {
            ok = true,
            agent = config.Name,
            model = agent.Client.Profile.Name,
        }));

        app.MapPost("/reset", async () =>
        {
            await gate.WaitAsync();
            try { agent.Reset(); }
            finally { gate.Release(); }
            return Results.Json(new { ok = true });
        });

        app.MapGet("/personas", () => Results.Json(new
        {
            current = agent.PersonaName,
            personas = ConfigLoader.ListPersonas(paths).ToList(),
        }));

        app.MapPost("/persona", async (NameRequest request) =>
        {
            var name = request.Name?.Trim() ?? "";
            var text = name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..")
                ? ConfigLoader.LoadPersona(paths, name) : "";
            if (text.Length == 0)
                return Results.NotFound(new { error = $"no persona called '{name}'" });

            await gate.WaitAsync();
            try
            {
                agent.SetPersona(name, text);
                state.Persona = name;
                ConfigLoader.SaveState(paths, state);
            }
            finally { gate.Release(); }
            return Results.Json(new { ok = true, current = name });
        });

        app.MapGet("/voice", () => Results.Json(new { voice = state.Voice }));

        app.MapPost("/voice", (NameRequest request) =>
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrEmpty(name))
                return Results.BadRequest(new { error = "name is required" });
            state.Voice = name;
            ConfigLoader.SaveState(paths, state);
            return Results.Json(new { ok = true, voice = name });
        });

        app.MapPost("/chat", async (ChatRequest request, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest(new { error = "text is required" });

            await gate.WaitAsync(http.RequestAborted);
            try
            {
                var tools = new List<string>();
                agent.VoiceMode = request.Voice;
                agent.OnToolActivity = tools.Add;

                Console.WriteLine($"[api] {(request.Voice ? "voice" : "text")}: {Text.Clip(request.Text, 120)}");
                var reply = await agent.RunTurnAsync(request.Text, http.RequestAborted);
                return Results.Json(new { reply, spoken = SpeechText.Clean(reply), tools });
            }
            catch (OperationCanceledException)
            {
                return Results.StatusCode(499); // client went away / pressed stop
            }
            catch (LlmException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 502);
            }
            finally
            {
                agent.VoiceMode = false;
                agent.OnToolActivity = null;
                gate.Release();
            }
        });

        Ui.Info($"{config.Name} API listening on port {port} (model {agent.Client.Profile.Name}).");
        await app.RunAsync();
    }
}
