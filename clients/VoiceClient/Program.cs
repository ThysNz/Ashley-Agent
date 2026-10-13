// VoiceClient - the only part of the voice setup that is tied to this PC: it
// owns the microphone and the speakers. Everything else runs in containers:
//
//   mic -> [stt container] -> text -> [agent container /chat] -> reply -> [tts container] -> speaker
//
// Push-to-talk: press Enter to start recording, Enter again to stop.
// You can also type a message instead and press Enter. Ctrl+C while she is
// working stops the task. Press any key while she is speaking to cut her off.
//
//   dotnet run -- [--agent URL] [--stt URL] [--tts URL] [--voice-name NAME]
//
// Start the services first:
//   F:\AI-Agents\voice>   docker compose up -d
//   F:\AI-Agents\ashley>  docker compose --profile server up -d server

using System.Net.Http.Json;
using System.Text.Json;
using NAudio.Wave;

string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var agentUrl = Arg("--agent", "http://127.0.0.1:5000").TrimEnd('/');
var sttUrl = Arg("--stt", "http://127.0.0.1:9001").TrimEnd('/');
var ttsUrl = Arg("--tts", "http://127.0.0.1:9002").TrimEnd('/');
var voiceName = Arg("--voice-name", "");
var recordingPath = Path.Combine(Path.GetTempPath(), "ai-agents-voice-input.wav");
var waveFormat = new WaveFormat(16000, 16, 1); // what Whisper wants

// ---- microphone selection / diagnostics ---------------------------------

if (args.Contains("--list-devices")) { ListMics(); return 0; }
if (args.Contains("--test-mics")) { await TestMics(); return 0; }

var micDevice = -1;                      // -1 = the Windows default input
var micName = "Windows default input";
var micArg = Arg("--mic", "");
if (micArg.Length > 0)
{
    var found = -1;
    if (int.TryParse(micArg, out var micIndex) && micIndex >= 0 && micIndex < WaveInEvent.DeviceCount) found = micIndex;
    else
        for (var i = 0; i < WaveInEvent.DeviceCount && found < 0; i++)
            if (WaveInEvent.GetCapabilities(i).ProductName.Contains(micArg, StringComparison.OrdinalIgnoreCase)) found = i;

    if (found < 0)
    {
        Console.WriteLine($"No microphone matches '{micArg}'. Available inputs:");
        ListMics();
        return 1;
    }
    micDevice = found;
    micName = WaveInEvent.GetCapabilities(found).ProductName;
}

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

Console.WriteLine("Voice client for the AI-Agents containers.");
Console.WriteLine($"Microphone: {micName}   (change with --mic, list with --list-devices)");
var allUp = true;
foreach (var (name, url, hint) in new[]
{
    ("agent", agentUrl, @"cd F:\AI-Agents\ashley ; docker compose --profile server up -d server"),
    ("speech-to-text", sttUrl, @"cd F:\AI-Agents\voice ; docker compose up -d"),
    ("text-to-speech", ttsUrl, @"cd F:\AI-Agents\voice ; docker compose up -d"),
})
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var r = await http.GetAsync(url + "/health", cts.Token);
        r.EnsureSuccessStatusCode();
        Console.WriteLine($"  ok   {name} ({url})");
    }
    catch (Exception)
    {
        allUp = false;
        Console.WriteLine($"  DOWN {name} ({url})  ->  {hint}");
    }
}
if (!allUp) { Console.WriteLine("Start the missing services, then run this again."); return 1; }

// The voice chosen last time is remembered by the agent (--voice-name overrides it).
if (voiceName.Length == 0)
{
    try { voiceName = (await http.GetFromJsonAsync<JsonElement>(agentUrl + "/voice")).GetProperty("voice").GetString() ?? ""; }
    catch (Exception) { /* fall back to the speech service's default voice */ }
}

CancellationTokenSource? turnCts = null;
Console.CancelKeyPress += (_, e) =>
{
    if (turnCts is { IsCancellationRequested: false }) { e.Cancel = true; turnCts.Cancel(); }
};

Console.WriteLine();
Console.WriteLine("Press Enter to talk, or type a message. Type /help for voice and personality commands, 'quit' to exit.");

while (true)
{
    Console.Write("\n> ");
    var line = Console.ReadLine();
    if (line == null) break;
    line = line.Trim();

    if (line.Equals("quit", StringComparison.OrdinalIgnoreCase) || line.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
    if (line.StartsWith('/'))
    {
        try { await HandleCommand(line); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Console.WriteLine($"Command failed: {ex.Message}");
        }
        continue;
    }

    string you;
    if (line.Length > 0)
    {
        you = line; // typed instead of spoken
    }
    else
    {
        Record();
        turnCts = new CancellationTokenSource();
        try
        {
            using var wav = new ByteArrayContent(await File.ReadAllBytesAsync(recordingPath));
            var stt = await http.PostAsync(sttUrl + "/stt", wav, turnCts.Token);
            stt.EnsureSuccessStatusCode();
            you = (await stt.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: turnCts.Token)).GetProperty("text").GetString()?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"Speech-to-text failed: {ex.Message}");
            continue;
        }
        finally { turnCts.Dispose(); turnCts = null; }

        if (you.Length == 0) { Console.WriteLine("Didn't catch that - try again."); continue; }
        Console.WriteLine($"You: {you}");
    }

    turnCts = new CancellationTokenSource();
    try
    {
        Console.WriteLine("(thinking... Ctrl+C to stop)");
        var response = await http.PostAsJsonAsync(agentUrl + "/chat", new { text = you, voice = true }, turnCts.Token);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: turnCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine("Agent error: " + (body.TryGetProperty("error", out var err) ? err.GetString() : response.StatusCode.ToString()));
            continue;
        }

        foreach (var t in body.GetProperty("tools").EnumerateArray())
            Console.WriteLine($"  > {t.GetString()}");

        var reply = body.GetProperty("reply").GetString() ?? "";
        var spoken = body.GetProperty("spoken").GetString() ?? "";
        Console.WriteLine($"\nAshley: {reply}");

        if (spoken.Length > 0) await Speak(spoken, turnCts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\n(stopped)");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"Couldn't reach a service: {ex.Message}");
    }
    finally { turnCts.Dispose(); turnCts = null; }
}

Console.WriteLine("Goodbye.");
return 0;

// ---- microphone ------------------------------------------------------

void Record()
{
    var peak = 0;
    var bytes = 0L;
    using var waveIn = new WaveInEvent { WaveFormat = waveFormat, DeviceNumber = micDevice };
    using (var writer = new WaveFileWriter(recordingPath, waveFormat))
    {
        waveIn.DataAvailable += (_, e) =>
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            bytes += e.BytesRecorded;
            peak = Math.Max(peak, PeakOf(e.Buffer, e.BytesRecorded));
        };
        waveIn.StartRecording();
        Console.WriteLine($"Recording from {micName}... press Enter to stop.");
        Console.ReadLine();
        waveIn.StopRecording();
        Thread.Sleep(200); // let the last buffer land
    }

    var level = peak / 327.68;
    Console.WriteLine($"Recorded {bytes / 32000.0:0.0}s, loudest point {level:0}% of full scale.");
    if (level < 1)
        Console.WriteLine("The microphone recorded silence. Check Windows input settings, or choose another input: " +
                          "run with --list-devices, then --mic <number or part of the name> (or --test-mics to find the live one).");
}

static int PeakOf(byte[] buffer, int count)
{
    var peak = 0;
    for (var i = 0; i + 1 < count; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(buffer, i)));
    return peak;
}

// Windows' own idea of the default input, to mark it in the list.
static string DefaultMicName()
{
    try
    {
        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        return enumerator.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console).FriendlyName;
    }
    catch (Exception) { return ""; }
}

void ListMics()
{
    var def = DefaultMicName();
    Console.WriteLine($"Input devices (Windows default: {(def.Length > 0 ? def : "unknown")}):");
    for (var i = 0; i < WaveInEvent.DeviceCount; i++)
    {
        var name = WaveInEvent.GetCapabilities(i).ProductName; // Windows cuts these at 31 characters
        var isDefault = def.Length > 0 && def.StartsWith(name.TrimEnd(), StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($" {(isDefault ? "*" : " ")} {i}: {name}");
    }
    Console.WriteLine("Use one with:  --mic <number or part of the name>");
}

// Records every input for 5 seconds at once and prints how loud each one was.
// Speak, or play something near the microphone, while it runs.
async Task TestMics()
{
    ListMics();
    Console.WriteLine("\nListening on all inputs for 5 seconds - speak now...");
    var peaks = new int[WaveInEvent.DeviceCount];
    var inputs = new List<WaveInEvent>();
    for (var i = 0; i < WaveInEvent.DeviceCount; i++)
    {
        var index = i;
        try
        {
            var input = new WaveInEvent { WaveFormat = waveFormat, DeviceNumber = index };
            input.DataAvailable += (_, e) => peaks[index] = Math.Max(peaks[index], PeakOf(e.Buffer, e.BytesRecorded));
            input.StartRecording();
            inputs.Add(input);
        }
        catch (Exception ex) { Console.WriteLine($"  {index}: couldn't open ({ex.Message})"); }
    }
    await Task.Delay(5000);
    foreach (var input in inputs) { input.StopRecording(); input.Dispose(); }

    Console.WriteLine("\nLoudest point per input:");
    for (var i = 0; i < peaks.Length; i++)
        Console.WriteLine($"  {i}: {WaveInEvent.GetCapabilities(i).ProductName,-34} {peaks[i] / 327.68,5:0.0}%{(peaks[i] / 327.68 >= 1 ? "   <-- hears sound" : "")}");
}

// ---- speaker ---------------------------------------------------------

async Task Speak(string text, CancellationToken ct)
{
    var request = new Dictionary<string, string> { ["text"] = text };
    if (voiceName.Length > 0) request["voice"] = voiceName;

    var response = await http.PostAsJsonAsync(ttsUrl + "/tts", request, ct);
    if (!response.IsSuccessStatusCode) { Console.WriteLine($"(text-to-speech failed: {(int)response.StatusCode})"); return; }
    var audio = await response.Content.ReadAsByteArrayAsync(ct);

    using var ms = new MemoryStream(audio);
    using var reader = new WaveFileReader(ms);
    using var output = new WaveOutEvent();
    output.Init(reader);
    output.Play();
    while (output.PlaybackState == PlaybackState.Playing)
    {
        if (ct.IsCancellationRequested || (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(true) != default)) { output.Stop(); break; }
        await Task.Delay(50, CancellationToken.None);
    }
}

// ---- voice and personality commands ----------------------------------

async Task HandleCommand(string line)
{
    var space = line.IndexOf(' ');
    var cmd = (space < 0 ? line : line[..space]).ToLowerInvariant();
    var arg = space < 0 ? "" : line[(space + 1)..].Trim();

    switch (cmd)
    {
        case "/help":
            Console.WriteLine("""
                Voice:
                  /voices              installed voices (* = current)
                  /voice <name>        switch voice (a part of the name is enough)
                  /findvoice <words>   search the Piper catalog, e.g. /findvoice en_GB   or   /findvoice amy
                  /addvoice <name>     download a voice from the catalog (about 60 MB), then /voice it
                Personality:
                  /personas            list personalities (* = current)
                  /persona <name>      switch personality (the conversation is kept)
                Other:
                  /reset               forget the conversation
                  quit                 exit
                """);
            break;

        case "/reset":
            await http.PostAsync(agentUrl + "/reset", null);
            Console.WriteLine("Conversation cleared.");
            break;

        case "/voices":
        {
            var (names, def) = await InstalledVoices();
            var current = voiceName.Length > 0 ? voiceName : def;
            foreach (var n in names) Console.WriteLine($" {(n == current ? "*" : " ")} {n}");
            break;
        }

        case "/voice":
        {
            var (names, _) = await InstalledVoices();
            var matches = names.Where(n => n.Equals(arg, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0 && arg.Length > 0) matches = names.Where(n => n.Contains(arg, StringComparison.OrdinalIgnoreCase)).ToList();
            if (arg.Length == 0) Console.WriteLine("Usage: /voice <name>. See /voices.");
            else if (matches.Count == 0) Console.WriteLine($"No installed voice matches '{arg}'. See /voices, or /findvoice and /addvoice to get more.");
            else if (matches.Count > 1) Console.WriteLine("More than one matches: " + string.Join(", ", matches));
            else
            {
                voiceName = matches[0];
                await http.PostAsJsonAsync(agentUrl + "/voice", new { name = voiceName }); // remembered for next time
                Console.WriteLine($"Voice is now {voiceName}.");
                await Speak("Hello, this is how I sound now.", CancellationToken.None);
            }
            break;
        }

        case "/findvoice":
        {
            var result = await http.GetFromJsonAsync<JsonElement>($"{ttsUrl}/voices/catalog?q={Uri.EscapeDataString(arg)}");
            if (result.TryGetProperty("error", out var e)) { Console.WriteLine(e.GetString()); break; }
            var total = result.GetProperty("total").GetInt32();
            foreach (var v in result.GetProperty("voices").EnumerateArray())
                Console.WriteLine($" {(v.GetProperty("installed").GetBoolean() ? "*" : " ")} {v.GetProperty("name").GetString(),-34} {v.GetProperty("language").GetString(),-26} {v.GetProperty("quality").GetString()}");
            Console.WriteLine(total == 0 ? "No voices match." : $"{total} match (showing up to 40; * = already installed). Add one with /addvoice <name>.");
            if (arg.Length == 0) Console.WriteLine("(Showing English voices. Search by language code, e.g. /findvoice de_DE)");
            break;
        }

        case "/addvoice":
        {
            if (arg.Length == 0) { Console.WriteLine("Usage: /addvoice <exact name from /findvoice>"); break; }
            Console.WriteLine($"Downloading {arg} ...");
            var response = await http.PostAsJsonAsync(ttsUrl + "/voices/add", new { name = arg });
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!response.IsSuccessStatusCode) { Console.WriteLine("Failed: " + (body.TryGetProperty("error", out var err) ? err.GetString() : response.StatusCode)); break; }
            Console.WriteLine($"Installed {arg}. Switch to it with /voice {arg}");
            break;
        }

        case "/personas":
        {
            var result = await http.GetFromJsonAsync<JsonElement>(agentUrl + "/personas");
            var current = result.GetProperty("current").GetString();
            foreach (var p in result.GetProperty("personas").EnumerateArray())
                Console.WriteLine($" {(p.GetString() == current ? "*" : " ")} {p.GetString()}");
            break;
        }

        case "/persona":
        {
            if (arg.Length == 0) { Console.WriteLine("Usage: /persona <name>. See /personas."); break; }
            var response = await http.PostAsJsonAsync(agentUrl + "/persona", new { name = arg });
            Console.WriteLine(response.IsSuccessStatusCode
                ? $"Personality is now {arg}."
                : $"No personality called '{arg}'. See /personas.");
            break;
        }

        default:
            Console.WriteLine($"Unknown command {cmd}. Try /help.");
            break;
    }
}

async Task<(List<string> Names, string Default)> InstalledVoices()
{
    var result = await http.GetFromJsonAsync<JsonElement>(ttsUrl + "/voices");
    return (result.GetProperty("voices").EnumerateArray().Select(v => v.GetString()!).ToList(),
            result.GetProperty("default").GetString() ?? "");
}
