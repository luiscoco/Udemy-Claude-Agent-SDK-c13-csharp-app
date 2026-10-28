using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LessonC13.Services;

public sealed record ExperimentInput(string Boundary = "", string CurrentText = "", string SavedText = "");
public sealed record Scenario(string Id, string Label, ExperimentInput Input, string Expected, object Checks);
public sealed record ExperimentResult(string Outcome, string Summary, object Evidence, string[] Steps)
{
    public string Source => "local-experiment";
}

public static class LessonEngine
{
    public const string Title = "Inventory, Branches, Snapshots and Mirrors";
    public const string Description = "A long project needs more than a list of chat messages. We need to find sessions, try an alternative direction, recover selected files, and inspect past events.";
    public const string Scope = "Creates a branch and snapshot plan over local seed records. It never overwrites project files or creates provider child sessions. Each run starts from a fresh local seed. The run journal stores results, not production workflow state.";
    public static readonly string[] Concepts = ["Branch at an explicit event boundary", "Reviewed context is transferred explicitly", "Snapshots compare actual bytes"];
    public static readonly Scenario[] Cases = [
        new("0", "Branch at a valid boundary", new("e2", "new draft", "reviewed draft"), "passed", new { selectedEvents = 2 }),
        new("1", "Reject a missing boundary", new("missing", "new", "old"), "rejected", new { })
    ];

    // Preserve the original API's text validation, including missing/null defaults.
    public static ExperimentInput Parse(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object");
        return new(Read("boundary"), Read("currentText"), Read("savedText"));
        string Read(string key)
        {
            if (!input.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return "";
            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 20_000)
                throw new ArgumentException($"{key} must be bounded text");
            return value.GetString()!;
        }
    }

    public static ExperimentResult Run(ExperimentInput input)
    {
        Validate(input.Boundary, "boundary");
        Validate(input.CurrentText, "currentText");
        Validate(input.SavedText, "savedText");
        (string Id, string Text)[] history = [("e1", "Project Aurora"), ("e2", "Approved scope"), ("e3", "Later proposal")];
        var at = Array.FindIndex(history, e => e.Id == input.Boundary);
        if (at < 0) return new("rejected", "Boundary not found; no branch planned", new { },
            ["Read canonical parent history", "Find the explicit boundary"]);

        var selected = history.Take(at + 1).ToArray();
        return new("passed", "Prepared reviewed context and a hash-bound restore plan", new
        {
            selectedEvents = selected.Length,
            branchContext = string.Join("\n", selected.Select(e => e.Text)),
            snapshot = new
            {
                savedHash = Hash(input.SavedText), currentHash = Hash(input.CurrentText),
                needsRestore = input.SavedText != input.CurrentText
            },
            parentMutated = false
        }, ["Stop history at the selected event", "Review the transferred summary",
            "Compare bytes before planning restoration", "Preserve the parent conversation"]);
    }

    private static void Validate(string? value, string key)
    {
        if (value is null || value.Length > 20_000) throw new ArgumentException($"{key} must be bounded text");
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Source
    {
        get
        {
            using var stream = typeof(LessonEngine).Assembly.GetManifestResourceStream("LessonC13.Services.LessonEngine.cs")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}
