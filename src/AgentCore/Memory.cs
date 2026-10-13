// Long-term memory and the session log. All plain text in the memory folder,
// which is a host folder mounted into the container - so it survives
// container rebuilds and can be read or edited by hand.
//
//   notes.md          what the agent knows (read into every prompt)
//   todo.md           the agent's task list
//   sessions/*.jsonl  every message of every session, written as it happens
//   backups/          copies of files before they were overwritten/deleted
//   state.json        last chosen model/persona

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCore;

public sealed class MemoryStore
{
    private readonly string _dir;
    private readonly int _maxNotesChars;
    private readonly object _logLock = new();

    public string NotesPath => Path.Combine(_dir, "notes.md");
    public string TodoPath => Path.Combine(_dir, "todo.md");
    private string SessionLogPath => Path.Combine(_dir, "sessions", DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl");

    public MemoryStore(string dir, int maxNotesChars)
    {
        _dir = dir;
        _maxNotesChars = maxNotesChars;
        Directory.CreateDirectory(Path.Combine(dir, "sessions"));
    }

    public string ReadNotes()
    {
        var text = ReadFile(NotesPath);
        if (text.Length <= _maxNotesChars) return text;
        // Newest notes are at the bottom, so keep the tail.
        return "[older notes cut off - tidy notes.md]\n" + text[^_maxNotesChars..];
    }

    public string Remember(string note)
    {
        note = note.Trim().Replace("\r", "").Replace("\n", " ");
        if (note.Length == 0) return "Error: nothing to remember.";
        if (ReadFile(NotesPath).Split('\n').Any(l => l.Contains(note, StringComparison.OrdinalIgnoreCase)))
            return "Already in my notes.";

        Directory.CreateDirectory(_dir);
        File.AppendAllText(NotesPath, $"- {note} ({DateTime.Now:yyyy-MM-dd})\n", Text.Utf8NoBom);
        return "Saved to notes.";
    }

    public string Forget(string match)
    {
        match = match.Trim();
        if (match.Length < 3) return "Error: give at least 3 characters to match.";
        var lines = ReadFile(NotesPath).Split('\n').ToList();
        var kept = lines.Where(l => !l.Contains(match, StringComparison.OrdinalIgnoreCase)).ToList();
        var removed = lines.Count - kept.Count;
        if (removed == 0) return $"No note contains '{match}'.";
        File.WriteAllText(NotesPath, string.Join("\n", kept), Text.Utf8NoBom);
        return $"Removed {removed} note line{(removed == 1 ? "" : "s")}.";
    }

    // ---- todo list ----------------------------------------------------

    public string Todo(string action, string? item)
    {
        var items = ReadFile(TodoPath).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        string result;

        switch (action.ToLowerInvariant())
        {
            case "list":
                return items.Count == 0 ? "The todo list is empty." : Format(items);
            case "add":
                if (string.IsNullOrWhiteSpace(item)) return "Error: 'add' needs an item.";
                items.Add("[ ] " + item.Trim());
                result = "Added.";
                break;
            case "done":
            case "undone":
            case "remove":
                if (!int.TryParse(item, out var n) || n < 1 || n > items.Count)
                    return $"Error: give the item number (1-{items.Count}).";
                if (action == "remove") items.RemoveAt(n - 1);
                else items[n - 1] = (action == "done" ? "[x] " : "[ ] ") + items[n - 1][3..].TrimStart();
                result = "Updated.";
                break;
            case "clear":
                items.Clear();
                result = "Cleared.";
                break;
            default:
                return "Error: action must be list, add, done, undone, remove or clear.";
        }

        File.WriteAllText(TodoPath, string.Join("\n", items) + (items.Count > 0 ? "\n" : ""), Text.Utf8NoBom);
        return items.Count == 0 ? result + " The list is now empty." : result + "\n" + Format(items);

        static string Format(List<string> list) => string.Join("\n", list.Select((l, i) => $"{i + 1}. {l}"));
    }

    // ---- session log --------------------------------------------------

    public void Log(Msg m)
    {
        var entry = new JsonObject
        {
            ["time"] = DateTime.Now.ToString("s"),
            ["role"] = m.Role,
            ["content"] = m.Content,
        };
        if (m.ToolName != null) entry["tool"] = m.ToolName;
        if (m.ToolCalls is { Count: > 0 })
            entry["tool_calls"] = JsonSerializer.SerializeToNode(m.ToolCalls.Select(c => new { c.Name, c.ArgsJson }));

        try
        {
            lock (_logLock) File.AppendAllText(SessionLogPath, entry.ToJsonString() + "\n", Text.Utf8NoBom);
        }
        catch (IOException) { /* logging must never break a turn */ }
    }

    private static string ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (IOException) { return ""; }
    }
}

public static class MemoryTools
{
    public static IEnumerable<Tool> Create(MemoryStore memory)
    {
        yield return new Tool("remember",
            "Save a lasting fact to your long-term notes: about Thys, his projects, preferences or decisions. One short, self-contained, verified sentence. " +
            "NOT for logging what you did in a task. Notes are shown to you at the start of every future session.",
            Schema.Obj(("note", "string", "The fact to remember.", true)),
            (a, ct) => Task.FromResult(memory.Remember(Args.Req(a, "note"))));

        yield return new Tool("forget",
            "Remove notes that contain the given text (use when a note is wrong or out of date).",
            Schema.Obj(("match", "string", "Text contained in the note(s) to delete.", true)),
            (a, ct) => Task.FromResult(memory.Forget(Args.Req(a, "match"))));

        yield return new Tool("todo",
            "Your task list for multi-step work. Actions: list, add (item=text), done / undone / remove (item=number), clear. Plan with it for any job of three or more steps and tick items off as you go.",
            Schema.Obj(("action", "string", "list | add | done | undone | remove | clear", true),
                       ("item", "string", "Text for add, or item number for done/undone/remove.", false)),
            (a, ct) => Task.FromResult(memory.Todo(Args.Req(a, "action"), Args.Str(a, "item"))));
    }
}
