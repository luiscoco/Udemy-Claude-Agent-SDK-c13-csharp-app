# C13 · Blazor / .NET 10 migration

This is the C# migration of `../lesson-c13-app`, using Blazor Interactive Server and the official [Anthropic 13.3.0 package](https://www.nuget.org/packages/Anthropic/13.3.0). The original application is unchanged. Node.js is not required.

## Run

Install the .NET 10 SDK, then run from this folder:

```powershell
dotnet restore
dotnet run
```

Open **http://127.0.0.1:5013**. Stop with Ctrl+C. Set `PORT` or pass `dotnet run -- --urls http://127.0.0.1:5014` to use another port. The default is deliberately different from the TypeScript application's port 3013 so both can run together.

Local experiments need no credentials. Both original scenarios are available with editable inputs, structured evidence, execution steps, and a local JSONL journal. Teaching pages remain at `/guide.html#learning`, `/guide.html#run-guide`, and `/architecture.html`. The interface retains the original styling, astronaut artwork, responsive layout, and persisted light/dark mode. The teaching content and architecture diagram describe the C# implementation.

## Optional live commentary

```powershell
Copy-Item .env.example .env
notepad .env
dotnet run
```

Enter your own `ANTHROPIC_API_KEY`, `CLAUDE_MANAGED_AGENT_ID`, and `CLAUDE_MANAGED_ENVIRONMENT_ID`. Existing resource IDs can be reused from the original application. This migration does not copy credentials. Environment variables are also supported. Existing process environment overrides `.env`; environment values override user-secrets. Restart after editing configuration.

For .NET user-secrets, use Development mode (`dotnet run` uses the included launch profile) and omit corresponding entries from `.env`:

```powershell
dotnet user-secrets set "ANTHROPIC_API_KEY" "your-real-key"
dotnet user-secrets set "CLAUDE_MANAGED_AGENT_ID" "your-real-agent-id"
dotnet user-secrets set "CLAUDE_MANAGED_ENVIRONMENT_ID" "your-real-environment-id"
```

If IDs are missing, explicitly provision them with:

```powershell
dotnet run -- --setup-resources
```

Setup creates an environment before a text-only agent and saves each ID immediately in `.lab-data/resources.json`. Existing IDs are reused. `CLAUDE_MODEL` defaults to `claude-opus-5-5`; choose a model available to your account. If creation fails after an uncertain network response, inspect your account before retrying. Provisioning never runs automatically at startup.

Live commentary uses the SDK's **Managed Agents sessions**, preserving the original integration. The prompt is queued in `InitialEvents` when the session is created, then `StreamStreaming` observes the session history and new events. Overrides explicitly remove tools, MCP servers, and skills. The session has a $10 list-cost ceiling, no retries, a 60-second observation deadline, and an interrupt attempt on failure. Failure reports that the remote stop is unconfirmed. The live response includes session ID, event types, observed model usage, stop reason, and a SHA-256 hash of the supplied experiment. Managed Agents access is required and API use may incur charges.

Live commentary explains the local result. It does not create the planned child branch, restore files, certify the experiment, or test external integrations.

## Live session scenarios

Open **http://127.0.0.1:5013/live-sessions**. These scenarios use real Managed Agents APIs, separately from commentary:

1. **List live sessions** — read one page for the configured agent/environment; load the next page using its cursor.
2. **Create a teaching parent** — create a text-only provider session with three Project Aurora seed messages, then observe its status and canonical event IDs.
3. **Inspect and mirror events** — read ascending canonical history across pages and durably append unseen IDs to a local mirror. Replays are deduplicated; conflicting bytes for one ID are rejected.
4. **Review a branch boundary** — select a real event and preview the context through that boundary. Later events are excluded. The preview includes a hash of the selected canonical history.
5. **Create a reviewed branch** — review/edit the context, accept the transfer, and create a real new session. The server rereads the selected prefix, rejects a stale hash, pins the parent's agent version, and records parent/boundary lineage in metadata. Only the reviewed context is sent.
6. **Reject a missing boundary** — verify the boundary is absent from complete canonical history and report zero child-create requests. Partial reads produce an incomplete result.
7. **Capture history snapshot** — save actual provider events and their canonical JSON SHA-256 hash locally.
8. **Compare history snapshot** — compare a saved snapshot with current canonical event bytes. This does not restore remote files or clone a container.

Creating a parent or child can incur charges. Each session has a $10 list-cost ceiling (`amount: "1000"`, in cents), empty tools/MCP/skills, no retries, and a 60-second observation deadline. Known created IDs are recorded before streaming. On uncertain failures the result keeps the ID, attempts an interrupt on that new session, and reports that stopping is unconfirmed. Inspect your account before retrying uncertain creation.

Local artifacts are under `.lab-data/live-sessions/`: `runs.jsonl`, `mirror-<session-hash>.jsonl`, and `snapshot-<id>.json`. These files contain session content; `.lab-data` remains ignored. A shared concurrency gate covers local runs, commentary, and live scenarios across API requests and browser circuits. API endpoints are `GET /api/session-scenarios` and `POST /api/session-scenarios/run` with the same JSON, origin, body-size, and bounded-text checks as the original lab.

The new implementation is in `Services/SessionGateway.cs`, `Services/SessionScenarioService.cs`, and `Components/Pages/LiveSessions.razor`. Self-tests use an isolated fixture gateway to exercise branch creation, negative cases, mirrors, snapshots, and uncertain failures without accessing an Anthropic account.

## Verification

```powershell
dotnet run -- --self-test
dotnet build -c Release
```

Self-tests exercise boundary selection, UTF-8 hash vectors, line endings, unchanged parent state, deterministic seed state, invalid input, generated SDK request serialization, and live event interpretation. They make no provider requests.

With the server running, the browser/API verification script also checks Blazor interaction, journal writes, API validation, navigation, mobile layout, and dark mode. It uses Python with Playwright and an installed Chromium browser:

```powershell
python scripts/verify-app.py
```

## Implementation

| Original | C# migration |
| --- | --- |
| `src/lesson.ts`, `src/core.ts` | `Services/LessonEngine.cs` |
| `src/server.ts` | `Program.cs`, `Services/LabService.cs` |
| `src/client.ts`, `public/index.html` | `Components/Pages/Home.razor` |
| `src/provider.ts` | `Services/ManagedAgentsService.cs` |
| `scripts/setup-resources.mts` | `dotnet run -- --setup-resources` |
| Student pages | `Components/Pages/Guide.razor`, `Architecture.razor` |

`GET /api/lesson`, `GET /api/source`, `POST /api/run`, and `POST /api/live` are retained. `/api/source` displays the embedded C# implementation, including in published builds. API inputs must be JSON objects, with a 64,000-byte request limit and 20,000-character text fields. Blazor inputs use the same engine and validation. A shared service permits one experiment at a time across API requests and Blazor circuits; local runs append to `.lab-data/runs.jsonl`.

`.env`, `.lab-data`, build output, and local screenshot artifacts are ignored. No key or production session state belongs in source control.
