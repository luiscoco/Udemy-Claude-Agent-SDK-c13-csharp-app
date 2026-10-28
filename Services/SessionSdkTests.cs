using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;

namespace LessonC13.Services;

internal static class SessionSdkTests
{
    public static async Task RunAsync(IWebHostEnvironment environment, Action<bool, string> check)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["ANTHROPIC_API_KEY"] = "fixture-key", ["CLAUDE_MANAGED_AGENT_ID"] = "agent_test", ["CLAUDE_MANAGED_ENVIRONMENT_ID"] = "env_test" }).Build();
        var provider = new ManagedAgentsService(configuration, environment);
        using var handler = new FixtureHandler();
        var gateway = new AnthropicSessionGateway(provider)
        {
            // Handler supplies every response; it has no HTTP transport and cannot access the network.
            ClientFactory = () => new AnthropicClient { ApiKey = "fixture-key", MaxRetries = 0,
                HttpClient = new HttpClient(handler, disposeHandler: false) }
        };
        var inventory = await gateway.ListAsync(null, default);
        check(inventory.Sessions.Length == 1 && inventory.NextPage == "sessions-next" && handler.Requests.Last().Uri.Contains("agent_id=agent_test"), "SDK gateway lists sessions with agent filter and parses cursor");
        var parent = await gateway.RetrieveAsync("sesn_parent", default);
        check(parent.GetProperty("agent").GetProperty("version").GetInt32() == 7, "SDK gateway retrieves canonical session configuration");
        var events = new List<JsonElement>();
        await foreach (var item in gateway.EventsAsync("sesn_parent", default)) events.Add(item);
        check(events.Count == 2 && events[1].GetProperty("id").GetString() == "sevt_2" && handler.Requests.Any(r => r.Uri.Contains("page=events-next")), "SDK gateway follows canonical event pagination");
        var parameters = SessionScenarioService.CreateParameters("agent_test", "env_test", 7, ["Reviewed context"], new { parent_session = "sesn_parent", boundary_event = "sevt_2" });
        var created = await gateway.CreateAsync(parameters, default);
        var request = JsonSerializer.Deserialize<JsonElement>(handler.Requests.Last().Body);
        check(created == "sesn_child" && request.GetProperty("metadata").GetProperty("boundary_event").GetString() == "sevt_2", "SDK gateway sends branch body and parses created ID");
        var observed = await gateway.ObserveAsync(created, default);
        check(observed.Answer == "Acknowledged." && observed.StopReason == "end_turn" && observed.EventTypes.Length == 2, "SDK gateway decodes actual SSE response through terminal status");
        await gateway.InterruptAsync(created, default);
        var interrupt = JsonSerializer.Deserialize<JsonElement>(handler.Requests.Last().Body);
        check(handler.Requests.Last().Uri.Contains("/sesn_child/events") && interrupt.GetProperty("events")[0].GetProperty("type").GetString() == "user.interrupt", "SDK gateway targets interrupt at the created child");
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<(string Uri, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            Requests.Add((uri.ToString(), body));
            var path = uri.AbsolutePath;
            string json;
            if (path.EndsWith("/events/stream"))
            {
                var sse = "event: agent.message\ndata: {\"id\":\"sevt_answer\",\"type\":\"agent.message\",\"processed_at\":\"2026-10-28T12:00:00Z\",\"content\":[{\"type\":\"text\",\"text\":\"Acknowledged.\"}]}\n\n" +
                    "event: session.status_idle\ndata: {\"id\":\"sevt_idle\",\"type\":\"session.status_idle\",\"processed_at\":\"2026-10-28T12:00:01Z\",\"stop_reason\":{\"type\":\"end_turn\"}}\n\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
            }
            if (path.EndsWith("/events"))
            {
                if (request.Method == HttpMethod.Post) json = "{\"data\":[],\"events\":[]}";
                else
                {
                    var next = !uri.Query.Contains("page=");
                    json = JsonSerializer.Serialize(new { data = new[] { new { id = next ? "sevt_1" : "sevt_2", type = "user.message", processed_at = "2026-10-28T12:00:00Z", content = new[] { new { type = "text", text = "Reviewed text" } } } }, next_page = next ? "events-next" : null });
                }
            }
            else if (path == "/v1/sessions" && request.Method == HttpMethod.Get)
                json = "{\"data\":[" + Session("sesn_parent") + "],\"next_page\":\"sessions-next\",\"prev_page\":null}";
            else if (path == "/v1/sessions" && request.Method == HttpMethod.Post) json = Session("sesn_child");
            else if (path == "/v1/sessions/sesn_parent") json = Session("sesn_parent");
            else throw new InvalidOperationException("Unexpected fixture request: " + path);
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
        private static string Session(string id) => JsonSerializer.Serialize(new
        {
            id, type = "session", agent = new { id = "agent_test", type = "agent", version = 7 }, environment_id = "env_test",
            created_at = "2026-10-28T12:00:00Z", updated_at = "2026-10-28T12:00:00Z", status = "idle", metadata = new { }
        });
    }
}
