// Console output. Plain ANSI colours, switched off when output is piped.

namespace AgentCore;

public static class Ui
{
    private static readonly bool Color = !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") == null;

    private static string Paint(string code, string text) => Color ? $"\u001b[{code}m{text}\u001b[0m" : text;

    public static void Say(string text) => Console.WriteLine(text);
    public static void Info(string text) => Console.WriteLine(Paint("2", text));
    public static void Warn(string text) => Console.WriteLine(Paint("33", text));
    public static void Error(string text) => Console.WriteLine(Paint("31", text));
    public static string Prompt(string text) => Paint("36;1", text);

    public static void Tool(string name, string what, string result)
    {
        if (what.Length > 100) what = what[..100] + "...";
        what = what.Replace('\n', ' ');
        Console.WriteLine(Paint("2", $"  > {name} {what}") + (result.Length > 0 ? Paint("2", $"  => {result}") : ""));
    }
}
