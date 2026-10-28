using System.Runtime.CompilerServices;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Beta.Sessions;
using Anthropic.Models.Beta.Sessions.Events;

namespace LessonC13.Services;

public sealed record SessionInventory(JsonElement[] Sessions, string? NextPage);
public sealed record SessionObservation(string Answer, string StopReason, string[] EventTypes, JsonElement[] Usage);

public interface ISessionGateway
{
    bool Configured { get; }
    string AgentId { get; }
    string EnvironmentId { get; }
    Task<SessionInventory> ListAsync(string? page, CancellationToken token);
    Task<JsonElement> RetrieveAsync(string id, CancellationToken token);
    IAsyncEnumerable<JsonElement> EventsAsync(string id, CancellationToken token);
    Task<string> CreateAsync(SessionCreateParams parameters, CancellationToken token);
    Task<SessionObservation> ObserveAsync(string id, CancellationToken token);
    Task InterruptAsync(string id, CancellationToken token);
}

public sealed class AnthropicSessionGateway(ManagedAgentsService provider) : ISessionGateway
{
    internal Func<AnthropicClient>? ClientFactory { get; init; }
    private AnthropicClient Client() => ClientFactory?.Invoke() ?? provider.Client();
    public bool Configured => provider.Configured;
    public string AgentId => provider.Setting("CLAUDE_MANAGED_AGENT_ID")!;
    public string EnvironmentId => provider.Setting("CLAUDE_MANAGED_ENVIRONMENT_ID")!;

    public async Task<SessionInventory> ListAsync(string? cursor, CancellationToken token)
    {
        using var client = Client();
        var page = await client.Beta.Sessions.List(new() { AgentID = AgentId, Limit = 20, Page = cursor }, token);
        return new(page.Items.Select(item => JsonSerializer.SerializeToElement(item.RawData)).ToArray(), page.NextPage);
    }

    public async Task<JsonElement> RetrieveAsync(string id, CancellationToken token)
    {
        using var client = Client();
        var session = await client.Beta.Sessions.Retrieve(id, new(), token);
        return JsonSerializer.SerializeToElement(session.RawData);
    }

    public async IAsyncEnumerable<JsonElement> EventsAsync(string id, [EnumeratorCancellation] CancellationToken token)
    {
        using var client = Client();
        var page = await client.Beta.Sessions.Events.List(id, new() { Order = "asc" }, token);
        while (true)
        {
            foreach (var item in page.Items) { token.ThrowIfCancellationRequested(); yield return item.Json.Clone(); }
            if (!page.HasNext()) yield break;
            page = await page.Next(token);
        }
    }

    public async Task<string> CreateAsync(SessionCreateParams parameters, CancellationToken token)
    {
        using var client = Client();
        return (await client.Beta.Sessions.Create(parameters, token)).ID;
    }

    public async Task<SessionObservation> ObserveAsync(string id, CancellationToken token)
    {
        using var client = Client();
        var observation = new LiveObservation();
        await foreach (var item in client.Beta.Sessions.Events.StreamStreaming(id, new(), token))
        {
            if (observation.Observe(item.Json))
                return new(observation.Answer, observation.StopReason, [.. observation.EventTypes], [.. observation.Usage]);
            if (observation.EventTypes.Count > 500) throw new InvalidOperationException("Session event limit exceeded.");
        }
        throw new InvalidOperationException("Session stream ended without a terminal status.");
    }

    public async Task InterruptAsync(string id, CancellationToken token)
    {
        using var client = Client();
        await client.Beta.Sessions.Events.Send(id, ManagedAgentsService.Parameters<EventSendParams>(new
        { events = new[] { new { type = "user.interrupt" } } }), token);
    }
}
