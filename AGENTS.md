# Repository Guidelines

## Concurrent Codex Tasks

Before starting parallel development, provision each task with `scripts/Task-Worktree.ps1`
as described in `docs/task-worktrees.md`. Launch each agent with its working directory
set to the returned absolute `WorktreePath`. All edits, builds, tests, and Git commands
for that task must run there. Never switch branches in another task's worktree or the
main checkout. Keep the assigned branch; do not merge or commit to `master-caio`.
Worktrees isolate files, HEAD, and index, but share Git refs and configuration; avoid
repository-wide Git configuration, pruning, or branch maintenance during parallel work.

## Project Structure & Module Organization

`DesignMode.AI.sln` contains six .NET 8 projects:

- `DesignMode.AI.Client/`: Blazor WebAssembly UI. Razor pages are under `Pages/`, layouts under `Layout/`, client services under `Services/`, and static CSS, JavaScript, images, and Argon Dashboard assets under `wwwroot/`.
- `DesignMode.AI.Server/`: Azure Functions v4 isolated-worker backend. HTTP, queue, and timer functions live in `DesignModeFunctions.cs` and the focused `*Function.cs` files.
- `DesignMode.AI.Business/`: business services.
- `DesignMode.AI.Data/`: Dapper/SQL connection and data-access code. Most current DynamoDB operations are implemented directly in server functions.
- `DesignMode.AI.Shared/`: shared models, requests, interfaces, authentication, and utilities.
- `DesignMode.AI.Test/`: MSTest tests.

Keep generated `bin/`, `obj/`, `publish/`, and `publish-test/` content out of source changes.

## Build, Test, and Development Commands

Run commands from the repository root:

```powershell
dotnet restore DesignMode.AI.sln
dotnet build DesignMode.AI.sln
dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj
dotnet run --project DesignMode.AI.Server -- --port 7032
dotnet run --project DesignMode.AI.Client
```

Start the server and client in separate terminals. The client expects the local API at `http://localhost:7032/`. Some projects reference `DesignMode.AI.Common.dll` and `DesignMode.AI.Services.OpenAI.dll` from sibling repositories; ensure those artifacts exist before troubleshooting build failures.

## Coding Style & Naming Conventions

Use four-space indentation and standard C# conventions: PascalCase for types, methods, components, and public properties; camelCase for parameters and locals; `_camelCase` for private fields. Nullable reference types and implicit usings are enabled. Prefer one focused Azure Function class per feature and place reusable contracts in `Shared`, data access in `Data`, and business rules in `Business`. Run `dotnet format DesignMode.AI.sln` before submitting broad formatting changes.

## Testing Guidelines

Tests use MSTest. Name test classes after the subject (`AuthServiceTests`) and methods after behavior (`Login_InvalidPassword_ReturnsFailure`). Add unit tests for new business and data behavior; isolate external AWS, Azure, Stripe, and OpenAI calls behind test doubles. No coverage threshold is currently enforced.

## Commit & Pull Request Guidelines

Recent history mostly uses `updates`; improve on this with short imperative, scoped subjects such as `Server: validate reset token expiry`. Keep commits focused. Pull requests should describe behavior and architecture impact, list verification commands, link relevant issues, and include screenshots for Razor/UI changes. Never commit secrets, function keys, connection strings, or populated `local.settings.json`; use environment variables or local user configuration.
