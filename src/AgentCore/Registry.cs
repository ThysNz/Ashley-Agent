// Two small stores used by the web page:
//
//   ModelRegistry  the models from config\models.json (read-only) plus any the user
//                  added in the page, kept in memory\models.local.json
//   ChatStore      the current conversation (memory\chats\current.json) and
//                  archived ones (memory\chats\<id>.json), so a restart or
//                  "New chat" doesn't lose anything

using System.Text.Json;

namespace AgentCore;

public sealed class ModelRegistry
{
    private readonly List<ModelProfile> _configured;
    private readonly List<ModelProfile> _local = new();
    private readonly string _localPath;

    public ModelRegistry(List<ModelProfile> configured, Paths paths)
    {
        _configured = configured;
        _localPath = Path.Combine(paths.Memory, "models.local.json");
        try
        {
            if (File.Exists(_localPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(_localPath));
                var saved = doc.RootElement.GetProperty("models").Deserialize<List<ModelProfile>>(ConfigLoader.Options);
                if (saved != null) _local.AddRange(saved.Where(m => !_configured.Any(c => SameName(c.Name, m.Name))));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException) { }
    }

    private static bool SameName(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ModelProfile> All => _configured.Concat(_local).ToList();

    public ModelProfile? Find(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : All.FirstOrDefault(m => SameName(m.Name, name.Trim()));

    public bool IsLocal(string name) => _local.Any(m => SameName(m.Name, name));

    public string? AddLocal(ModelProfile profile)
    {
        if (profile.Name.Length == 0 || profile.Model.Length == 0) return "A model needs a name and a model id.";
        if (Find(profile.Name) != null) return $"There is already a model called '{profile.Name}'.";
        _local.Add(profile);
        Save();
        return null;
    }

    public bool RemoveLocal(string name)
    {
        var removed = _local.RemoveAll(m => SameName(m.Name, name)) > 0;
        if (removed) Save();
        return removed;
    }

    private void Save()
    {
        try { File.WriteAllText(_localPath, JsonSerializer.Serialize(new { models = _local }, ConfigLoader.Options), Text.Utf8NoBom); }
        catch (IOException) { }
    }
}

public sealed record StoredCall(string Id, string Name, string Args);
public sealed record StoredMsg(string Role, string Content, List<StoredCall>? Calls, string? ToolCallId, string? ToolName);
public sealed record StoredChat(string Title, DateTime Time, List<StoredMsg> Messages);
public sealed record ChatSummary(string Id, string Title, DateTime Time, int Messages);

public sealed class ChatStore
{
    private readonly string _dir;
    private readonly object _lock = new();

    public ChatStore(string memoryDir)
    {
        _dir = Path.Combine(memoryDir, "chats");
        Directory.CreateDirectory(_dir);
    }

    private string CurrentPath => Path.Combine(_dir, "current.json");

    private static StoredMsg ToStored(Msg m) =>
        new(m.Role, m.Content, m.ToolCalls?.Select(c => new StoredCall(c.Id, c.Name, c.ArgsJson)).ToList(), m.ToolCallId, m.ToolName);

    private static Msg FromStored(StoredMsg s) =>
        new(s.Role, s.Content, s.Calls?.Select(c => new ToolCall(c.Id, c.Name, c.Args)).ToList(), s.ToolCallId, s.ToolName);

    private static string TitleOf(IEnumerable<Msg> messages)
    {
        var first = messages.FirstOrDefault(m => m.Role == "user")?.Content.Trim().Replace('\n', ' ') ?? "Empty chat";
        return first.Length > 70 ? first[..70] + "..." : first;
    }

    private void Write(string path, StoredChat chat)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(chat, ConfigLoader.Options), Text.Utf8NoBom);
        File.Move(temp, path, overwrite: true);
    }

    private static StoredChat? Read(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<StoredChat>(File.ReadAllText(path), ConfigLoader.Options) : null; }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    public void SaveCurrent(IReadOnlyList<Msg> messages)
    {
        lock (_lock)
        {
            try { Write(CurrentPath, new StoredChat(TitleOf(messages), DateTime.Now, messages.Select(ToStored).ToList())); }
            catch (IOException) { }
        }
    }

    public List<Msg> LoadCurrent()
    {
        lock (_lock) return Read(CurrentPath)?.Messages.Select(FromStored).ToList() ?? new List<Msg>();
    }

    // Moves the current conversation into the archive (if it has anything in it).
    public string? ArchiveCurrent(IReadOnlyList<Msg> messages)
    {
        lock (_lock)
        {
            if (!messages.Any(m => m.Role == "user")) return null;
            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            while (File.Exists(Path.Combine(_dir, id + ".json"))) id += "x";
            Write(Path.Combine(_dir, id + ".json"), new StoredChat(TitleOf(messages), DateTime.Now, messages.Select(ToStored).ToList()));
            File.Delete(CurrentPath);
            return id;
        }
    }

    public List<ChatSummary> List()
    {
        lock (_lock)
        {
            return Directory.GetFiles(_dir, "*.json")
                .Where(f => Path.GetFileName(f) != "current.json")
                .Select(f => (id: Path.GetFileNameWithoutExtension(f), chat: Read(f)))
                .Where(x => x.chat != null)
                .OrderByDescending(x => x.id)
                .Select(x => new ChatSummary(x.id, x.chat!.Title, x.chat.Time, x.chat.Messages.Count(m => m.Role == "user")))
                .ToList();
        }
    }

    public List<Msg>? Open(string id)
    {
        if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c == '-'))) return null; // ids look like 20261014-093000
        lock (_lock) return Read(Path.Combine(_dir, id + ".json"))?.Messages.Select(FromStored).ToList();
    }
}
