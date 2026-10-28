using System.Text.Json;
using LessonC13.Components;
using LessonC13.Services;

LocalConfiguration.LoadDotEnv(Path.Combine(Directory.GetCurrentDirectory(), ".env"));
var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls($"http://127.0.0.1:{builder.Configuration["PORT"] ?? "5013"}");
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64_000);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR(options => options.MaximumReceiveMessageSize = 512_000);
builder.Services.AddSingleton<ManagedAgentsService>();
builder.Services.AddSingleton<LabService>();
builder.Services.AddSingleton<LabExecutionGate>();
builder.Services.AddSingleton<ISessionGateway, AnthropicSessionGateway>();
builder.Services.AddSingleton<SessionScenarioService>();

var app = builder.Build();
if (args.Contains("--self-test")) { await SelfTests.RunAsync(); return; }
if (args.Contains("--setup-resources"))
{
    try { await app.Services.GetRequiredService<ManagedAgentsService>().SetupResourcesAsync(); }
    catch (Exception error)
    {
        Console.Error.WriteLine($"Setup failed ({error.GetType().Name}). Resource creation may be uncertain; inspect your account before retrying. Previously saved IDs are retained.");
        Environment.ExitCode = 1;
    }
    return;
}
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error", createScopeForErrors: true);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapGet("/api/lesson", (ManagedAgentsService provider) => Results.Json(new
{
    number = 13, title = LessonEngine.Title, description = LessonEngine.Description, concepts = LessonEngine.Concepts,
    scope = LessonEngine.Scope, cases = LessonEngine.Cases, liveConfigured = provider.Configured
}));
app.MapGet("/api/source", () => Results.Text(LessonEngine.Source, "text/plain"));
app.MapGet("/api/session-scenarios", (SessionScenarioService sessions) => Results.Json(new
{ source = "live-session-scenario", liveConfigured = sessions.Configured, scenarios = SessionScenarioService.Scenarios }));
app.MapPost("/api/session-scenarios/run", async (HttpRequest request, SessionScenarioService sessions) =>
{
    request.HttpContext.Response.Headers.CacheControl = "no-store";
    var origin = request.Headers.Origin.ToString();
    if (origin.Length > 0 && origin != $"{request.Scheme}://{request.Host}")
        return Results.Json(new { error = "Foreign origin" }, statusCode: 403);
    if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "JSON input required" }, statusCode: 415);
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        var input = document.RootElement.Deserialize<SessionScenarioInput>(LabJson.Options) ?? throw new ArgumentException("Expected an object");
        return Results.Json(await sessions.RunAsync(input, request.HttpContext.RequestAborted));
    }
    catch (BadHttpRequestException error) { return Results.Json(new { error = "Input exceeds the sample limit" }, statusCode: error.StatusCode); }
    catch (ExperimentBusyException error) { return Results.Json(new { error = error.Message }, statusCode: 409); }
    catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
    { return Results.Json(new { error = error.Message }, statusCode: 400); }
});
app.MapPost("/api/{operation:regex(^(run|live)$)}", async (string operation, HttpRequest request, LabService lab) =>
{
    request.HttpContext.Response.Headers.CacheControl = "no-store";
    var origin = request.Headers.Origin.ToString();
    if (origin.Length > 0 && origin != $"{request.Scheme}://{request.Host}")
        return Results.Json(new { error = "Foreign origin" }, statusCode: 403);
    if (!string.Equals(request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase))
        return Results.Json(new { error = "JSON input required" }, statusCode: 415);
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
        var input = LessonEngine.Parse(document.RootElement);
        return Results.Json(await lab.RunAsync(input, operation == "live", request.HttpContext.RequestAborted));
    }
    catch (BadHttpRequestException error) { return Results.Json(new { error = "Input exceeds the sample limit" }, statusCode: error.StatusCode); }
    catch (ExperimentBusyException error) { return Results.Json(new { error = error.Message }, statusCode: 409); }
    catch (Exception error) when (error is ArgumentException or JsonException or InvalidOperationException)
    { return Results.Json(new { error = error.Message }, statusCode: 400); }
});
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
