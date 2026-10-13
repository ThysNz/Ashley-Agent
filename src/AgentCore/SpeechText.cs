// Turns a written reply (markdown, lists, code, links) into something that
// sounds right when a text-to-speech voice reads it. Models in voice mode
// still slip into bullet lists now and then, so this is the safety net.

using System.Text.RegularExpressions;

namespace AgentCore;

public static class SpeechText
{
    private const int MaxChars = 600;

    private static readonly Regex CodeBlock = new(@"```.*?```", RegexOptions.Singleline);
    private static readonly Regex InlineCode = new(@"`([^`]*)`");
    private static readonly Regex Url = new(@"https?://\S+");
    private static readonly Regex Bullet = new(@"^\s*(?:[-*+•]|\d+[.)])\s+");
    private static readonly Regex Heading = new(@"^\s*#{1,6}\s*");
    // Underscores inside words (date_calculator.py) are kept; only emphasis markers go.
    private static readonly Regex Emphasis = new(@"(\*\*|\*|(?<![\w])__?|__?(?![\w]))");
    private static readonly Regex Spaces = new(@"[ \t]+");

    public static string Clean(string text)
    {
        text = CodeBlock.Replace(text, " (code omitted) ");
        text = InlineCode.Replace(text, "$1");
        text = Url.Replace(text, "a link");

        var sentences = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Heading.Replace(Bullet.Replace(raw, ""), "");
            line = Spaces.Replace(Emphasis.Replace(line, ""), " ").Trim();
            if (line.Length == 0) continue;
            sentences.Add(line.EndsWith('.') || line.EndsWith('!') || line.EndsWith('?') || line.EndsWith(':') ? line : line + ".");
        }

        var spoken = string.Join(" ", sentences).Replace(":.", ":").Trim();
        if (spoken.Length <= MaxChars) return spoken;

        // Too long to listen to: stop at the last full sentence that fits.
        var cut = spoken[..MaxChars];
        var end = cut.LastIndexOfAny(new[] { '.', '!', '?' });
        return (end > 100 ? cut[..(end + 1)] : cut) + " The rest is in the written reply.";
    }
}
