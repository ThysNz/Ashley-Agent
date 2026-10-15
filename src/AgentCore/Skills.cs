// Skills, in the same format Claude uses: a folder containing SKILL.md (a YAML header with a
// name and description, then instructions in markdown) plus optional supporting files
// (scripts, references). The agent sees only the names and descriptions; when a request
// matches, she loads the full instructions with the `skill` tool. The user can also run a
// skill by typing /skill-name in the chat.
//
//   <agent>\skills\<name>\SKILL.md     this agent only        (mounted at /agent/skills)
//   F:\AI-Agents\skills\<name>\SKILL.md  every agent            (mounted at /agent/shared-skills)
//
// An agent's own skill wins over a shared one with the same name. Both folders are mounted
// read-only, so she cannot change or add skills herself.
//
// Understood in the header: name, description, disable-model-invocation (user-only skill),
// user-invocable (false = model-only), argument-hint. Other Claude fields (allowed-tools,
// model, context...) are ignored. In the body, $ARGUMENTS, $ARGUMENTS[N] and
// ${CLAUDE_SKILL_DIR} / ${SKILL_DIR} are replaced.

using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore;

public sealed class Skill
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "";        // "agent" or "shared"
    public string Dir { get; init; } = "";           // folder inside the container
    public string Body { get; init; } = "";
    public string ArgumentHint { get; init; } = "";
    public bool ModelInvocable { get; init; } = true;
    public bool UserInvocable { get; init; } = true;
    public List<string> Files { get; init; } = new();   // supporting files, relative to Dir
    public string Raw { get; init; } = "";               // the whole SKILL.md as written
}

public sealed class SkillStore
{
    private const int MaxBodyChars = 60_000;
    private const int MaxListChars = 6_000;
    private static readonly Regex ValidName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$");

    private readonly (string Path, string Source)[] _roots;
    private readonly object _lock = new();
    private List<Skill> _skills = new();
    private DateTime _scanned = DateTime.MinValue;

    public SkillStore(string agentDir, string sharedDir)
    {
        _roots = new[] { (agentDir, "agent"), (sharedDir, "shared") };
    }

    public IReadOnlyList<Skill> All
    {
        get
        {
            Refresh();
            return _skills;
        }
    }

    public Skill? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return All.FirstOrDefault(s => s.Name.Equals(name.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase));
    }

    // Skills are picked up as soon as a folder is dropped in; scanning at most every 2 seconds is cheap.
    private void Refresh()
    {
        lock (_lock)
        {
            if ((DateTime.UtcNow - _scanned).TotalSeconds < 2) return;
            _scanned = DateTime.UtcNow;

            var found = new List<Skill>();
            foreach (var (root, source) in _roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in SafeDirs(root))
                {
                    var skill = Load(dir, source);
                    if (skill != null && !found.Any(s => s.Name.Equals(skill.Name, StringComparison.OrdinalIgnoreCase)))
                        found.Add(skill);
                }
            }
            _skills = found.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static Skill? Load(string dir, string source)
    {
        try
        {
            var file = Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase));
            if (file == null) return null;

            var raw = File.ReadAllText(file);
            var (meta, body) = ParseFrontmatter(raw);
            var name = meta.GetValueOrDefault("name") is { Length: > 0 } n ? n : Path.GetFileName(dir);
            if (!ValidName.IsMatch(name)) return null;

            // Claude Code falls back to the first paragraph when there is no description.
            var description = meta.GetValueOrDefault("description") ?? "";
            if (description.Length == 0)
                description = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => !p.TrimStart().StartsWith('#'))?.Trim().Replace('\n', ' ') ?? "";
            if (description.Length > 500) description = description[..500] + "...";

            var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                .Where(f => !f.Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(60).ToList();

            return new Skill
            {
                Name = name,
                Description = description,
                Source = source,
                Dir = dir.Replace('\\', '/'),
                Body = body.Length > MaxBodyChars ? body[..MaxBodyChars] + "\n[...skill text cut off]" : body,
                Raw = raw,
                ArgumentHint = meta.GetValueOrDefault("argument-hint") ?? "",
                ModelInvocable = !IsTrue(meta.GetValueOrDefault("disable-model-invocation")),
                UserInvocable = meta.GetValueOrDefault("user-invocable") is not { } u || !u.Equals("false", StringComparison.OrdinalIgnoreCase),
                Files = files,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool IsTrue(string? v) => v != null && v.Equals("true", StringComparison.OrdinalIgnoreCase);

    // A small reader for the YAML subset skills use: "key: value", quoted values, and folded (>) or
    // literal (|) multi-line values. Lists and nested maps are skipped.
    public static (Dictionary<string, string> Meta, string Body) ParseFrontmatter(string text)
    {
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        text = text.TrimStart('\uFEFF').Replace("\r\n", "\n");
        if (!text.StartsWith("---\n")) return (meta, text.Trim());

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) return (meta, text.Trim());
        var header = text[4..end].Split('\n');
        var after = text.IndexOf('\n', end + 1);
        var body = after < 0 ? "" : text[(after + 1)..];

        for (var i = 0; i < header.Length; i++)
        {
            var m = Regex.Match(header[i], @"^([A-Za-z0-9_-]+):\s*(.*)$");
            if (!m.Success) continue;
            var key = m.Groups[1].Value;
            var value = m.Groups[2].Value.Trim();

            if (value is ">" or "|" or ">-" or "|-" or ">+" or "|+")
            {
                var folded = value.StartsWith('>');
                var lines = new List<string>();
                while (i + 1 < header.Length && (header[i + 1].Length == 0 || char.IsWhiteSpace(header[i + 1][0])))
                    lines.Add(header[++i].Trim());
                value = string.Join(folded ? " " : "\n", lines).Trim();
            }
            else if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }
            meta[key] = value;
        }
        return (meta, body.Trim());
    }

    // ---- what the model is told ------------------------------------------

    public string PromptSection()
    {
        var usable = All.Where(s => s.ModelInvocable).ToList();
        if (usable.Count == 0) return "";

        var list = new StringBuilder();
        foreach (var s in usable)
        {
            var line = $"- {s.Name}: {s.Description}\n";
            if (list.Length + line.Length > MaxListChars) { list.Append("- (more skills exist but the list is full)\n"); break; }
            list.Append(line);
        }

        return "## Skills\n" +
               "Skills are saved procedures for particular kinds of task. When a request matches a skill's description, call the `skill` tool with its name BEFORE you start, " +
               "then follow the instructions it returns. A skill may come with files (scripts, references): read them with the tool's `file` argument and run scripts with run_command " +
               "using the directory the tool gives you. Skills written for Claude may name tools such as Bash, Read, Write, Edit, Grep, Glob, WebFetch or WebSearch: use " +
               "run_command, read_file, write_file, edit_file, search_files, find_files, web_fetch and web_search instead. Don't mention a skill unless you are using it. " +
               "The skill tool only loads skills that already exist; writing a NEW skill is itself a task (use the write-a-skill skill if it is listed). " +
               "If no skill fits, just do the task normally.\n\n" +
               "Available skills:\n" + list.ToString().TrimEnd();
    }

    // The skill text with arguments filled in, as returned by the tool or used for /name.
    public static string Render(Skill s, string args)
    {
        var body = s.Body.Replace("${CLAUDE_SKILL_DIR}", s.Dir).Replace("${SKILL_DIR}", s.Dir);
        var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        body = Regex.Replace(body, @"\$ARGUMENTS\[(\d+)\]", m =>
            int.TryParse(m.Groups[1].Value, out var i) && i < parts.Length ? parts[i] : "");
        if (body.Contains("$ARGUMENTS", StringComparison.Ordinal)) body = body.Replace("$ARGUMENTS", args);
        else if (args.Length > 0) body += $"\n\nARGUMENTS: {args}";

        var files = s.Files.Count > 0 ? "\nFiles in this skill (read one with the skill tool's `file` argument): " + string.Join(", ", s.Files.Take(30)) : "";
        return $"Skill directory: {s.Dir}{files}\n\n{body}";
    }

    // "/name args" typed by the user -> the full instruction for the model (or null if it isn't a skill).
    public const string InvokedPrefix = "[Skill invoked by the user: /";

    public string? TryExpand(string text)
    {
        var m = Regex.Match(text.Trim(), @"^/([A-Za-z0-9][A-Za-z0-9._-]*)(?:\s+([\s\S]*))?$");
        if (!m.Success) return null;
        var skill = Find(m.Groups[1].Value);
        if (skill == null || !skill.UserInvocable) return null;

        var args = m.Groups[2].Value.Trim();
        return $"{InvokedPrefix}{skill.Name}]\n" + (args.Length > 0 ? $"Arguments: {args}\n" : "") +
               "Follow this skill for the request. Its full instructions are below, so do not load it again with the skill tool; start with the first step now.\n\n" + Render(skill, args);
    }

    // What to show in the chat for a message that was expanded from /name.
    public static string DisplayText(string content)
    {
        if (!content.StartsWith(InvokedPrefix, StringComparison.Ordinal)) return content;
        var lines = content.Split('\n');
        var name = lines[0][InvokedPrefix.Length..].TrimEnd(']');
        var args = lines.Length > 1 && lines[1].StartsWith("Arguments: ", StringComparison.Ordinal) ? " " + lines[1]["Arguments: ".Length..] : "";
        return "/" + name + args;
    }
}

public static class SkillTool
{
    public static Tool Create(SkillStore store)
    {
        return new Tool("skill",
            "Load a skill's instructions (a saved procedure for a kind of task) and then follow them. Use it when the request matches a skill in the Skills list. " +
            "With `file`, reads a supporting file from that skill's folder instead (for example a reference document).",
            Schema.Obj(("name", "string", "The skill's name, as listed under Available skills.", true),
                       ("args", "string", "What to do with it: the request or arguments for the skill. Optional.", false),
                       ("file", "string", "A file inside the skill's folder to read instead of the instructions. Optional.", false)),
            (a, ct) => Task.FromResult(Run(store, Args.Req(a, "name"), Args.Str(a, "args") ?? "", Args.Str(a, "file"))));
    }

    private static string Run(SkillStore store, string name, string args, string? file)
    {
        var skill = store.Find(name);
        if (skill == null)
        {
            var names = string.Join(", ", store.All.Where(s => s.ModelInvocable).Select(s => s.Name));
            var create = store.Find("write-a-skill") != null
                ? " This tool only loads skills that already exist. If you were asked to WRITE or CREATE a new skill, call this tool with name 'write-a-skill' instead."
                : " This tool only loads skills that already exist; it cannot create one.";
            return $"Error: there is no skill called '{name}'." + (names.Length > 0 ? $" Available: {names}." : " No skills are installed.") + create;
        }
        if (!skill.ModelInvocable)
            return $"Error: the skill '{skill.Name}' can only be run by Thys typing /{skill.Name}. Don't use it yourself.";

        if (!string.IsNullOrWhiteSpace(file))
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(skill.Dir));
            var full = Path.GetFullPath(Path.Combine(root, file.Trim().Replace('\\', '/')));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return $"Error: '{file}' is outside the skill's folder.";
            if (!File.Exists(full)) return $"Error: the skill has no file '{file}'. Files: {string.Join(", ", skill.Files.Take(30))}.";

            var text = File.ReadAllText(full);
            if (text.Contains('\0')) return $"Error: '{file}' is not a text file (run it with run_command if it is a script).";
            return text.Length > 40_000 ? text[..40_000] + "\n[...file cut off]" : text;
        }

        return SkillStore.Render(skill, args.Trim());
    }
}
