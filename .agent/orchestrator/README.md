# Autonomous Engineering Orchestrator V1

From a clean repository at latest **local** `master-caio`, with an approved,
committed task and this implementation available, run:

```powershell
node .agent/orchestrator/run.mjs .agent/tasks/approved/example.md
```

Requires Node.js 18+, Git, an authenticated `codex` executable on PATH, and the
existing role-specific build/browser dependencies. The runner does not fetch,
merge, push, deploy, create tasks, or provision worktrees. It creates one timestamped
`codex/` branch; subsequent invocations must remain in that checkout. Each stage is
a fresh `codex exec` session using its permanent `.agent/prompts/<role>.md` file.
Branch ownership and unique evidence paths replace the prompts' manual branch
creation and historical example report paths. Other role obligations remain intact.

Run the same command to resume. Exit 0 means `READY_FOR_HUMAN_REVIEW`; exit 2
means `BLOCKED` / `HUMAN_REQUIRED`. Human approval is still required for integration.
Initial precondition failures print explicit blocked diagnostics before any agent
launch. Existing terminal lifecycle records are never silently reset.

## State and evidence

`.agent/artifacts/orchestrator/<task-id>/state.json` is atomically replaced after
each transition. It records task/branch/base/HEAD, timestamps, pending invocation,
completed repairs, limit, ordered results and terminal decision. Each invocation
retains its input prompt, output schema, CLI event stream and structured final
result. Normal independent Markdown reports live in
`.agent/results/<task-id>/<sequence>-<role>.md`. Existing roles remain separate.
Runtime artifacts are local evidence (the repository already ignores artifacts);
archive them with reports when sharing a lifecycle. Do not delete them to resume.

The final structured response binds invocation, task, stage, branch, HEAD and
Markdown report. Unrecognized, stale, missing or malformed results block; prose
is never used to infer success. Initial QA saves its exact commands and reusable
test scripts/fixtures in `qa-contract/`; those files, the task and all five role
prompts are hashed. Retry instructions use the same contract. Changes block.
QA must list every contract dependency; environment prerequisites belong in
`contract.md`. Evidence and failure reports are never rewritten by retries.

QA failure runs triage; only confirmed `BRANCH_REGRESSION` permits repair.
Baseline-only, mixed/unsafe, infrastructure and inconclusive findings escalate
conservatively to a human in V1. After three successful repair completions, another
QA failure blocks immediately. Failed/incomplete repairs never consume a completed
attempt. Regression failure blocks without hiding its report.

## Restart and concurrency safety

A loopback exclusive socket derived from the Git common directory prevents
simultaneous runners, including across checkouts. A hash collision safely rejects
the second runner. A shared `.git/designmode-orchestrator-active.json` ownership
record also rejects another task while a lifecycle remains nonterminal or has an
unresolved invocation. No parallel agents are launched.

Invocation intent is saved before launch. After a host interruption, a complete,
valid final response is consumed once without relaunching the stage. If completion
is uncertain, execution blocks instead of launching a duplicate agent. An interrupted
CLI child may still be running: a human must establish that it has exited and inspect
the preserved evidence before any recovery. There is intentionally no automatic
reset/retry override for an ambiguous invocation. Use the explicit human reconciliation
procedure below; never edit pending state or increment repair counts manually to
represent an incomplete run.

The runner uses the `--approve-for-me` preset, which supplies workspace-write
sandboxing and automatic approval review together, never bypass flags. Do not add
`--sandbox workspace-write`: the installed CLI rejects that combination. Verified
with `codex-cli 0.155.0-alpha.16.4`; other installations must pass the compatibility
test below. There is no fallback to weaker permissions if the CLI is incompatible.
Existing Codex permissions and approval policies apply;
if a role cannot perform an operation,
it must report BLOCKED. Role instructions prohibit production and protected-system
actions. These instructions and post-invocation hashes are guardrails, not an OS
security boundary against a malicious agent. Use appropriately scoped credentials;
the control plane does not grant production permissions. Read-only roles are also
checked for changes to HEAD or tracked/untracked source before accepting their results.
Developer and Repair must commit source changes before returning READY_FOR_QA.
Completed Markdown and structured result files are hashed against later overwrites.

## Developer verification

```powershell
node --test .agent/orchestrator/run.test.mjs
dotnet build DesignMode.AI.Client/DesignMode.AI.Client.csproj
dotnet build DesignMode.AI.Server/DesignMode.AI.Server.csproj
git diff --check
```

The suite requires `codex` on PATH. Its compatibility test passes the production
argument builder's complete arguments to the installed CLI with empty stdin, both
normally and with user configuration disabled. It requires the specific missing-prompt
exit after argument parsing, rather than treating help output as proof of compatible
flags. It also checks that CLI help still documents the preset's sandbox/review intent.
No model session or lifecycle is started by this probe; it does not verify authentication,
runtime sandbox enforcement, or successful agent completion. Missing or incompatible
CLIs fail the test rather than silently skipping it.

Lifecycle unit tests use temporary Git repositories and fake structured agent responses;
they never launch QA, triage, repair or regression agents. They cover recovery,
exhaustion, incomplete/completed invocation restart, immutable contracts and locking.
They do **not** prove Experiment #3. An independently authorized real autonomous
lifecycle, repair-exhaustion and restart demonstration remain required by the task.

## Explicit human reconciliation

Only use this after establishing that the invocation's Codex child and any
workers it started have exited. A timeout, missing result, BLOCKED state or free
repository lock does NOT prove exit. Inspect the launching terminal/session and
OS process tree (on Windows, Task Manager Details or Get-CimInstance Win32_Process,
including PID, parent PID, creation time and command line). Identify the actual
invocation, wait for its process tree to exit, and record how this was verified.
If identity or exit is uncertain, stop and leave ownership blocked. The command
cannot independently prove process exit and never kills or retries a child.

1. Establish exit as above; prevent anyone restarting the child during recovery.
2. Inspect and back up the complete lifecycle directory, associated Markdown
   reports, and `.git/designmode-orchestrator-active.json`. Review any working-tree
   changes and original failure. Keep the backup outside the lifecycle directory.
3. On the original lifecycle branch and checkout, create a human request JSON
   file (for example `human-reconciliation.json`) with these exact fields. Replace
   every example value with the actual invocation and human findings:

```json
{
  "invocationId": "001-developer",
  "disposition": "ABANDON_UNRESOLVED",
  "operator": "Human name",
  "reason": "Why this invocation is being abandoned after reviewing evidence",
  "childExited": true,
  "exitEvidence": "Actual process/session identity and how/when its exit and worker exit were established"
}
```

```powershell
node .agent/orchestrator/run.mjs reconcile .agent/tasks/approved/<task-id>.md human-reconciliation.json
```

4. Exit 0 means reconciliation completed (or an identical request was already
   completed), NOT agent success. Exit 2 means refusal/failure; inspect diagnostics.
   The operation uses the same repository lock, checks exact ownership, task,
   invocation, branch, protected contracts and prior completed evidence. Missing
   state, wrong ownership, non-BLOCKED state, missing exit attestation and conflicting
   repeats are refused. Existing result files are never accepted by this operation.
5. One atomic state replacement stores `reconciliation`: the complete original
   BLOCKED state including pending intent, a timestamp, observed HEAD, human request,
   evidence hashes (null denotes an absent file), and `HUMAN_ABANDONED_UNRESOLVED`.
   Only then is pending cleared. Original invocation files are untouched. Stage,
   humanRequired, failure decision, history and completed repair count stay unchanged.
   A crash before replacement leaves pending blocking ownership; retry the identical
   request. A crash after replacement leaves the full audit record committed and
   retry is a no-op. Keep that state and all evidence permanently.
6. Ownership becomes eligible for takeover by the normal runner only after that
   atomic replacement. The ownership file is not deleted or changed by reconciliation.
   This lifecycle stays terminal BLOCKED; rerunning it never retries the abandoned
   invocation. A human may review/salvage changes and prepare a separately approved
   lifecycle, satisfying the normal clean checkout/latest local master-caio rules.
   This command does not switch branches, integrate changes or authorize a new live
   test. Never manually erase state or fabricate result.json to continue.
