// AgentCore - a text agent that runs inside a Docker container.
// One image serves every agent in F:\AI-Agents; each agent's folder supplies
// its own config, workspace and memory (mounted in by its docker-compose.yml).
//
//   dotnet AgentCore.dll                    interactive chat
//   dotnet AgentCore.dll --prompt "text"    one request, then exit

using AgentCore;

var paths = new Paths();
AgentConfig config;
List<ModelProfile> models;
try
{
    config = ConfigLoader.LoadAgent(paths);
    models = ConfigLoader.LoadModels(paths);
}
catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidOperationException or KeyNotFoundException)
{
    Ui.Error($"Problem with the config files in {paths.Config}: {ex.Message}");
    return 2;
}
var state = ConfigLoader.LoadState(paths);

// config\models.json plus any models added from the web page.
var registry = new ModelRegistry(models, paths);
ModelProfile? FindModel(string? name) => registry.Find(name);

var profile = FindModel(state.Model) ?? FindModel(config.DefaultModel) ?? registry.All[0];
var personaName = state.Persona ?? config.Persona;
var personaText = ConfigLoader.LoadPersona(paths, personaName);
if (personaText.Length == 0)
{
    personaName = config.Persona;
    personaText = ConfigLoader.LoadPersona(paths, personaName);
}

// Generous timeout: a 30B model on a partly-CPU setup can think for minutes.
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };

var sandbox = new Sandbox(paths.Workspace, paths.Memory);
var memory = new MemoryStore(paths.Memory, config.MaxNotesChars);

ShellTool.GitName = config.GitName;
ShellTool.GitEmail = config.GitEmail;

// Skills: folders with a SKILL.md, from this agent's skills folder and the shared one.
var skills = new SkillStore(paths.Skills, paths.SharedSkills);

var tools = new List<Tool>();
tools.AddRange(FileTools.Create(sandbox));
tools.Add(ShellTool.Create(sandbox, config.CommandTimeoutSeconds));
tools.AddRange(WebTools.Create(http));
tools.AddRange(MemoryTools.Create(memory));
tools.Add(SkillTool.Create(skills));
tools.RemoveAll(t => config.DisabledTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase));

var agent = new AgentLoop(config, paths, sandbox, memory, skills, tools, new LlmClient(http, profile), personaName, personaText);

// Ctrl+C stops the current turn; pressed while idle it exits.
CancellationTokenSource? turnCts = null;
Console.CancelKeyPress += (_, e) =>
{
    if (turnCts is { IsCancellationRequested: false })
    {
        e.Cancel = true;
        turnCts.Cancel();
    }
};

// ---- HTTP API mode (used by the voice client) ----------------------------

if (args.Contains("--serve"))
{
    var port = int.TryParse(Environment.GetEnvironmentVariable("AGENT_PORT"), out var envPort) ? envPort : 5000;
    var down = await agent.Client.PingAsync();
    if (down != null) Ui.Warn(down);
    var chats = new ChatStore(paths.Memory);
    agent.LoadHistory(chats.LoadCurrent()); // pick up where the last session left off
    await ApiServer.RunAsync(new ApiContext(agent, config, paths, state, registry, http, memory, sandbox, chats), port);
    return 0;
}

// ---- one-shot mode ---------------------------------------------------

var promptIndex = Array.IndexOf(args, "--prompt");
if (promptIndex >= 0 && promptIndex + 1 < args.Length)
{
    return await RunOnce(args[promptIndex + 1]) ? 0 : 1;
}

// ---- interactive mode ------------------------------------------------

Ui.Info($"{config.Name} - agent in a container. Type /help for commands, Ctrl+C stops a running task.");
Ui.Info($"Model: {agent.Client.Profile.Name} ({agent.Client.Profile.Model}, {agent.Client.Profile.ContextSize} ctx) | persona: {agent.PersonaName} | workspace: {sandbox.Root}");
var problem = await agent.Client.PingAsync();
if (problem != null) Ui.Warn(problem + "\n(You can still use /model to pick another.)");

while (true)
{
    var context = agent.LastPromptTokens is int used ? $" [{used}/{agent.Client.Profile.ContextSize}]" : "";
    Console.Write(Ui.Prompt($"{Environment.NewLine}you{context}> "));
    var line = Console.ReadLine();
    if (line == null) break; // stdin closed
    line = line.Trim();
    if (line.Length == 0) continue;

    if (line.StartsWith('/'))
    {
        if (!await HandleCommand(line)) break;
        continue;
    }

    await RunOnce(line);
}

Ui.Info("Goodbye.");
return 0;

// ---- helpers ---------------------------------------------------------

async Task<bool> RunOnce(string text)
{
    turnCts = new CancellationTokenSource();
    try
    {
        var reply = await agent.RunTurnAsync(text, turnCts.Token);
        Console.WriteLine();
        Console.WriteLine($"{config.Name}: {reply}");
        return true;
    }
    catch (OperationCanceledException)
    {
        Ui.Warn("\n(stopped)");
        return false;
    }
    catch (LlmException ex)
    {
        Ui.Error("\n" + ex.Message);
        return false;
    }
    finally
    {
        turnCts.Dispose();
        turnCts = null;
    }
}

// Returns false to quit.
async Task<bool> HandleCommand(string line)
{
    var space = line.IndexOf(' ');
    var cmd = (space < 0 ? line : line[..space]).ToLowerInvariant();
    var arg = space < 0 ? "" : line[(space + 1)..].Trim();

    switch (cmd)
    {
        case "/quit":
        case "/exit":
            return false;

        case "/help":
            Ui.Info("""
                /models          list models      /model <name>    switch model
                /personas        list personas    /persona <name>  switch persona
                /tools           list tools       /notes           show long-term notes
                /todo            show task list   /reset           forget this conversation
                /status          show settings    /quit            exit
                /skills          list skills      /<skill> text    run a skill on your request
                Anything else is sent to the agent. Ctrl+C stops a running task.
                """);
            break;

        case "/skills":
            if (agent.Skills.All.Count == 0) { Ui.Say("(no skills installed - see skills\\README.md)"); break; }
            foreach (var s in agent.Skills.All)
                Ui.Say($"  /{s.Name,-22} {(s.Source == "shared" ? "shared" : "agent ")}  {Text.Clip(s.Description, 90)}");
            break;

        case "/models":
            foreach (var m in registry.All)
            {
                var mark = m.Name == agent.Client.Profile.Name ? "*" : " ";
                Ui.Say($" {mark} {m.Name,-18} {m.Provider,-7} {m.ContextSize,6} ctx  {m.Description}");
            }
            break;

        case "/model":
            var chosen = FindModel(arg);
            if (chosen == null) { Ui.Warn($"No model called '{arg}'. Try /models."); break; }
            agent.SetClient(new LlmClient(http, chosen));
            state.Model = chosen.Name;
            ConfigLoader.SaveState(paths, state);
            Ui.Info($"Now using {chosen.Name} ({chosen.Model}). The conversation so far is kept.");
            var issue = await agent.Client.PingAsync();
            if (issue != null) Ui.Warn(issue);
            break;

        case "/personas":
            foreach (var p in ConfigLoader.ListPersonas(paths))
                Ui.Say($" {(p == agent.PersonaName ? "*" : " ")} {p}");
            break;

        case "/persona":
            var text = ConfigLoader.LoadPersona(paths, arg);
            if (text.Length == 0) { Ui.Warn($"No persona called '{arg}'. Try /personas."); break; }
            agent.SetPersona(arg, text);
            state.Persona = arg;
            ConfigLoader.SaveState(paths, state);
            Ui.Info($"Persona is now {arg}.");
            break;

        case "/tools":
            foreach (var t in agent.Tools) Ui.Say($"  {t.Name,-14} {t.Description.Split(". ")[0].TrimEnd('.')}.");
            break;

        case "/notes":
            var notes = memory.ReadNotes();
            Ui.Say(notes.Length == 0 ? "(no notes yet)" : notes);
            break;

        case "/todo":
            Ui.Say(memory.Todo("list", null));
            break;

        case "/reset":
            agent.Reset();
            Ui.Info("Conversation cleared. Notes and workspace are untouched.");
            break;

        case "/status":
            var p2 = agent.Client.Profile;
            Ui.Say($"model {p2.Name} -> {p2.BaseUrl} ({p2.Model}), context {p2.ContextSize}, last prompt {agent.LastPromptTokens?.ToString() ?? "n/a"} tokens");
            Ui.Say($"workspace {sandbox.Root}\nmemory    {paths.Memory}\nconfig    {paths.Config}");
            break;

        default:
            // "/skill-name your request" runs that skill.
            if (agent.Skills.Find(cmd) is { UserInvocable: true })
            {
                await RunOnce(line);
                break;
            }
            Ui.Warn($"Unknown command {cmd}. Try /help or /skills.");
            break;
    }
    return true;
}
