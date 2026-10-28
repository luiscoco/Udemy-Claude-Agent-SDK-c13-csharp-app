using System.Text.Json;

namespace LessonC13.Services;

public sealed class ExperimentBusyException() : Exception("An experiment is already running. Wait for it to finish.");

public sealed class LabExecutionGate : IDisposable
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    public void Dispose() => Semaphore.Dispose();
}

public sealed record RecordedExperiment(Guid Id, string Source, string Outcome, string Summary, object Evidence, string[] Steps)
{
    public ExperimentResult ToExperiment() => new(Outcome, Summary, Evidence, Steps);
}

public static class LabJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
    public static string Format(object value) => JsonSerializer.Serialize(value, Pretty);
}

public sealed class LabService(IWebHostEnvironment environment, ManagedAgentsService provider, LabExecutionGate execution)
{
    private SemaphoreSlim gate => execution.Semaphore;
    public async Task<object> RunAsync(ExperimentInput input, bool live, CancellationToken token = default)
    {
        if (!await gate.WaitAsync(0, token)) throw new ExperimentBusyException();
        try
        {
            var result = LessonEngine.Run(input);
            if (live) return await provider.ExplainAsync(result, token);
            var id = Guid.NewGuid();
            var directory = Path.Combine(environment.ContentRootPath, ".lab-data");
            Directory.CreateDirectory(directory);
            await File.AppendAllTextAsync(Path.Combine(directory, "runs.jsonl"),
                JsonSerializer.Serialize(new { id, at = DateTimeOffset.UtcNow, lesson = 13, result }, LabJson.Options) + "\n", token);
            return new RecordedExperiment(id, result.Source, result.Outcome, result.Summary, result.Evidence, result.Steps);
        }
        finally { gate.Release(); }
    }
}
