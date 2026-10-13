// File tools, confined to the workspace (see Sandbox). Errors are returned as
// plain text so the model can read them and retry - they never crash the loop.

using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore;

public static class FileTools
{
    private const int MaxReadChars = 40_000;
    private const int MaxListEntries = 300;
    private const int MaxSearchHits = 100;

    private static readonly HashSet<string> SkipDirs =
        new(StringComparer.OrdinalIgnoreCase) { ".git", "node_modules", "__pycache__", ".venv", "venv", "bin", "obj" };

    public static IEnumerable<Tool> Create(Sandbox sb)
    {
        yield return new Tool("list_files",
            "List files and folders in the workspace (or a subfolder). Paths are relative to the workspace.",
            Schema.Obj(("path", "string", "Subfolder to list. Default '.'", false),
                       ("recursive", "boolean", "List subfolders too (up to 4 levels deep). Default false.", false)),
            (a, ct) => Task.FromResult(ListFiles(sb, Args.Str(a, "path") ?? ".", Args.Bool(a, "recursive"))));

        yield return new Tool("read_file",
            "Read a text file from the workspace. Output has line numbers (not part of the file). Use start_line/max_lines for big files.",
            Schema.Obj(("path", "string", "File path relative to the workspace.", true),
                       ("start_line", "integer", "First line to show (1-based). Default 1.", false),
                       ("max_lines", "integer", "How many lines to show. Default 2000.", false)),
            (a, ct) => Task.FromResult(ReadFile(sb, Args.Req(a, "path"), Args.Int(a, "start_line") ?? 1, Args.Int(a, "max_lines") ?? 2000)));

        yield return new Tool("write_file",
            "Create a file, or replace its entire contents. Creates parent folders. The old version is backed up.",
            Schema.Obj(("path", "string", "File path relative to the workspace.", true),
                       ("content", "string", "The complete new contents.", true)),
            (a, ct) => Task.FromResult(WriteFile(sb, Args.Req(a, "path"), Args.Req(a, "content"), append: false)));

        yield return new Tool("append_file",
            "Add text to the end of a file (creates it if missing).",
            Schema.Obj(("path", "string", "File path relative to the workspace.", true),
                       ("content", "string", "Text to add.", true)),
            (a, ct) => Task.FromResult(WriteFile(sb, Args.Req(a, "path"), Args.Req(a, "content"), append: true)));

        yield return new Tool("edit_file",
            "Replace exact text in a file. old_text must match exactly once (include enough surrounding lines), unless replace_all is true. Prefer this over write_file for small changes.",
            Schema.Obj(("path", "string", "File path relative to the workspace.", true),
                       ("old_text", "string", "Exact text to find.", true),
                       ("new_text", "string", "Replacement text.", true),
                       ("replace_all", "boolean", "Replace every occurrence. Default false.", false)),
            (a, ct) => Task.FromResult(EditFile(sb, Args.Req(a, "path"), Args.Req(a, "old_text"),
                                                Args.Str(a, "new_text") ?? "", Args.Bool(a, "replace_all"))));

        yield return new Tool("delete_file",
            "Delete a file, or a folder with recursive=true. A file is backed up first; a folder is not.",
            Schema.Obj(("path", "string", "Path relative to the workspace.", true),
                       ("recursive", "boolean", "Required to delete a folder.", false)),
            (a, ct) => Task.FromResult(Delete(sb, Args.Req(a, "path"), Args.Bool(a, "recursive"))));

        yield return new Tool("move_file",
            "Move or rename a file or folder inside the workspace.",
            Schema.Obj(("from", "string", "Existing path.", true), ("to", "string", "New path.", true)),
            (a, ct) => Task.FromResult(Move(sb, Args.Req(a, "from"), Args.Req(a, "to"))));

        yield return new Tool("find_files",
            "Find files by name pattern, e.g. '*.cs' or 'src/**/*.json'.",
            Schema.Obj(("pattern", "string", "Glob pattern. No '/' = match file names anywhere.", true),
                       ("path", "string", "Folder to search. Default '.'", false)),
            (a, ct) => Task.FromResult(FindFiles(sb, Args.Req(a, "pattern"), Args.Str(a, "path") ?? ".")));

        yield return new Tool("search_files",
            "Search file contents with a regular expression (like grep). Returns file:line: text.",
            Schema.Obj(("pattern", "string", "Regular expression.", true),
                       ("path", "string", "Folder or file to search. Default '.'", false),
                       ("glob", "string", "Only search files matching this pattern, e.g. '*.cs'.", false),
                       ("ignore_case", "boolean", "Case-insensitive. Default false.", false)),
            (a, ct) => Task.FromResult(Search(sb, Args.Req(a, "pattern"), Args.Str(a, "path") ?? ".",
                                              Args.Str(a, "glob"), Args.Bool(a, "ignore_case"))));
    }

    // ---- list / find / search ----------------------------------------

    private static string ListFiles(Sandbox sb, string path, bool recursive)
    {
        var dir = sb.Resolve(path);
        if (!Directory.Exists(dir)) return $"Error: folder '{path}' does not exist.";

        var lines = new List<string>();
        Walk(dir, 0, recursive ? 4 : 1);
        void Walk(string d, int depth, int maxDepth)
        {
            if (depth >= maxDepth || lines.Count > MaxListEntries) return;
            foreach (var sub in Directory.EnumerateDirectories(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                lines.Add($"folder: {sb.Rel(sub)}/");
                if (!SkipDirs.Contains(Path.GetFileName(sub))) Walk(sub, depth + 1, maxDepth);
            }
            foreach (var file in Directory.EnumerateFiles(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                lines.Add($"file: {sb.Rel(file)} ({new FileInfo(file).Length} bytes)");
        }

        var where = sb.Rel(dir) == "." ? "the workspace" : $"'{sb.Rel(dir)}'";
        if (lines.Count == 0) return $"{where} is empty.";
        var more = lines.Count > MaxListEntries ? $"\n...list cut off at {MaxListEntries} entries" : "";
        return $"Contents of {where}:\n" + string.Join("\n", lines.Take(MaxListEntries)) + more;
    }

    private static string FindFiles(Sandbox sb, string pattern, string path)
    {
        var dir = sb.Resolve(path);
        if (!Directory.Exists(dir)) return $"Error: folder '{path}' does not exist.";

        var glob = GlobToRegex(pattern);
        var hits = EnumerateFiles(dir).Where(f => MatchesGlob(glob, pattern, sb, f)).Select(sb.Rel).Take(MaxListEntries + 1).ToList();
        if (hits.Count == 0) return $"No files match '{pattern}'.";
        var more = hits.Count > MaxListEntries ? $"\n...cut off at {MaxListEntries}" : "";
        return string.Join("\n", hits.Take(MaxListEntries)) + more;
    }

    private static string Search(Sandbox sb, string pattern, string path, string? globPattern, bool ignoreCase)
    {
        Regex regex;
        try { regex = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { return $"Error: bad regular expression: {ex.Message}"; }

        var target = sb.Resolve(path);
        IEnumerable<string> files;
        if (File.Exists(target)) files = new[] { target };
        else if (Directory.Exists(target)) files = EnumerateFiles(target);
        else return $"Error: '{path}' does not exist.";

        Regex? glob = globPattern == null ? null : GlobToRegex(globPattern);
        var hits = new List<string>();
        foreach (var file in files)
        {
            if (glob != null && !MatchesGlob(glob, globPattern!, sb, file)) continue;
            if (!LooksLikeText(file)) continue;

            var n = 0;
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    n++;
                    if (regex.IsMatch(line))
                    {
                        hits.Add($"{sb.Rel(file)}:{n}: {Text.Clip(line.Trim(), 200)}");
                        if (hits.Count >= MaxSearchHits) return string.Join("\n", hits) + $"\n...stopped at {MaxSearchHits} matches";
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RegexMatchTimeoutException) { }
        }
        return hits.Count == 0 ? $"No matches for '{pattern}'." : string.Join("\n", hits);
    }

    private static IEnumerable<string> EnumerateFiles(string dir)
    {
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            IEnumerable<string> subs, files;
            try { subs = Directory.EnumerateDirectories(d).ToList(); files = Directory.EnumerateFiles(d).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var f in files) yield return f;
            foreach (var s in subs) if (!SkipDirs.Contains(Path.GetFileName(s))) stack.Push(s);
        }
    }

    private static bool LooksLikeText(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (info.Length > 2_000_000) return false;
            using var fs = File.OpenRead(file);
            var buf = new byte[Math.Min(1024, (int)info.Length)];
            var read = fs.Read(buf, 0, buf.Length);
            return !buf.Take(read).Contains((byte)0);
        }
        catch (IOException) { return false; }
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                i++;
                if (i + 1 < glob.Length && glob[i + 1] == '/') { i++; sb.Append("(.*/)?"); }
                else sb.Append(".*");
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
    }

    // A pattern without '/' matches the file name anywhere; otherwise the relative path.
    private static bool MatchesGlob(Regex regex, string pattern, Sandbox sb, string file) =>
        regex.IsMatch(pattern.Contains('/') ? sb.Rel(file) : Path.GetFileName(file));

    // ---- read / write / edit -----------------------------------------

    private static string ReadFile(Sandbox sb, string path, int startLine, int maxLines)
    {
        var full = sb.Resolve(path);
        if (Directory.Exists(full)) return $"Error: '{path}' is a folder. Use list_files.";
        if (!File.Exists(full)) return $"Error: file '{path}' does not exist.";
        if (!LooksLikeText(full) && new FileInfo(full).Length <= 2_000_000) return $"Error: '{path}' is not a text file.";

        var output = new StringBuilder();
        var n = 0;
        var shown = 0;
        var truncated = false;
        foreach (var line in File.ReadLines(full))
        {
            n++;
            if (n < Math.Max(1, startLine)) continue;
            if (shown >= maxLines || output.Length > MaxReadChars) { truncated = true; break; }
            output.Append(n).Append('\t').AppendLine(line);
            shown++;
        }
        if (shown == 0) return n == 0 ? $"'{path}' is empty." : $"Error: '{path}' has only {n} lines.";
        if (truncated) output.Append($"[...stopped after line {n - 1}; use start_line={n} to continue]");
        return output.ToString();
    }

    private static string WriteFile(Sandbox sb, string path, string content, bool append)
    {
        var full = sb.Resolve(path);
        if (full == sb.Root || Directory.Exists(full)) return $"Error: '{path}' is a folder, not a file.";

        var existed = File.Exists(full);
        if (existed) sb.Backup(full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        if (append)
        {
            // Start appended text on its own line instead of gluing it to the last one.
            if (existed && !content.StartsWith('\n') && !content.StartsWith("\r\n"))
            {
                var existing = File.ReadAllText(full);
                if (existing.Length > 0 && !existing.EndsWith('\n')) content = Environment.NewLine + content;
            }
            File.AppendAllText(full, content, Text.Utf8NoBom);
        }
        else File.WriteAllText(full, content, Text.Utf8NoBom);

        var verb = append ? "Appended" : existed ? "Replaced" : "Created";
        return $"{verb} '{sb.Rel(full)}' ({content.Length} chars){(existed ? ", previous version backed up" : "")}.";
    }

    private static string EditFile(Sandbox sb, string path, string oldText, string newText, bool all)
    {
        var full = sb.Resolve(path);
        if (!File.Exists(full)) return $"Error: file '{path}' does not exist.";
        if (oldText.Length == 0) return "Error: old_text is empty.";

        var text = File.ReadAllText(full);
        var count = Count(text, oldText);

        // Files with Windows line endings: the model usually writes \n only.
        if (count == 0 && text.Contains("\r\n") && !oldText.Contains('\r'))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.Replace("\n", "\r\n");
            count = Count(text, oldText);
        }

        if (count == 0) return $"Error: old_text was not found in '{path}'. Re-read the file and copy the text exactly (whitespace matters).";
        if (count > 1 && !all) return $"Error: old_text appears {count} times in '{path}'. Add more surrounding lines to make it unique, or set replace_all=true.";

        sb.Backup(full);
        File.WriteAllText(full, text.Replace(oldText, newText, StringComparison.Ordinal), Text.Utf8NoBom);
        return $"Edited '{sb.Rel(full)}': replaced {count} occurrence{(count == 1 ? "" : "s")}.";
    }

    private static int Count(string text, string find)
    {
        var count = 0;
        for (var i = text.IndexOf(find, StringComparison.Ordinal); i >= 0; i = text.IndexOf(find, i + find.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ---- delete / move ------------------------------------------------

    private static string Delete(Sandbox sb, string path, bool recursive)
    {
        var full = sb.Resolve(path);
        if (full == sb.Root) return "Error: can't delete the workspace itself.";

        if (File.Exists(full))
        {
            sb.Backup(full);
            File.Delete(full);
            return $"Deleted '{sb.Rel(full)}' (backed up first).";
        }
        if (Directory.Exists(full))
        {
            if (!recursive) return $"Error: '{path}' is a folder. Set recursive=true to delete it and everything inside.";
            Directory.Delete(full, recursive: true);
            return $"Deleted folder '{sb.Rel(full)}' and its contents.";
        }
        return $"Error: '{path}' does not exist.";
    }

    private static string Move(Sandbox sb, string from, string to)
    {
        var src = sb.Resolve(from);
        var dst = sb.Resolve(to);
        if (src == sb.Root) return "Error: can't move the workspace itself.";
        if (File.Exists(dst) || Directory.Exists(dst)) return $"Error: '{to}' already exists.";

        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (File.Exists(src)) File.Move(src, dst);
        else if (Directory.Exists(src)) Directory.Move(src, dst);
        else return $"Error: '{from}' does not exist.";
        return $"Moved '{sb.Rel(src)}' to '{sb.Rel(dst)}'.";
    }
}
