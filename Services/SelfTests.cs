using System.Text.Json;
using Anthropic.Models.Beta.Agents;
using Anthropic.Models.Beta.Environments;
using Anthropic.Models.Beta.Sessions.Events;

namespace LessonC13.Services;

internal static class SelfTests
{
    public static async Task RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException($"FAIL: {name}");
            Console.WriteLine($"PASS: {name}"); count++;
        }
        JsonElement Evidence(ExperimentInput input) => JsonSerializer.SerializeToElement(LessonEngine.Run(input).Evidence);
        foreach (var scenario in LessonEngine.Cases)
            Check(LessonEngine.Run(scenario.Input).Outcome == scenario.Expected, scenario.Label);
        var evidence = Evidence(LessonEngine.Cases[0].Input);
        Check(evidence.GetProperty("selectedEvents").GetInt32() == 2, "e2 includes exactly two events");
        Check(evidence.GetProperty("branchContext").GetString() == "Project Aurora\nApproved scope", "reviewed context excludes later proposal");
        Check(!evidence.GetProperty("parentMutated").GetBoolean(), "parent remains unchanged");
        Check(evidence.GetProperty("snapshot").GetProperty("needsRestore").GetBoolean(), "different text requires restore plan");
        Check(Evidence(new("e1")).GetProperty("selectedEvents").GetInt32() == 1, "first boundary");
        Check(Evidence(new("e3")).GetProperty("selectedEvents").GetInt32() == 3, "last boundary");
        Check(!Evidence(new("e2", "same", "same")).GetProperty("snapshot").GetProperty("needsRestore").GetBoolean(), "identical snapshots");
        Check(LessonEngine.Hash("") == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "SHA-256 empty vector");
        Check(LessonEngine.Hash("abc") == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", "SHA-256 known vector");
        Check(LessonEngine.Hash("café") == "850f7dc43910ff890f8879c0ed26fe697c93a067ad93a7d50f466a7028a9bf4e", "UTF-8 hash vector");
        Check(LessonEngine.Hash("a\nb") != LessonEngine.Hash("a\r\nb"), "line endings remain distinct");
        Check(LabJson.Format(LessonEngine.Run(LessonEngine.Cases[0].Input)) == LabJson.Format(LessonEngine.Run(LessonEngine.Cases[0].Input)), "fresh seed is deterministic");
        foreach (var invalid in new[] { "[]", "null", "{\"boundary\":12}", "{\"savedText\":false}" })
        {
            var rejected = false;
            try { LessonEngine.Parse(JsonSerializer.Deserialize<JsonElement>(invalid)); } catch (ArgumentException) { rejected = true; }
            Check(rejected, $"reject invalid input {invalid}");
        }
        var tooLong = false;
        try { LessonEngine.Run(new("e2", new string('x', 20_001))); } catch (ArgumentException) { tooLong = true; }
        Check(tooLong, "reject oversized text");
        Check(LessonEngine.Parse(JsonSerializer.Deserialize<JsonElement>("{\"boundary\":null}")) == new ExperimentInput(), "missing and null default to empty text");
        var parameters = ManagedAgentsService.CommentaryParameters("agent_test", "env_test", LessonEngine.Run(LessonEngine.Cases[0].Input));
        parameters.Agent.Validate();
        var request = JsonSerializer.SerializeToElement(parameters.RawBodyData);
        Check(request.GetProperty("environment_id").GetString() == "env_test", "SDK session environment serialization");
        Check(request.GetProperty("agent").GetProperty("tools").GetArrayLength() == 0, "SDK commentary has no tools");
        Check(request.GetProperty("initial_events")[0].GetProperty("type").GetString() == "user.message", "SDK initial prompt serialization");
        Check(request.GetProperty("budget").GetProperty("max_list_cost").GetProperty("amount").GetString() == "1000", "SDK session budget uses cents ($10)");
        var interrupt = ManagedAgentsService.Parameters<EventSendParams>(new { events = new[] { new { type = "user.interrupt" } } });
        Check(JsonSerializer.SerializeToElement(interrupt.RawBodyData).GetProperty("events")[0].GetProperty("type").GetString() == "user.interrupt", "SDK interrupt serialization");
        var env = ManagedAgentsService.Parameters<EnvironmentCreateParams>(new { name = "test", config = new { type = "cloud", networking = new { type = "limited" } } });
        env.Config?.Validate();
        Check(JsonSerializer.SerializeToElement(env.RawBodyData).GetProperty("config").GetProperty("type").GetString() == "cloud", "SDK environment setup serialization");
        var agent = ManagedAgentsService.Parameters<AgentCreateParams>(new { name = "test", model = "claude-opus-5-5", tools = Array.Empty<object>() });
        agent.Model.Validate();
        Check(JsonSerializer.SerializeToElement(agent.RawBodyData).GetProperty("tools").GetArrayLength() == 0, "SDK agent setup serialization");
        var observation = new LiveObservation();
        bool Observe(string json) => observation.Observe(JsonSerializer.Deserialize<JsonElement>(json));
        Check(!Observe("{\"type\":\"session.status_idle\",\"stop_reason\":{\"type\":\"end_turn\"}}"), "ignore initial idle before response");
        Observe("{\"type\":\"agent.message\",\"content\":[{\"type\":\"text\",\"text\":\"Reviewed context.\"}]}");
        Observe("{\"type\":\"span.model_request_end\",\"model_usage\":{\"input_tokens\":10}}");
        Check(Observe("{\"type\":\"session.status_idle\",\"stop_reason\":{\"type\":\"end_turn\"}}") && observation.Answer == "Reviewed context." && observation.Usage.Count == 1, "live answer, usage and terminal status");
        Check(Observe("{\"type\":\"session.error\"}") && observation.StopReason == "error", "provider errors remain visible");
        Check(LessonEngine.Source.Contains("public static ExperimentResult Run"), "published app can display embedded actual source");
        await SessionScenarioTests.RunAsync(Check);
        Console.WriteLine($"{count} checks passed. No real provider requests were made.");
    }
}
