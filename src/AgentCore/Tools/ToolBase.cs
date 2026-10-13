// Shared plumbing for tools: the Tool record, argument helpers, the
// workspace sandbox, and a few text utilities.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCore;

public sealed record Tool(string Name, string Description, JsonObject Schema,
                          Func<JsonElement, CancellationToken, Task<string>> Run);

public static class Schema
{
    // Builds {"type":"object","properties":{...},"required":[...]}.
    public static JsonObject Obj(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.Required) required.Add(p.Name);
        }
        return new JsonObject { ["type"] = "object", ["properties"] = properties, ["required"] = required };
    }
}

public static class Args
{
    public static string? Str(JsonElement a, string key)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null => null,
            _ => v.ToString(),
        };
    }

    public static string Req(JsonElement a, string key) =>
        Str(a, key) ?? throw new ArgumentException($"missing required argument '{key}'.");

    public static int? Int(JsonElement a, string key)
    {
        var s = Str(a, key);
        return int.TryParse(s, out var n) ? n : null;
    }

    public static bool Bool(JsonElement a, string key, bool fallback = false)
    {
        var s = Str(a, key);
        return s == null ? fallback : s.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}

public static class Text
{
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..max] + $"... [{s.Length - max} more chars]";

    // Keeps the start and the end - for command output the end (the error) matters most.
    public static string ClipMiddle(string s, int head, int tail)
    {
        if (s.Length <= head + tail) return s;
        return s[..head] + $"\n... [{s.Length - head - tail} chars omitted] ...\n" + s[^tail..];
    }
}

// The file tools only touch files under the workspace folder. (run_command is
// not confined by this - the container itself is the sandbox there.)
// Overwritten/deleted files are copied to <memory>/backups first, because the
// workspace is a real folder on the host and a bad edit shouldn't be final.
public sealed class Sandbox
{
    private readonly string _backupRoot;
    private readonly string _session = DateTime.Now.ToString("yyyyMMdd_HHmmss");

    public string Root { get; }

    public Sandbox(string root, string memoryDir)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _backupRoot = Path.Combine(memoryDir, "backups", _session);
    }

    public string Resolve(string? relative)
    {
        var trimmed = (relative ?? ".").Trim().Trim('"');
        if (trimmed.Length == 0) trimmed = ".";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(Root, trimmed)));

        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inside = string.Equals(full, Root, cmp) || full.StartsWith(Root + Path.DirectorySeparatorChar, cmp);
        if (!inside) throw new ArgumentException($"'{relative}' is outside the workspace - the file tools can't go there.");
        return full;
    }

    public string Rel(string full)
    {
        var r = Path.GetRelativePath(Root, full).Replace('\\', '/');
        return r == "." ? "." : r;
    }

    public string? Backup(string full)
    {
        if (!File.Exists(full)) return null;
        var dest = Path.Combine(_backupRoot, Path.GetRelativePath(Root, full));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.Copy(full, dest, overwrite: true);
        return dest;
    }
}
