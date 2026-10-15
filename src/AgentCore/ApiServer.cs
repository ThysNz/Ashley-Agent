// --serve mode: the same agent, driven over HTTP. Serves the web page (ui\) and its API,
// and is what the Windows voice client talks to.
//
//   Chat       POST /chat {"text","voice"}        -> {"reply","spoken","tools"}
//              POST /chat/stream (same body)      -> newline-delimited JSON events as she works
//              POST /reset, GET /history, GET /status, GET /inspect
//   Chats      GET /chats, POST /chats/new, POST /chats/open {"id"}
//   Settings   GET /models, POST /model {"name"}, POST /models/add, POST /models/remove
//              GET /personas, POST /persona {"name"}, GET /voice, POST /voice {"name"}
//   Panels     GET /notes, GET /todo, GET /files?path=, GET /file?path=
//   Speech     POST /stt, POST /tts, GET /voices, GET /voices/catalog, POST /voices/add
//              (forwarded to the speech containers, so the page only talks to one address)
//
// One conversation, one request at a time (extra requests wait their turn).
// The agent has a shell, so this API must never be exposed beyond the local machine:
// the compose file publishes it on 127.0.0.1 only, and requests whose Host header is not
// localhost are refused (blocks DNS-rebinding from web pages).

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore;

public sealed record ChatRequest(string? Text, bool Voice = false);
public sealed record NameRequest(string? Name);
public sealed record IdRequest(string? Id);
public sealed record AddModelRequest(string? Name, string? Model, string? Provider, string? BaseUrl,
                                     int? ContextSize, bool? SupportsTools, bool? Think, string? Description);

public sealed record ApiContext(AgentLoop Agent, AgentConfig Config, Paths Paths, AgentState State,
                                ModelRegistry Models, HttpClient Http, MemoryStore Memory, Sandbox Sandbox, ChatStore Chats);

public static class ApiServer
{
    private static readonly JsonSerializerOptions StreamJson =
        new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static async Task RunAsync(ApiContext ctx, int port)
    {
        var agent = ctx.Agent;
        var state = ctx.State;
        var sttUrl = (Environment.GetEnvironmentVariable("STT_URL") ?? "http://host.docker.internal:9001").TrimEnd('/');
        var ttsUrl = (Environment.GetEnvironmentVariable("TTS_URL") ?? "http://host.docker.internal:9002").TrimEnd('/');

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Configuration["AllowedHosts"] = "localhost;127.0.0.1;[::1]";
        var app = builder.Build();

        var gate = new SemaphoreSlim(1, 1);

        // ---- the web page ------------------------------------------------

        var uiDir = Path.Combine(AppContext.BaseDirectory, "ui");
        if (Directory.Exists(uiDir))
        {
            var files = new PhysicalFileProvider(uiDir);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache",
            });
        }

        // ---- status ------------------------------------------------------

        app.MapGet("/health", () => Results.Json(new { ok = true, agent = ctx.Config.Name, model = agent.Client.Profile.Name }));

        string? pingModel = null, pingProblem = null;
        var pingAt = DateTime.MinValue;
        async Task<string?> ModelProblem()
        {
            var name = agent.Client.Profile.Name;
            if (name == pingModel && (DateTime.UtcNow - pingAt).TotalSeconds < 8) return pingProblem;
            pingProblem = await agent.Client.PingAsync();
            pingModel = name;
            pingAt = DateTime.UtcNow;
            return pingProblem;
        }

        app.MapGet("/status", async () =>
        {
            var p = agent.Client.Profile;
            var problem = await ModelProblem();
            return Results.Json(new
            {
                agent = ctx.Config.Name,
                model = new { name = p.Name, id = p.Model, provider = p.Provider, contextSize = p.ContextSize, launch = p.Launch, placeholder = ModelRegistry.IsPlaceholder(p) },
                persona = agent.PersonaName,
                voice = state.Voice,
                reachable = problem == null,
                problem,
                busy = gate.CurrentCount == 0,
                context = agent.GetContextInfo(),
            });
        });

        app.MapGet("/inspect", () => Results.Json(new { prompt = agent.Inspect(), context = agent.GetContextInfo() }));

        // ---- chat --------------------------------------------------------

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
                ctx.Chats.SaveCurrent(agent.History);
                gate.Release();
            }
        });

        // Same as /chat, but sends each step as one JSON line while she works:
        // thinking, usage, text, tool_start, tool_end, then final (or error / stopped).
        app.MapPost("/chat/stream", async (ChatRequest request, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(request.Text))
            {
                http.Response.StatusCode = 400;
                await http.Response.WriteAsJsonAsync(new { error = "text is required" });
                return;
            }

            await gate.WaitAsync(http.RequestAborted);
            var lines = Channel.CreateUnbounded<string>();
            void Send(object o) => lines.Writer.TryWrite(JsonSerializer.Serialize(o, StreamJson));

            http.Response.ContentType = "application/x-ndjson";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            var started = System.Diagnostics.Stopwatch.StartNew();
            Task? turn = null;
            try
            {
                agent.VoiceMode = request.Voice;
                agent.OnEvent = e => Send(e);
                Console.WriteLine($"[api] stream {(request.Voice ? "voice" : "text")}: {Text.Clip(request.Text, 120)}");

                turn = Task.Run(async () =>
                {
                    try
                    {
                        var reply = await agent.RunTurnAsync(request.Text, http.RequestAborted);
                        Send(new { type = "final", reply, spoken = SpeechText.Clean(reply), ms = started.ElapsedMilliseconds, context = agent.GetContextInfo() });
                    }
                    catch (OperationCanceledException) { Send(new { type = "stopped" }); }
                    catch (LlmException ex) { Send(new { type = "error", error = ex.Message }); }
                    catch (Exception ex) { Send(new { type = "error", error = "Unexpected error: " + ex.Message }); }
                    finally { lines.Writer.TryComplete(); }
                });

                try
                {
                    await foreach (var line in lines.Reader.ReadAllAsync())
                    {
                        await http.Response.WriteAsync(line + "\n");
                        await http.Response.Body.FlushAsync();
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { /* page closed */ }
            }
            finally
            {
                if (turn != null) { try { await turn; } catch (Exception) { } }
                agent.VoiceMode = false;
                agent.OnEvent = null;
                ctx.Chats.SaveCurrent(agent.History);
                gate.Release();
            }
        });

        app.MapGet("/history", () => Results.Json(new { turns = BuildHistory(agent.History) }));

        // ---- conversations ----------------------------------------------

        async Task<IResult> NewChat()
        {
            await gate.WaitAsync();
            try
            {
                var archived = ctx.Chats.ArchiveCurrent(agent.History);
                agent.Reset();
                ctx.Chats.SaveCurrent(agent.History);
                return Results.Json(new { ok = true, archived });
            }
            finally { gate.Release(); }
        }

        app.MapPost("/reset", NewChat);
        app.MapPost("/chats/new", NewChat);
        app.MapGet("/chats", () => Results.Json(new { chats = ctx.Chats.List() }));

        app.MapPost("/chats/open", async (IdRequest request) =>
        {
            var messages = ctx.Chats.Open(request.Id ?? "");
            if (messages == null) return Results.NotFound(new { error = "that chat doesn't exist" });
            await gate.WaitAsync();
            try
            {
                ctx.Chats.ArchiveCurrent(agent.History);
                agent.LoadHistory(messages);
                ctx.Chats.SaveCurrent(agent.History);
                return Results.Json(new { ok = true });
            }
            finally { gate.Release(); }
        });

        // ---- models ------------------------------------------------------

        app.MapGet("/models", async () =>
        {
            var profiles = ctx.Models.All;

            // Is each model server reachable? One check per server, in parallel.
            var servers = profiles.GroupBy(p => p.Provider + "|" + p.BaseUrl).ToList();
            var checks = await Task.WhenAll(servers.Select(async g =>
                (key: g.Key, problem: await new LlmClient(ctx.Http, g.First()).PingAsync())));
            var reachable = checks.ToDictionary(c => c.key, c => c.problem == null);

            // Ollama models that are installed but have no profile yet.
            var discovered = new List<object>();
            var used = profiles.Select(p => p.Model).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ollamaUrls = profiles.Where(p => p.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
                                     .Select(p => p.BaseUrl).Distinct().ToList();
            // A new agent has no Ollama profile yet: look at the standard address on the host.
            if (ollamaUrls.Count == 0) ollamaUrls.Add("http://host.docker.internal:11434");
            foreach (var baseUrl in ollamaUrls)
            {
                if (!reachable.TryGetValue("ollama|" + baseUrl, out var up))
                    up = await new LlmClient(ctx.Http, new ModelProfile { Provider = "ollama", BaseUrl = baseUrl }).PingAsync() == null;
                if (!up) continue;
                discovered.AddRange(await DiscoverOllama(ctx.Http, baseUrl, used));
            }

            return Results.Json(new
            {
                current = agent.Client.Profile.Name,
                models = profiles.Select(p => new
                {
                    name = p.Name, description = p.Description, provider = p.Provider, id = p.Model,
                    contextSize = p.ContextSize, supportsTools = p.SupportsTools, launch = p.Launch,
                    local = ctx.Models.IsLocal(p.Name),
                    placeholder = ModelRegistry.IsPlaceholder(p),
                    reachable = reachable.GetValueOrDefault(p.Provider + "|" + p.BaseUrl),
                }),
                discovered,
            });
        });

        app.MapPost("/model", async (NameRequest request) =>
        {
            var profile = ctx.Models.Find(request.Name);
            if (profile == null) return Results.NotFound(new { error = $"no model called '{request.Name}'" });
            await gate.WaitAsync();
            try
            {
                agent.SetClient(new LlmClient(ctx.Http, profile));
                state.Model = profile.Name;
                ConfigLoader.SaveState(ctx.Paths, state);
                pingModel = null;
                return Results.Json(new { ok = true, current = profile.Name, contextSize = profile.ContextSize });
            }
            finally { gate.Release(); }
        });

        app.MapPost("/models/add", async (AddModelRequest r) =>
        {
            var provider = (r.Provider ?? "ollama").ToLowerInvariant();
            var baseUrl = r.BaseUrl ?? ctx.Models.All.FirstOrDefault(p => p.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))?.BaseUrl
                          ?? "http://host.docker.internal:11434";
            var profile = new ModelProfile
            {
                Name = (r.Name ?? r.Model ?? "").Trim(),
                Model = (r.Model ?? "").Trim(),
                Provider = provider,
                BaseUrl = baseUrl,
                ContextSize = r.ContextSize is > 512 ? r.ContextSize.Value : 16384,
                SupportsTools = r.SupportsTools ?? true,
                Think = r.Think,
                Description = r.Description ?? "Added from the web page",
                Launch = provider == "ollama" ? "Ollama must be running on the host (ollama serve)" : "",
            };
            var error = ctx.Models.AddLocal(profile);
            if (error != null) return Results.BadRequest(new { error });

            // First real model for a new agent: start using it straight away.
            if (ModelRegistry.IsPlaceholder(agent.Client.Profile))
            {
                await gate.WaitAsync();
                try
                {
                    agent.SetClient(new LlmClient(ctx.Http, profile));
                    state.Model = profile.Name;
                    ConfigLoader.SaveState(ctx.Paths, state);
                    pingModel = null;
                }
                finally { gate.Release(); }
            }
            return Results.Json(new { ok = true, name = profile.Name });
        });

        app.MapPost("/models/remove", (NameRequest r) =>
        {
            var name = r.Name ?? "";
            if (name.Equals(agent.Client.Profile.Name, StringComparison.OrdinalIgnoreCase))
                return Results.Conflict(new { error = "That model is in use. Switch to another first." });
            return ctx.Models.RemoveLocal(name)
                ? Results.Json(new { ok = true })
                : Results.NotFound(new { error = "Only models added from the web page can be removed here; the others are in config\\models.json." });
        });

        // ---- personas and voice ------------------------------------------

        app.MapGet("/personas", () => Results.Json(new
        {
            current = agent.PersonaName,
            personas = ConfigLoader.ListPersonas(ctx.Paths).ToList(),
        }));

        app.MapPost("/persona", async (NameRequest request) =>
        {
            var name = request.Name?.Trim() ?? "";
            var text = name.Length > 0 && !name.Contains('/') && !name.Contains('\\') && !name.Contains("..")
                ? ConfigLoader.LoadPersona(ctx.Paths, name) : "";
            if (text.Length == 0)
                return Results.NotFound(new { error = $"no persona called '{name}'" });

            await gate.WaitAsync();
            try
            {
                agent.SetPersona(name, text);
                state.Persona = name;
                ConfigLoader.SaveState(ctx.Paths, state);
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
            ConfigLoader.SaveState(ctx.Paths, state);
            return Results.Json(new { ok = true, voice = name });
        });

        // ---- skills ------------------------------------------------------

        app.MapGet("/skills", () => Results.Json(new
        {
            skills = agent.Skills.All.Select(s => new
            {
                name = s.Name,
                description = s.Description,
                source = s.Source,
                argumentHint = s.ArgumentHint,
                userInvocable = s.UserInvocable,
                modelInvocable = s.ModelInvocable,
                files = s.Files,
            }),
        }));

        app.MapGet("/skill", (string? name) =>
        {
            var skill = agent.Skills.Find(name);
            return skill == null
                ? Results.NotFound(new { error = $"no skill called '{name}'" })
                : Results.Json(new { name = skill.Name, source = skill.Source, dir = skill.Dir, text = skill.Raw, files = skill.Files });
        });

        // ---- side panels -------------------------------------------------

        app.MapGet("/notes", () =>
        {
            var lines = ctx.Memory.ReadNotes().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
                .Select(l => l.StartsWith("- ") ? l[2..] : l).ToList();
            return Results.Json(new { notes = lines });
        });

        app.MapGet("/todo", () =>
        {
            var items = new List<object>();
            if (File.Exists(ctx.Memory.TodoPath))
            {
                foreach (var line in File.ReadAllLines(ctx.Memory.TodoPath).Where(l => l.Trim().Length > 0))
                    items.Add(new { text = line.Length > 3 ? line[3..].Trim() : line, done = line.StartsWith("[x]") });
            }
            return Results.Json(new { items });
        });

        app.MapGet("/files", (string? path) =>
        {
            try
            {
                var dir = ctx.Sandbox.Resolve(path);
                if (!Directory.Exists(dir)) return Results.NotFound(new { error = "no such folder" });
                var entries = Directory.EnumerateDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => new { name = Path.GetFileName(d), path = ctx.Sandbox.Rel(d), type = "folder", size = 0L })
                    .Concat(Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                        .Select(f => new { name = Path.GetFileName(f), path = ctx.Sandbox.Rel(f), type = "file", size = new FileInfo(f).Length }))
                    .Where(e => e.name != ".gitkeep")
                    .ToList();
                var rel = ctx.Sandbox.Rel(dir);
                var parent = rel == "." ? null : (rel.Contains('/') ? rel[..rel.LastIndexOf('/')] : ".");
                return Results.Json(new { path = rel, parent, entries });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapGet("/file", (string? path) =>
        {
            try
            {
                var full = ctx.Sandbox.Resolve(path);
                if (!File.Exists(full)) return Results.NotFound(new { error = "no such file" });
                var info = new FileInfo(full);
                using var fs = File.OpenRead(full);
                var buffer = new byte[Math.Min(200_000, info.Length)];
                var read = fs.Read(buffer, 0, buffer.Length);
                if (buffer.Take(read).Contains((byte)0))
                    return Results.Json(new { path = ctx.Sandbox.Rel(full), size = info.Length, binary = true });
                return Results.Json(new
                {
                    path = ctx.Sandbox.Rel(full), size = info.Length, binary = false,
                    text = Encoding.UTF8.GetString(buffer, 0, read), truncated = info.Length > read,
                });
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        // ---- speech (forwarded to the speech containers) -------------------

        app.MapPost("/stt", (HttpContext http) => Forward(ctx.Http, http, HttpMethod.Post, sttUrl + "/stt", TimeSpan.FromMinutes(2), "speech-to-text"));
        app.MapPost("/tts", (HttpContext http) => Forward(ctx.Http, http, HttpMethod.Post, ttsUrl + "/tts", TimeSpan.FromMinutes(2), "text-to-speech"));
        app.MapGet("/voices", (HttpContext http) => Forward(ctx.Http, http, HttpMethod.Get, ttsUrl + "/voices", TimeSpan.FromSeconds(15), "text-to-speech"));
        app.MapGet("/voices/catalog", (HttpContext http) =>
            Forward(ctx.Http, http, HttpMethod.Get, ttsUrl + "/voices/catalog" + http.Request.QueryString, TimeSpan.FromSeconds(90), "text-to-speech"));
        app.MapPost("/voices/add", (HttpContext http) => Forward(ctx.Http, http, HttpMethod.Post, ttsUrl + "/voices/add", TimeSpan.FromMinutes(10), "text-to-speech"));

        Ui.Info($"{ctx.Config.Name} API and web page on port {port} (model {agent.Client.Profile.Name}).");
        await app.RunAsync();
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task Forward(HttpClient client, HttpContext http, HttpMethod method, string url, TimeSpan timeout, string what)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        cts.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (method == HttpMethod.Post)
            {
                using var body = new MemoryStream();
                await http.Request.Body.CopyToAsync(body, cts.Token);
                request.Content = new ByteArrayContent(body.ToArray());
                if (http.Request.ContentType != null)
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", http.Request.ContentType);
            }
            using var response = await client.SendAsync(request, cts.Token);
            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            http.Response.StatusCode = (int)response.StatusCode;
            http.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            await http.Response.Body.WriteAsync(bytes, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !http.RequestAborted.IsCancellationRequested)
        {
            http.Response.StatusCode = 502;
            await http.Response.WriteAsJsonAsync(new
            {
                error = $"The {what} service isn't reachable. Start it with: cd F:\\AI-Agents\\voice ; docker compose up -d",
            });
        }
    }

    private static async Task<List<object>> DiscoverOllama(HttpClient client, string baseUrl, HashSet<string> used)
    {
        var found = new List<object>();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var tags = JsonDocument.Parse(await client.GetStringAsync(baseUrl.TrimEnd('/') + "/api/tags", cts.Token));
            var models = tags.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => (name: m.GetProperty("name").GetString() ?? "",
                              size: m.TryGetProperty("size", out var s) ? s.GetInt64() : 0L,
                              parameters: m.TryGetProperty("details", out var d) && d.TryGetProperty("parameter_size", out var ps) ? ps.GetString() : null))
                .Where(m => m.name.Length > 0 && !used.Contains(m.name)).ToList();

            var details = await Task.WhenAll(models.Select(async m =>
            {
                try
                {
                    using var response = await client.PostAsync(baseUrl.TrimEnd('/') + "/api/show",
                        new StringContent(JsonSerializer.Serialize(new { model = m.name }), Encoding.UTF8, "application/json"), cts.Token);
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cts.Token));
                    var caps = doc.RootElement.TryGetProperty("capabilities", out var c)
                        ? c.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : null;
                    return (m, caps);
                }
                catch (Exception) { return (m, caps: (List<string>?)null); }
            }));

            foreach (var (m, caps) in details)
            {
                if (caps != null && !caps.Contains("completion")) continue; // embedding models can't chat
                found.Add(new
                {
                    id = m.name, baseUrl,
                    sizeGb = Math.Round(m.size / 1e9, 1),
                    parameters = m.parameters,
                    tools = caps?.Contains("tools"),
                    thinking = caps?.Contains("thinking"),
                });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { }
        return found;
    }

    private sealed class HistTool { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public string What { get; set; } = ""; public string Args { get; set; } = ""; public string? Result { get; set; } public bool Failed { get; set; } }
    private sealed class HistTurn { public string Role { get; set; } = ""; public string Text { get; set; } = ""; public List<HistTool> Tools { get; set; } = new(); public List<string> Notes { get; set; } = new(); }

    // Turns the model-facing message list into what the page shows: user messages,
    // and for each answer the tool calls she made (with results) and her final text.
    private static List<HistTurn> BuildHistory(IReadOnlyList<Msg> messages)
    {
        var turns = new List<HistTurn>();
        HistTurn? open = null;
        var byId = new Dictionary<string, HistTool>();

        foreach (var m in messages.ToList())
        {
            // A message run with /skill-name is stored with the skill's full text; the page shows what was typed.
            if (m.Role == "user") { open = null; turns.Add(new HistTurn { Role = "user", Text = SkillStore.DisplayText(m.Content) }); continue; }

            if (m.Role == "assistant")
            {
                if (open == null) { open = new HistTurn { Role = "assistant" }; turns.Add(open); }
                if (m.ToolCalls is { Count: > 0 })
                {
                    if (m.Content.Length > 0) open.Notes.Add(m.Content);
                    foreach (var c in m.ToolCalls)
                    {
                        var tool = new HistTool { Id = c.Id, Name = c.Name, Args = Text.Clip(c.ArgsJson, 4000), What = DescribeCall(c) };
                        byId[c.Id] = tool;
                        open.Tools.Add(tool);
                    }
                }
                else { open.Text = m.Content; open = null; }
                continue;
            }

            if (m.Role == "tool" && m.ToolCallId != null && byId.TryGetValue(m.ToolCallId, out var t))
            {
                t.Result = Text.Clip(m.Content, 4000);
                t.Failed = m.Content.StartsWith("Error", StringComparison.Ordinal);
            }
        }
        return turns;
    }

    private static string DescribeCall(ToolCall c)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(c.ArgsJson) ? "{}" : c.ArgsJson);
            return AgentLoop.Describe(c.Name, doc.RootElement);
        }
        catch (JsonException) { return ""; }
    }
}
