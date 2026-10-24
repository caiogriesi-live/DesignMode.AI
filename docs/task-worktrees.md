# Isolated Codex tasks

Requires Git and PowerShell 5.1+ (existing Windows tooling; no new dependencies).
Run the launcher from the main checkout:

```powershell
.\scripts\Task-Worktree.ps1 -Operation Create -TaskId 123 -TaskName 'customer search'
```

The returned object contains `TaskId`, `Branch`, absolute `WorktreePath`, and
`BaseCommit`. Capture it as `$task = ...` or pipe to `ConvertTo-Json` for automation.
Start the Codex task with **that WorktreePath as its working directory**. Merely
running this script does not move an existing agent or constrain its filesystem
access. Give each agent the assigned path and branch in its initial instructions.
Do not launch two agents in the same returned directory.

The script fetches origin's branches, resolves `origin/master-caio` to a commit,
and creates a branch and worktree from that commit without changing the main
checkout. Names use `codex/<ID>-<slug>-<UTC YYYYMMDD-HHMMSS>` and
`<main checkout>/worktrees/<ID>-<slug>-<timestamp>`. IDs are numeric; names become
lowercase ASCII slugs, capped at 48 characters, with `task` as the empty fallback.
An exclusive file lock in the common Git directory serializes fetch, reservation,
creation, and cleanup across launcher processes, including linked checkouts.
Collisions with local refs, remote-tracking refs, paths, or saved reservations
advance the timestamp by one second until unused. Thus the timestamp may be ahead
of wall time. Repeated launches of the same ID get separate worktrees.

After stopping the agent and any processes using its worktree, run cleanup from
the main checkout (not from inside the directory being removed):

```powershell
.\scripts\Task-Worktree.ps1 -Operation Cleanup -TaskId 123
```

If the ID has multiple launches, add `-Branch 'codex/123-...'` using the exact
returned branch. Cleanup verifies the managed path, registration, and assigned
branch, then refuses changed, untracked, **and ignored** files. Preserve needed
files and explicitly remove disposable build outputs first. There is no force
option. Cleanup removes only the worktree and its task record; local and remote
branches are retained, even when unpushed. It does not push, merge, or deploy.

## Publish a completed task for review

Use `scripts/Publish-TaskPullRequest.ps1` after the agent has stopped, validation
has passed, and the task changes have been committed. It uses the launcher's
existing JSON record and lock; it does not provision a replacement worktree.
New launcher records also retain `TaskName`. Older records are supported by
checking the task name's slug against the assigned branch.

Run task Git commands and checks in the assigned worktree. Save validation
outside that worktree, for example in `$env:TEMP`, so it stays clean:

```powershell
# In the assigned task worktree, after executing the checks successfully:
$commit = git rev-parse HEAD
$validationFile = Join-Path $env:TEMP 'task-validation.json'
@{
    Commit = $commit
    Results = @(
        @{ Command = 'dotnet test DesignMode.AI.Test/DesignMode.AI.Test.csproj'; ExitCode = 0 }
    )
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $validationFile

# Use the main checkout's helper until tooling is integrated into the base branch.
$publish = @{
    TaskId = $task.TaskId
    TaskName = 'customer search'
    Branch = $task.Branch
    WorktreePath = $task.WorktreePath
    TaskCommit = $commit
    ValidationPath = $validationFile
}
& 'C:\Users\caiog\source\repos\DesignMode.AI\scripts\Publish-TaskPullRequest.ps1' @publish -CheckOnly
& 'C:\Users\caiog\source\repos\DesignMode.AI\scripts\Publish-TaskPullRequest.ps1' @publish
```

Record actual commands and exit codes, never assumed successes. The helper
requires at least one passing check tied to the final commit, but trusts the
caller's validation report; it is not a test runner or CI system. For a text-only
test, content assertions and `git diff HEAD^ HEAD --check` are sufficient.
`-CheckOnly` performs local guards and authenticated remote reads without pushing
or creating a PR. `Test-TaskPullRequest.ps1` accepts the same task/validation
arguments and exercises rejection cases plus the authenticated preflight.

Publication verifies the managed path, registration, task ID/name, exact assigned
branch, final HEAD, base ancestry, clean tracked/untracked state, and passing
validation. `-AllowDirty` explicitly permits local edits, but only the supplied
committed HEAD is published. Ignored build artifacts do not block publication.
Only launcher-shaped `codex/<ID>-<slug>-<timestamp>` branches are eligible;
integration names, the repository default branch, and entries supplied through
`-ProtectedBranches` are rejected. The target is fixed to `master-caio`.

The origin must be an Azure DevOps HTTPS URL without embedded credentials, with
one identical fetch/push destination and no mirror configuration. Existing Azure
DevOps CLI authentication is tried first when its extension is installed, then
noninteractive Git Credential Manager/session credentials. REST requests use
those existing credentials in memory, with redirects disabled. No PAT is created
or saved, no extension is automatically installed, and credentials are not logged.
Missing authentication or repository access fails before push. PR creation rights
can still fail after a successful push; preserve the branch and retry after access
is corrected. There is no repository-specific environment credential convention
to fall back to in this repository.

The helper performs a normal single-branch push with upstream tracking and creates
an active PR titled `[Codex] <task-id> - <task-name>`. The description records the
task, branches, base/final commits, files, validation, and human review requirement.
It verifies the remote commit, PR metadata, open status, disabled auto-complete,
and unchanged remote target SHA. Reruns reuse an identical active PR; a closed PR
or differing metadata requires manual inspection. A failed/ambiguous create can
be retried to discover the existing PR first. The utility's lock coordinates local
runs, but cannot prevent remote users changing refs or PRs concurrently.

Publication stops at review. It never completes/merges PRs, deletes branches,
changes policies, schedules work, invokes pipelines, or deploys. Existing server
hooks/policies may independently react to a push or PR; this helper does not
configure or monitor those external systems. Do not clean up a published task
until its commit, successful push, and PR have been confirmed. The existing
cleanup command remains unchanged and preserves the local branch.

## Limits and recovery

- The lock coordinates this utility on one local filesystem. Other Git tools do
  not honor it; avoid external worktree/ref maintenance while launching tasks.
  Git's own locks make conflicting operations fail rather than overwrite branches.
- Each launch fetches independently; if origin advances between launches, their
  base commits can differ. `BaseCommit` records the exact starting point.
- A crash releases the lock but may leave a reservation, branch, or partial
  worktree. Inspect `git worktree list` and `.git/codex-task-worktrees/*.json` and
  recover manually. Failed launches never automatically delete branches. Do not
  delete the lock file; stale reservations intentionally prevent name reuse.
- Worktrees share refs, object storage, remotes, and Git configuration. They do
  not isolate ports, databases, cloud credentials, or other external resources.
- Uncommitted main-checkout changes are not copied. Until these instructions and
  scripts are integrated into the base branch after review, provision using the
  main checkout's script and pass the isolation instructions to agents explicitly.
- New directory depth may affect Windows path limits and sibling DLL references
  in this solution. Verify dependency paths and assign distinct local service
  ports before running real tasks. Do not copy populated production settings.
- This utility assumes a normal clone with a `.git` directory, not a bare clone
  or a separate Git directory. It is not an agent sandbox or scheduler.
