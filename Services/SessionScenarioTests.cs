using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic.Models.Beta.Sessions;
using Microsoft.Extensions.FileProviders;

namespace LessonC13.Services;

internal static class SessionScenarioTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        // All writes are isolated in a unique temporary directory; no account access.
        var root = Path.Combine(Path.GetTempPath(), "lesson-c13-session-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var environment = new TestEnvironment(root);
            var gateway = new FixtureGateway();
            using var gate = new LabExecutionGate();
            var service = new SessionScenarioService(gateway, environment, gate);
            var inventory = await service.RunAsync(new("inventory"));
            check(inventory.Source == "live-session-scenario" && inventory.Evidence.GetProperty("sessions").GetArrayLength() == 1, "live inventory is scoped to configured resources");
            check(inventory.Evidence.GetProperty("nextPage").GetString() == "next", "live inventory pagination cursor");
            await service.RunAsync(new("inventory", Page: "next"));
            check(gateway.LastCursor == "next", "live inventory passes opaque page cursor");
            var inspected = await service.RunAsync(new("inspect", "sesn_parent"));
            check(inspected.Evidence.GetProperty("events").GetArrayLength() == 3, "live history keeps provider event IDs and deduplicates replay");
            check(inspected.Evidence.GetProperty("mirroredNewEvents").GetInt32() == 3, "mirror durably records canonical events");
            var restarted = new SessionScenarioService(gateway, environment, gate);
            check((await restarted.RunAsync(new("inspect", "sesn_parent"))).Evidence.GetProperty("mirroredNewEvents").GetInt32() == 0, "mirror replay stays deduplicated after service restart");
            var missing = await service.RunAsync(new("reject-missing", "sesn_parent", "sevt_missing"));
            check(missing.Outcome == "rejected" && gateway.Created.Count == 0, "missing provider boundary creates no child");
            check((await service.RunAsync(new("branch", "sesn_parent", "sevt_missing", "reviewed", "hash", true))).Outcome == "rejected" && gateway.Created.Count == 0, "branch path rejects missing boundary before create");
            check((await service.RunAsync(new("reject-missing", "sesn_parent", "sevt_2"))).Outcome == "conflict", "negative scenario detects an existing boundary");
            var preview = await service.RunAsync(new("preview", "sesn_parent", "sevt_2"));
            var context = preview.Evidence.GetProperty("reviewedContext").GetString()!;
            var hash = preview.Evidence.GetProperty("historyHash").GetString()!;
            check(context == "Project Aurora\n\nApproved scope" && preview.Evidence.GetProperty("selectedEvents").GetInt32() == 2, "real branch preview includes boundary and excludes later proposal");
            var unaccepted = false;
            try { await service.RunAsync(new("branch", "sesn_parent", "sevt_2", context, hash)); } catch (ArgumentException) { unaccepted = true; }
            check(unaccepted && gateway.Created.Count == 0, "branch requires explicit reviewed-context acceptance");
            check((await service.RunAsync(new("branch", "sesn_parent", "sevt_2", context, "stale", true))).Outcome == "conflict" && gateway.Created.Count == 0, "stale preview creates no child");
            var branch = await service.RunAsync(new("branch", "sesn_parent", "sevt_2", "My reviewed summary", hash, true));
            var body = JsonSerializer.SerializeToElement(gateway.Created.Single().RawBodyData);
            gateway.Created[0].Agent.Validate();
            check(branch.Outcome == "passed" && branch.Evidence.GetProperty("childSessionId").GetString() == "sesn_child_1", "branch returns real gateway child ID and observation");
            check(body.GetProperty("agent").GetProperty("version").GetInt32() == 7, "branch pins the canonical parent's agent version");
            check(body.GetProperty("metadata").GetProperty("parent_session").GetString() == "sesn_parent" && body.GetProperty("metadata").GetProperty("boundary_event").GetString() == "sevt_2", "child records parent and boundary lineage");
            check(body.GetProperty("initial_events")[0].GetProperty("content")[0].GetProperty("text").GetString() == "My reviewed summary", "child receives only reviewed context");
            check(body.GetProperty("agent").GetProperty("tools").GetArrayLength() == 0 && body.GetProperty("budget").GetProperty("max_list_cost").GetProperty("amount").GetString() == "1000", "live child has empty tools and budget in cents");
            check(gateway.Interrupted.Count == 0 && branch.Evidence.GetProperty("parentWriteRequests").GetInt32() == 0, "successful branch does not write to parent");
            var snapshot = await service.RunAsync(new("capture-snapshot", "sesn_parent"));
            var snapshotId = snapshot.Evidence.GetProperty("snapshotId").GetString()!;
            var same = await service.RunAsync(new("compare-snapshot", "sesn_parent", SnapshotId: snapshotId));
            check(!same.Evidence.GetProperty("changed").GetBoolean(), "snapshot compares saved and current canonical bytes");
            gateway.History.Add(Event("sevt_4", "New event"));
            var changed = await service.RunAsync(new("compare-snapshot", "sesn_parent", SnapshotId: snapshotId));
            check(changed.Evidence.GetProperty("changed").GetBoolean() && !changed.Evidence.GetProperty("restoreExecuted").GetBoolean(), "snapshot detects appended real history without restoring files");
            var invalidPath = false;
            try { await service.RunAsync(new("compare-snapshot", "sesn_parent", SnapshotId: "../outside")); } catch (ArgumentException) { invalidPath = true; }
            check(invalidPath, "snapshot ID cannot select arbitrary filesystem paths");
            gateway.WrongAgent = true;
            var wrongParent = false;
            try { await service.RunAsync(new("preview", "sesn_other", "sevt_1")); } catch (ArgumentException) { wrongParent = true; }
            check(wrongParent, "session inspection requires configured agent and environment");
            gateway.WrongAgent = false;
            gateway.FailObserve = true;
            var failed = await service.RunAsync(new("branch", "sesn_parent", "sevt_2", context, hash, true));
            check(failed.Outcome == "unconfirmed" && failed.Evidence.GetProperty("createdSessionId").GetString() == "sesn_child_2", "failed observation preserves known child ID");
            check(gateway.Interrupted.SequenceEqual(["sesn_child_2"]) && !failed.Evidence.GetProperty("stopConfirmed").GetBoolean(), "failed child observation attempts interrupt and preserves stop uncertainty");
            gateway.FailObserve = false; gateway.FailCreate = true;
            var uncertain = await service.RunAsync(new("create-parent"));
            check(uncertain.Outcome == "unconfirmed" && uncertain.Evidence.GetProperty("createdSessionId").ValueKind == JsonValueKind.Null, "creation failure preserves unknown resource uncertainty");
            gateway.FailCreate = false;
            var parent = await service.RunAsync(new("create-parent"));
            var seed = JsonSerializer.SerializeToElement(gateway.Created.Last().RawBodyData);
            check(parent.Outcome == "passed" && seed.GetProperty("initial_events").GetArrayLength() == 3, "teaching parent queues three real seed messages");
            check(!seed.GetProperty("agent").TryGetProperty("version", out _), "unversioned teaching parent omits version instead of sending null");
            gateway.FailHistory = true;
            check((await service.RunAsync(new("reject-missing", "sesn_parent", "missing"))).Outcome == "incomplete", "partial provider history never proves boundary absence");
            gateway.FailHistory = false;
            var acquired = await gate.Semaphore.WaitAsync(0);
            var busy = false;
            try { await service.RunAsync(new("inventory")); } catch (ExperimentBusyException) { busy = true; }
            finally { if (acquired) gate.Semaphore.Release(); }
            check(busy, "live operations share the application's concurrency gate");
            var orderedA = JsonSerializer.Deserialize<JsonElement>("{\"id\":\"x\",\"type\":\"user.message\"}");
            var orderedB = JsonSerializer.Deserialize<JsonElement>("{\"type\":\"user.message\",\"id\":\"x\"}");
            check(SessionScenarioService.HistoryHash([orderedA]) == SessionScenarioService.HistoryHash([orderedB]), "snapshot hashes use stable property ordering");
            gateway.History.Add(Event("sevt_2", "Conflicting replay"));
            check((await service.RunAsync(new("inspect", "sesn_parent"))).Outcome == "incomplete", "conflicting event replay does not silently replace mirror content");
            await SessionSdkTests.RunAsync(environment, check);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private static JsonElement Event(string id, string text) => JsonSerializer.SerializeToElement(new
    { id, type = "user.message", processed_at = "2026-10-28T12:00:00Z", content = new[] { new { type = "text", text } } });

    private sealed class FixtureGateway : ISessionGateway
    {
        public bool Configured => true;
        public string AgentId => "agent_test";
        public string EnvironmentId => "env_test";
        public List<SessionCreateParams> Created { get; } = [];
        public List<string> Interrupted { get; } = [];
        public List<JsonElement> History { get; } = [Event("sevt_1", "Project Aurora"), Event("sevt_2", "Approved scope"), Event("sevt_3", "Later proposal")];
        public bool WrongAgent, FailObserve, FailCreate, FailHistory;
        public string? LastCursor;
        public Task<SessionInventory> ListAsync(string? page, CancellationToken token)
        { LastCursor = page; return Task.FromResult(new SessionInventory([Session("sesn_parent"), Session("sesn_unrelated", "agent_other")], page is null ? "next" : null)); }
        private JsonElement Session(string id, string agent = "agent_test") => JsonSerializer.SerializeToElement(new
        { id, agent = new { id = agent, version = 7 }, environment_id = EnvironmentId, title = "Test parent", status = "idle" });
        public Task<JsonElement> RetrieveAsync(string id, CancellationToken token) => Task.FromResult(Session(id, WrongAgent ? "agent_other" : AgentId));
        public async IAsyncEnumerable<JsonElement> EventsAsync(string id, [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            foreach (var item in History) { token.ThrowIfCancellationRequested(); yield return item; }
            if (FailHistory) throw new HttpRequestException("Fixture page two failed");
            yield return History[0]; // Simulate duplicate delivery across pages.
        }
        public Task<string> CreateAsync(SessionCreateParams parameters, CancellationToken token)
        { if (FailCreate) throw new HttpRequestException("Fixture creation uncertain"); Created.Add(parameters); return Task.FromResult($"sesn_child_{Created.Count}"); }
        public Task<SessionObservation> ObserveAsync(string id, CancellationToken token)
        { if (FailObserve) throw new OperationCanceledException("Fixture deadline"); return Task.FromResult(new SessionObservation("Acknowledged.", "end_turn", ["agent.message", "session.status_idle"], [])); }
        public Task InterruptAsync(string id, CancellationToken token) { Interrupted.Add(id); return Task.CompletedTask; }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "LessonC13";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
