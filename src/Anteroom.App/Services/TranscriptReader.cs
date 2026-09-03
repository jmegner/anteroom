using System.IO;
using System.Text;
using System.Text.Json;

namespace Anteroom.App.Services;

/// <summary>
/// Claude's hook payload carries no session title and only a terse notification string, so we mine
/// the session's JSONL transcript for the two things the tab needs: what this session is about,
/// and what Claude last said or asked. The file is live \u2014 always open it share-read-write.
/// </summary>
public static class TranscriptReader
{
    private const int HeadLines = 400;
    private const int TailLines = 250;

    /// <summary>First real user prompt of the session, trimmed for the tab subtitle.</summary>
    public static string? ReadFirstPrompt(string? transcriptPath)
    {
        foreach (var line in ReadHead(transcriptPath, HeadLines))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (Str(root, "type") != "user") continue;
                if (!root.TryGetProperty("message", out var message)) continue;

                var text = ExtractText(message);
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (text.StartsWith("<", StringComparison.Ordinal)) continue; // system reminders and tool plumbing
                return Shorten(text, 120);
            }
            catch
            {
                // Partial or non-JSON line: skip it.
            }
        }
        return null;
    }

    /// <summary>What Claude is currently blocked on: its pending tool call, or its closing message.</summary>
    public static string? ReadPendingDetail(string? transcriptPath)
    {
        var tail = ReadTail(transcriptPath, TailLines);
        for (int i = tail.Count - 1; i >= 0; i--)
        {
            try
            {
                using var doc = JsonDocument.Parse(tail[i]);
                var root = doc.RootElement;
                if (Str(root, "type") != "assistant") continue;
                if (!root.TryGetProperty("message", out var message)) continue;
                if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;

                string? pendingTool = null;
                var prose = new StringBuilder();

                foreach (var block in content.EnumerateArray())
                {
                    var kind = Str(block, "type");
                    if (kind == "tool_use")
                    {
                        var name = Str(block, "name") ?? "tool";
                        var arg = block.TryGetProperty("input", out var input) ? SummarizeInput(input) : null;
                        pendingTool = string.IsNullOrWhiteSpace(arg) ? name : $"{name}: {arg}";
                    }
                    else if (kind == "text")
                    {
                        var text = Str(block, "text");
                        if (!string.IsNullOrWhiteSpace(text)) prose.Append(text).Append(' ');
                    }
                }

                if (pendingTool is not null) return Shorten(pendingTool, 240);
                if (prose.Length > 0) return Shorten(prose.ToString(), 240);
            }
            catch
            {
                // Skip unparseable lines and keep walking backwards.
            }
        }
        return null;
    }

    private static string? SummarizeInput(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "command", "file_path", "path", "pattern", "url", "prompt", "description", "query" })
        {
            if (input.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }

    private static string? ExtractText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return null;

        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (Str(block, "type") == "text" && block.TryGetProperty("text", out var text))
                sb.Append(text.GetString()).Append(' ');
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    private static IEnumerable<string> ReadHead(string? path, int max)
    {
        var lines = new List<string>();
        if (!SafeExists(path)) return lines;
        try
        {
            using var reader = OpenShared(path!);
            for (int i = 0; i < max; i++)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                if (!string.IsNullOrWhiteSpace(line)) lines.Add(line);
            }
        }
        catch { /* transcript locked or gone */ }
        return lines;
    }

    private static List<string> ReadTail(string? path, int max)
    {
        var ring = new Queue<string>(max);
        if (!SafeExists(path)) return new List<string>();
        try
        {
            using var reader = OpenShared(path!);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (ring.Count == max) ring.Dequeue();
                ring.Enqueue(line);
            }
        }
        catch { /* transcript locked or gone */ }
        return ring.ToList();
    }

    private static StreamReader OpenShared(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);

    private static bool SafeExists(string? path)
    {
        try { return !string.IsNullOrWhiteSpace(path) && File.Exists(path); }
        catch { return false; }
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Shorten(string text, int max)
    {
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ");
        return text.Length <= max ? text : text[..max].TrimEnd() + "\u2026";
    }
}
