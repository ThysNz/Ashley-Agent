// Internet access: fetch a page as text, and a simple web search.
// web_search scrapes DuckDuckGo's HTML endpoint (no API key). That page
// format can change or block automated use; when it does the tool says so
// and the agent can fall back to web_fetch on a known URL.

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentCore;

public static class WebTools
{
    private const int MaxDownloadBytes = 2_000_000;

    private static readonly Regex Scripts = new(@"<(script|style|noscript|svg|head)\b.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    private static readonly Regex Breaks = new(@"</?(p|div|br|li|tr|h[1-6]|section|article|pre|table|ul|ol)\b[^>]*>", RegexOptions.IgnoreCase);
    private static readonly Regex Tags = new(@"<[^>]+>");
    private static readonly Regex Spaces = new(@"[ \t ]+");
    private static readonly Regex BlankLines = new(@"(\s*\n){3,}");

    public static IEnumerable<Tool> Create(HttpClient http)
    {
        yield return new Tool("web_fetch",
            "Download a web page or text file and return its text (HTML is converted to plain text).",
            Schema.Obj(("url", "string", "http:// or https:// address.", true),
                       ("max_chars", "integer", "Cut the result to this many characters. Default 12000, max 40000.", false)),
            (a, ct) => FetchAsync(http, Args.Req(a, "url"), Math.Clamp(Args.Int(a, "max_chars") ?? 12000, 500, 40000), ct));

        yield return new Tool("web_search",
            "Search the web. Returns titles, URLs and snippets; use web_fetch to read a result.",
            Schema.Obj(("query", "string", "What to search for.", true)),
            (a, ct) => SearchAsync(http, Args.Req(a, "query"), ct));
    }

    private static async Task<string> FetchAsync(HttpClient http, string url, int maxChars, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "Error: give a full http:// or https:// URL.";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            AddBrowserHeaders(request);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return $"Error: {(int)response.StatusCode} {response.ReasonPhrase} from {uri.Host}.";

            var type = response.Content.Headers.ContentType?.MediaType ?? "";
            var textual = type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") ||
                          type.Contains("javascript") || type == "";
            if (!textual) return $"Error: that URL is {type}, not text. Download it with run_command (curl -L -o file URL) instead.";

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0 && buffer.Length < MaxDownloadBytes)
                buffer.Write(chunk, 0, read);

            var body = Encoding.UTF8.GetString(buffer.ToArray());
            var text = type.Contains("html") ? HtmlToText(body) : body;
            return Text.Clip(text.Trim(), maxChars);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"Error fetching {uri.Host}: {ex.Message}";
        }
    }

    private static async Task<string> SearchAsync(HttpClient http, string query, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query));
            AddBrowserHeaders(request);
            using var response = await http.SendAsync(request, ct);
            var html = await response.Content.ReadAsStringAsync(ct);

            var links = Regex.Matches(html, @"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline);
            var snippets = Regex.Matches(html, @"class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline);

            var results = new StringBuilder();
            for (var i = 0; i < Math.Min(8, links.Count); i++)
            {
                var title = Clean(links[i].Groups[2].Value);
                var snippet = i < snippets.Count ? Clean(snippets[i].Groups[1].Value) : "";
                results.AppendLine($"{i + 1}. {title}\n   {RealUrl(links[i].Groups[1].Value)}\n   {snippet}");
            }
            return results.Length > 0
                ? results.ToString().TrimEnd()
                : "No results parsed - the search page may have changed or blocked the request. Try web_fetch on a URL you know.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return $"Error searching: {ex.Message}";
        }
    }

    // DuckDuckGo wraps result links as //duckduckgo.com/l/?uddg=<encoded real url>.
    private static string RealUrl(string href)
    {
        href = WebUtility.HtmlDecode(href);
        var m = Regex.Match(href, @"[?&]uddg=([^&]+)");
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        return href.StartsWith("//") ? "https:" + href : href;
    }

    private static string Clean(string html) => Spaces.Replace(WebUtility.HtmlDecode(Tags.Replace(html, "")), " ").Trim();

    private static string HtmlToText(string html)
    {
        var titleMatch = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var title = titleMatch.Success ? Clean(titleMatch.Groups[1].Value) : "";

        html = Scripts.Replace(html, " ");
        html = Breaks.Replace(html, "\n");
        html = WebUtility.HtmlDecode(Tags.Replace(html, ""));
        html = string.Join("\n", html.Split('\n').Select(l => Spaces.Replace(l, " ").Trim()));
        html = BlankLines.Replace(html, "\n\n").Trim();
        return title.Length > 0 ? $"Page title: {title}\n\n{html}" : html;
    }

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en");
    }
}
