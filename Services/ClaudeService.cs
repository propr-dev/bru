using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

public record ClaudeResponse(string Text, List<PointTarget> Points);
public record PointTarget(int X, int Y, string Label);

/// <summary>
/// Streams responses from Claude (claude-sonnet-4-6) via Server-Sent Events.
/// Parses [POINT:x,y:label] tags from the response for overlay positioning.
/// Maintains conversation history for context across turns.
/// </summary>
public partial class ClaudeService
{
    private static readonly HttpClient Http = new();

    // Match [POINT:123,456:element_name] tags in Claude's response
    [GeneratedRegex(@"\[POINT:(\d+),(\d+):([^\]]*)\]")]
    private static partial Regex PointTagRegex();

    // Match the explicit "nothing to point at" marker: [POINT:none]
    [GeneratedRegex(@"\[POINT:\s*none\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex PointNoneRegex();

    // ── Screen-annotation tags (drawn temporarily over the user's screen) ──
    [GeneratedRegex(@"\[BOX:(\d+),(\d+),(\d+),(\d+):([^\]]*)\]")]
    private static partial Regex BoxTagRegex();

    [GeneratedRegex(@"\[ARROW:(\d+),(\d+),(\d+),(\d+):([^\]]*)\]")]
    private static partial Regex ArrowTagRegex();

    [GeneratedRegex(@"\[LINE:(\d+),(\d+),(\d+),(\d+)\]")]
    private static partial Regex LineTagRegex();

    [GeneratedRegex(@"\[NOTE:(\d+),(\d+):([^\]]*)\]")]
    private static partial Regex NoteTagRegex();

    // Ported from the original macOS Clicky's voice prompt (the personality that made it
    // feel alive), adapted for Bru: same lowercase spoken-word style, same "err on the
    // side of pointing" philosophy, same dimension-anchored coordinate space.
    private static readonly string SystemPrompt = """
        you're bru, a friendly always-on companion that lives on the user's windows desktop. the user just spoke to you via push-to-talk and you can see their screen. your reply will be spoken aloud via text-to-speech, so write the way you'd actually talk. this is an ongoing conversation — you remember everything they've said before.

        rules:
        - default to one or two sentences. be direct and dense. BUT if the user asks you to explain more, go deeper, or elaborate, then go all out — give a thorough, detailed explanation with no length limit.
        - all lowercase, casual, warm. no emojis. always respond in english.
        - write for the ear, not the eye. short sentences. no lists, bullet points, markdown, or formatting — just natural speech.
        - don't use abbreviations or symbols that sound weird read aloud. write "for example" not "e.g.", spell out small numbers.
        - if the user's question relates to what's on their screen, reference specific things you see.
        - if the screenshot doesn't seem relevant to their question, just answer the question directly.
        - you can help with anything — coding, writing, general knowledge, brainstorming.
        - never say "simply" or "just".
        - don't read out code verbatim. describe what the code does or what needs to change conversationally.
        - don't end with dead-end yes/no questions like "want me to explain more?". instead, when it fits naturally, end by planting a seed — something bigger they could try, a related idea worth going deeper on. it's okay to end with nothing extra if the answer is complete on its own.
        - if you receive multiple screen images, the one labeled "primary focus" is where the cursor is — prioritize that one.

        element pointing:
        you have a small glowing cursor buddy that can fly to and point at things on screen. use it whenever pointing would genuinely help — if the user is asking how to do something, looking for a menu, trying to find a button, or needs help navigating an app, point at the relevant element. err on the side of pointing rather than not pointing, because it makes your help way more useful and concrete.

        don't point when it would be pointless — general knowledge questions, or when the conversation has nothing to do with the screen.

        when you point, append a coordinate tag at the very end of your response, AFTER your spoken text. each screenshot's label states its exact pixel dimensions — use those dimensions as the coordinate space. the origin (0,0) is the top-left corner of the image. x increases rightward, y increases downward. only point at elements on the "primary focus" screen.

        format: [POINT:x,y:label] where x,y are integer pixel coordinates in the image's coordinate space, and label is a short 1-3 word description of the element (like "search bar" or "save button"). if pointing wouldn't help, append [POINT:none].

        teaching — your heart:
        you're a teacher before anything else. people learn by DOING, not by watching lectures. when someone asks you to teach them, explain a concept, or walk them through software (photoshop, excel, davinci resolve, anything):
        - teach in small steps: give ONE clear step, point at or box exactly where it happens on screen, then tell them to do it and talk to you again when they're ready. next time you see their screen, continue from what actually changed.
        - ground everything in what's on their screen right now. a real example on their screen beats abstract theory every time.
        - for concepts (economics, maths, school subjects): explain simply, and use everyday south african examples when they make things click — rands, taxi fare, a spaza shop's stock, a monthly budget. draw a quick diagram if it helps.
        - celebrate progress in a few words ("nice, that's it") and correct gently when they click the wrong thing — never make them feel dumb.
        - BUT: quick questions stay quick. "what button do i press" or "where is x" gets a short answer and a point — don't turn it into a lesson unless they asked to learn.

        drawing on screen:
        beyond pointing, you can draw temporary annotations over the user's screen. use them whenever a question benefits from visual explanation — walking someone through a UI, comparing regions, analysing a chart or financial market, or sketching a quick diagram. all drawing coordinates use the SAME image pixel space as POINT (the labeled dimensions, origin top-left).

        drawing tags — append after your spoken text, in the order they should be drawn:
        - [BOX:x,y,width,height:label] glowing highlight box around a region. label is 1-3 words, or leave empty.
        - [ARROW:x1,y1,x2,y2:label] arrow drawn from (x1,y1) to (x2,y2), arrowhead at the end point.
        - [LINE:x1,y1,x2,y2] plain line — trendlines on charts, connections, diagram edges.
        - [NOTE:x,y:text] small text chip anchored at a position. keep it under 6 words.

        drawing guidance:
        - use BOX to highlight the area you're talking about while you explain it — much clearer than pointing alone for anything bigger than a single button.
        - for charts and data (like financial markets): trace the trend with LINE, mark zones of interest with BOX, and call out key points with ARROW plus a NOTE.
        - for step-by-step walkthroughs: put a numbered NOTE ("1. open this") next to each BOX so the user can follow the sequence.
        - if a diagram would genuinely help explain a concept, sketch a simple one in an EMPTY area of the screen using LINE, ARROW and NOTE.
        - keep it clean: at most six annotations per response. they draw in the order you write them and fade away on their own.
        - annotations and POINT can be combined. use POINT alone when a single small element is the whole answer; use BOX and friends when you're explaining or demonstrating something bigger.

        examples:
        - user asks how to export a pdf: "see the export button up in the toolbar? click that and pick pdf from the list. [POINT:1100,42:export button]"
        - user asks what html is: "html stands for hypertext markup language, it's basically the skeleton of every web page. curious how it connects to the css you're looking at? [POINT:none]"
        - user asks about a stock chart: "this rally started here and it's been climbing steadily — see the trend? the volume spike right there is where the momentum kicked in. [LINE:220,610,780,340] [BOX:640,520,160,120:volume spike] [ARROW:700,300,745,362:momentum] [POINT:none]"
        - user asks how a login flow works: "roughly, the browser sends your details to the server, the server checks them against the database, and a session token comes back. [NOTE:900,200:1. browser] [ARROW:960,230,1080,230:] [NOTE:1100,200:2. server] [ARROW:1160,230,1280,230:] [NOTE:1300,200:3. database] [POINT:none]"
        """;

    private readonly AppSettings _settings;
    private readonly ConversationHistory _history;
    private readonly FileService _files;

    public ClaudeService(AppSettings settings, ConversationHistory history, FileService files)
    {
        _settings = settings;
        _history = history;
        _files = files;

        // Pre-configure headers
        Http.DefaultRequestHeaders.Remove("x-api-key");
        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
            Http.DefaultRequestHeaders.Add("x-api-key", settings.AnthropicApiKey);
        Http.DefaultRequestHeaders.Remove("anthropic-version");
        Http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    // ── Conversational response: STREAMING, with sentence callbacks + tool loop ──

    /// <summary>
    /// Sends transcript + screenshots to Claude, streaming the reply. Complete
    /// sentences are handed to <paramref name="onSentence"/> the moment they arrive,
    /// so speech starts long before the full response (and its drawing tags) has
    /// finished generating. File tools still work: if the stream ends in tool_use,
    /// the tools run and the loop continues. Returns the full text, tags included.
    /// </summary>
    public async Task<string> GetResponseAsync(
        string transcript,
        List<ScreenshotResult> screenshots,
        Action<string>? onSentence = null,
        CancellationToken ct = default)
    {
        // Build the current user message: images first, then the transcript.
        var contentArray = new JsonArray();
        foreach (var shot in screenshots)
        {
            contentArray.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"] = shot.Base64,
                },
            });
            contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = shot.Label });
        }
        contentArray.Add(new JsonObject { ["type"] = "text", ["text"] = transcript });

        var messages = new JsonArray();
        foreach (var turn in _history.Turns)
            messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = contentArray });

        bool toolsOn = _files.Enabled && _files.Roots.Count > 0;
        var fullText = new StringBuilder();      // everything, tags included
        var spokenBuffer = new StringBuilder();  // text awaiting sentence emission

        // Tool-use loop — bounded so a misbehaving model can't spin forever.
        for (int iteration = 0; iteration < 6; iteration++)
        {
            var body = new JsonObject
            {
                ["model"] = _settings.ClaudeModel,
                ["max_tokens"] = 1024,
                ["stream"] = true,
                ["system"] = BuildSystemPrompt(),
                ["messages"] = messages,
            };
            if (toolsOn) body["tools"] = BuildTools();

            var request = new HttpRequestMessage(HttpMethod.Post, _settings.ClaudeApiUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                throw new Exception($"Claude API error {(int)response.StatusCode}: {err}");
            }

            // Collect content blocks by index as the SSE stream arrives.
            var blocks = new SortedDictionary<int, StreamedBlock>();
            string stopReason = "";

            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            using (var reader = new System.IO.StreamReader(stream))
            {
                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line == null) break;
                    if (!line.StartsWith("data: ")) continue;

                    JsonNode? node;
                    try { node = JsonNode.Parse(line["data: ".Length..]); }
                    catch { continue; }

                    switch (node?["type"]?.GetValue<string>())
                    {
                        case "content_block_start":
                        {
                            int idx = node["index"]!.GetValue<int>();
                            var cb = node["content_block"]!;
                            blocks[idx] = new StreamedBlock(
                                cb["type"]?.GetValue<string>() ?? "text",
                                cb["id"]?.GetValue<string>() ?? "",
                                cb["name"]?.GetValue<string>() ?? "");
                            break;
                        }
                        case "content_block_delta":
                        {
                            int idx = node["index"]!.GetValue<int>();
                            if (!blocks.TryGetValue(idx, out var blk)) break;
                            var delta = node["delta"];
                            var dType = delta?["type"]?.GetValue<string>();
                            if (dType == "text_delta")
                            {
                                var chunk = delta?["text"]?.GetValue<string>() ?? "";
                                blk.Content.Append(chunk);
                                fullText.Append(chunk);
                                spokenBuffer.Append(chunk);
                                EmitCompleteSentences(spokenBuffer, onSentence);
                            }
                            else if (dType == "input_json_delta")
                            {
                                blk.Content.Append(delta?["partial_json"]?.GetValue<string>() ?? "");
                            }
                            break;
                        }
                        case "message_delta":
                            stopReason = node?["delta"]?["stop_reason"]?.GetValue<string>() ?? stopReason;
                            break;
                    }
                }
            }

            if (stopReason == "tool_use")
            {
                // Rebuild the assistant turn (text + tool_use blocks) and run the tools.
                var assistantContent = new JsonArray();
                var toolResults = new JsonArray();
                foreach (var kv in blocks)
                {
                    var blk = kv.Value;
                    if (blk.Type == "text")
                    {
                        if (blk.Content.Length > 0)
                            assistantContent.Add(new JsonObject
                            { ["type"] = "text", ["text"] = blk.Content.ToString() });
                    }
                    else if (blk.Type == "tool_use")
                    {
                        JsonNode input;
                        try
                        {
                            input = JsonNode.Parse(
                                blk.Content.Length > 0 ? blk.Content.ToString() : "{}") ?? new JsonObject();
                        }
                        catch { input = new JsonObject(); }

                        assistantContent.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = blk.Id,
                            ["name"] = blk.Name,
                            ["input"] = input,
                        });
                        string result = ExecuteTool(blk.Name, input as JsonObject);
                        toolResults.Add(new JsonObject
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = blk.Id,
                            ["content"] = result,
                        });
                    }
                }
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = assistantContent });
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
                fullText.Append(' ');
                spokenBuffer.Append(' ');
                continue; // stream the follow-up turn now that the tools have run
            }

            break;
        }

        // Flush any remaining speech text (tags sit at the end — stripped here).
        var residual = StripPointTags(spokenBuffer.ToString());
        if (!string.IsNullOrWhiteSpace(residual))
            onSentence?.Invoke(residual);

        var finalText = fullText.ToString();

        // History must never contain an empty assistant message — the API rejects
        // empty content on the next request, which would poison every later turn.
        var clean = StripPointTags(finalText);
        _history.AddUserMessage(transcript);
        _history.AddAssistantMessage(string.IsNullOrWhiteSpace(clean)
            ? "(pointed at the requested element on screen)"
            : clean);
        return finalText;
    }

    /// <summary>One content block being assembled from SSE deltas.</summary>
    private sealed class StreamedBlock(string type, string id, string name)
    {
        public string Type { get; } = type;
        public string Id { get; } = id;
        public string Name { get; } = name;
        public StringBuilder Content { get; } = new();
    }

    /// <summary>
    /// Emits all complete sentences from the buffer to the speech callback, in one
    /// batch (fewer TTS calls). Holds back anything from the first '[' onward so
    /// drawing/point tags are never spoken, and never splits mid-number ("3.5").
    /// </summary>
    private static void EmitCompleteSentences(StringBuilder buffer, Action<string>? onSentence)
    {
        if (onSentence == null || buffer.Length == 0) return;

        var text = buffer.ToString();
        int bracket = text.IndexOf('[');
        int searchEnd = bracket >= 0 ? bracket : text.Length;

        // Last sentence terminator followed by whitespace within the safe range.
        int cut = -1;
        for (int i = 0; i < searchEnd - 1; i++)
        {
            char c = text[i];
            if ((c == '.' || c == '!' || c == '?') && char.IsWhiteSpace(text[i + 1]))
                cut = i + 1;
        }
        if (cut <= 0) return;

        var sentence = text[..cut].Trim();
        buffer.Remove(0, cut);
        if (sentence.Length > 0)
            onSentence(sentence);
    }

    private string BuildSystemPrompt()
    {
        if (!(_files.Enabled && _files.Roots.Count > 0))
            return SystemPrompt;

        var roots = string.Join("\n", _files.Roots.Select(r => "  • " + r));
        return SystemPrompt + "\n\n" + $"""
            You can also help the user find and organize files, but ONLY inside these allowed folders:
            {roots}
            Tools:
            - list_files: list or find files in an allowed folder (read-only, always safe). Use it to
              locate a file before copying, or to answer questions about what files exist.
            - copy_file: STAGE a copy. It does not copy immediately. After calling it, tell the user
              exactly which file will be copied and to where, then ask them to say "yes" to confirm.
            - confirm_last_action: when the user replies to a pending copy, call this with confirmed=true
              if they agreed (yes/confirm/go ahead) or confirmed=false if they declined. If a copy is
              already pending, do NOT call copy_file again — call confirm_last_action.
            Never say a file was copied unless confirm_last_action actually succeeded. Stay strictly
            inside the allowed folders; refuse anything outside them.
            """;
    }

    private static JsonArray BuildTools() =>
    [
        new JsonObject
        {
            ["name"] = "list_files",
            ["description"] = "List files and subfolders inside an allowed folder. Read-only and always safe. Use to find files before copying or to report what exists.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["folder"] = new JsonObject { ["type"] = "string", ["description"] = "Folder to list, relative to an allowed folder. Use '.' for the allowed root." },
                    ["pattern"] = new JsonObject { ["type"] = "string", ["description"] = "Optional glob filter like '*.pdf'. Omit for everything." },
                },
                ["required"] = new JsonArray { "folder" },
            },
        },
        new JsonObject
        {
            ["name"] = "copy_file",
            ["description"] = "Stage a file copy for the user to confirm. Does NOT copy immediately — returns a confirmation prompt. After calling, tell the user what will be copied and ask them to confirm.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["source"] = new JsonObject { ["type"] = "string", ["description"] = "File to copy, relative to an allowed folder." },
                    ["destination"] = new JsonObject { ["type"] = "string", ["description"] = "Destination folder (or full target path), relative to an allowed folder." },
                },
                ["required"] = new JsonArray { "source", "destination" },
            },
        },
        new JsonObject
        {
            ["name"] = "confirm_last_action",
            ["description"] = "Execute or cancel the file copy awaiting confirmation. Call when the user responds to a pending copy.",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["confirmed"] = new JsonObject { ["type"] = "boolean", ["description"] = "true if the user agreed, false if they declined." },
                },
                ["required"] = new JsonArray { "confirmed" },
            },
        },
    ];

    private string ExecuteTool(string name, JsonObject? input)
    {
        try
        {
            return name switch
            {
                "list_files" => _files.ListFiles(
                    input?["folder"]?.GetValue<string>() ?? ".",
                    input?["pattern"]?.GetValue<string>()),
                "copy_file" => _files.StageCopy(
                    input?["source"]?.GetValue<string>() ?? "",
                    input?["destination"]?.GetValue<string>() ?? ""),
                "confirm_last_action" => _files.ConfirmPending(
                    input?["confirmed"]?.GetValue<bool>() ?? false),
                _ => $"Unknown tool: {name}",
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"[Files] Tool '{name}' error: {ex.Message}");
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Parses [POINT:x,y:label] tags from a complete response string.
    /// </summary>
    public static List<PointTarget> ParsePoints(string responseText)
    {
        var points = new List<PointTarget>();
        foreach (Match m in PointTagRegex().Matches(responseText))
        {
            if (int.TryParse(m.Groups[1].Value, out int x) &&
                int.TryParse(m.Groups[2].Value, out int y))
            {
                points.Add(new PointTarget(x, y, m.Groups[3].Value));
            }
        }
        return points;
    }

    /// <summary>
    /// Strips [POINT:...] tags from text before sending to TTS.
    /// </summary>
    public static string StripPointTags(string text)
    {
        text = PointTagRegex().Replace(text, "");
        text = PointNoneRegex().Replace(text, "");
        text = BoxTagRegex().Replace(text, "");
        text = ArrowTagRegex().Replace(text, "");
        text = LineTagRegex().Replace(text, "");
        text = NoteTagRegex().Replace(text, "");
        return text.Trim();
    }

    /// <summary>
    /// Parses all drawing tags ([BOX]/[ARROW]/[LINE]/[NOTE]) from the response,
    /// in the order they appear (= the order Bru "draws" them). Coordinates are in
    /// the primary screenshot's image pixel space — the caller scales to physical.
    /// </summary>
    public static List<ScreenAnnotation> ParseAnnotations(string responseText)
    {
        var found = new List<(int Index, ScreenAnnotation Ann)>();

        foreach (Match m in BoxTagRegex().Matches(responseText))
            found.Add((m.Index, new ScreenAnnotation(AnnotationKind.Box,
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value),
                m.Groups[5].Value.Trim())));

        foreach (Match m in ArrowTagRegex().Matches(responseText))
            found.Add((m.Index, new ScreenAnnotation(AnnotationKind.Arrow,
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value),
                m.Groups[5].Value.Trim())));

        foreach (Match m in LineTagRegex().Matches(responseText))
            found.Add((m.Index, new ScreenAnnotation(AnnotationKind.Line,
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), "")));

        foreach (Match m in NoteTagRegex().Matches(responseText))
            found.Add((m.Index, new ScreenAnnotation(AnnotationKind.Note,
                int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                0, 0, m.Groups[3].Value.Trim())));

        return found.OrderBy(f => f.Index).Select(f => f.Ann).ToList();
    }

    /// <summary>
    /// Uses Claude's Computer Use API to precisely locate a named UI element on screen.
    /// Sends a resized screenshot (at CU resolution) and gets back physical pixel coordinates.
    /// Returns (-1, -1) if the element cannot be found.
    /// </summary>
    public async Task<(int physX, int physY)> DetectElementAsync(
        string base64Jpeg,
        string elementDescription,
        int screenWidth, int screenHeight,
        CancellationToken ct = default)
    {
        var (cuW, cuH) = CoordinateHelper.DetectComputerUseResolution(screenWidth, screenHeight);

        var body = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(_settings.PointingModel)
                ? _settings.ClaudeModel : _settings.PointingModel,
            ["max_tokens"] = 256,
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "computer_20251124",
                    ["name"] = "computer",
                    ["display_width_px"] = cuW,
                    ["display_height_px"] = cuH,
                },
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = base64Jpeg,
                            },
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"Move the mouse to the exact centre of the '{elementDescription}' element on this screen. Be precise — aim for the middle of the element, not its edge or nearby text.",
                        },
                    },
                },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _settings.ClaudeApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("anthropic-beta", "computer-use-2025-11-24");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            throw new Exception($"Computer Use API request failed: {ex.Message}", ex);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Computer Use API error {(int)response.StatusCode}: {json}");

        var node = JsonNode.Parse(json);
        var content = node?["content"]?.AsArray();
        if (content != null)
        {
            foreach (var block in content)
            {
                if (block?["type"]?.GetValue<string>() != "tool_use" ||
                    block?["name"]?.GetValue<string>() != "computer")
                    continue;

                var input = block?["input"];
                int cuX = -1, cuY = -1;

                var coord = input?["coordinate"]?.AsArray();
                if (coord?.Count >= 2)
                {
                    cuX = coord[0]!.GetValue<int>();
                    cuY = coord[1]!.GetValue<int>();
                }
                else if (input?["x"] != null && input?["y"] != null)
                {
                    // Some responses use {x, y} instead of coordinate:[x,y]
                    cuX = input["x"]!.GetValue<int>();
                    cuY = input["y"]!.GetValue<int>();
                }

                if (cuX >= 0)
                {
                    // Scale from CU space to physical pixels
                    int physX = (int)((double)cuX / cuW * screenWidth);
                    int physY = (int)((double)cuY / cuH * screenHeight);
                    return (physX, physY);
                }
            }
        }

        // Diagnosable failure: log a snippet of what actually came back
        var snippet = json.Length > 600 ? json[..600] : json;
        Logger.Log($"[CU] No usable coordinate. Response snippet: {snippet}");
        return (-1, -1);
    }
}
