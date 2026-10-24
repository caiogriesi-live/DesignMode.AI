#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import net from 'node:net';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const roles = { DEVELOP: 'developer', QA: 'qa', TRIAGE: 'triage', REPAIR: 'repair', REGRESSION: 'regression' };
const outcomes = {
    DEVELOP: ['READY_FOR_QA', 'BLOCKED'], QA: ['PASS', 'FAIL', 'BLOCKED'],
    TRIAGE: ['BRANCH_REGRESSION', 'PRE_EXISTING', 'TEST_INFRASTRUCTURE', 'FLAKY_OR_INCONCLUSIVE', 'BLOCKED'],
    REPAIR: ['READY_FOR_QA', 'BLOCKED'], REGRESSION: ['READY_FOR_HUMAN_REVIEW', 'FAIL', 'BLOCKED']
};
const terminal = stage => ['BLOCKED', 'READY_FOR_HUMAN_REVIEW'].includes(stage);
const now = () => new Date().toISOString();
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
function atomic(file, value) {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    const tmp = `${file}.tmp`;
    const fd = fs.openSync(tmp, 'w');
    try { fs.writeFileSync(fd, JSON.stringify(value, null, 2) + '\n'); fs.fsyncSync(fd); }
    finally { fs.closeSync(fd); }
    fs.renameSync(tmp, file);
}
const read = file => JSON.parse(fs.readFileSync(file, 'utf8'));
function git(root, ...args) {
    const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
    if (result.error || result.status !== 0) throw new Error(result.error?.message || result.stderr);
    return result.stdout.trim();
}
function inside(root, relative) {
    const resolved = path.resolve(root, relative);
    if (!resolved.startsWith(path.resolve(root) + path.sep)) throw new Error(`Path outside repository: ${relative}`);
    if (fs.existsSync(resolved) && !fs.realpathSync(resolved).startsWith(fs.realpathSync(root) + path.sep)) {
        throw new Error(`Symlink outside repository: ${relative}`);
    }
    return resolved;
}
function sourceSnapshot(root) {
    return Object.fromEntries([...new Set(git(root, 'ls-files', '--cached', '--others', '--exclude-standard', '-z').split('\0'))].filter(file => file && !file.startsWith('.agent/results/') && !file.startsWith('.agent/artifacts/')).sort()
        .map(file => [file, fs.existsSync(inside(root, file)) ? hash(inside(root, file)) : null]));
}

// Pure transition function; completed repairs are counted only after a validated result.
export function transition(stage, outcome, completed) {
    if (!outcomes[stage]?.includes(outcome)) throw new Error(`Invalid ${stage} outcome: ${outcome}`);
    if (stage === 'DEVELOP') return outcome === 'READY_FOR_QA' ? 'QA' : 'BLOCKED';
    if (stage === 'QA') return outcome === 'PASS' ? 'REGRESSION' : outcome === 'FAIL' && completed < 3 ? 'TRIAGE' : 'BLOCKED';
    if (stage === 'TRIAGE') return outcome === 'BRANCH_REGRESSION' && completed < 3 ? 'REPAIR' : 'BLOCKED';
    if (stage === 'REPAIR') return outcome === 'READY_FOR_QA' ? 'QA' : 'BLOCKED';
    return outcome === 'READY_FOR_HUMAN_REVIEW' ? outcome : 'BLOCKED';
}

export function validateResult(result, pending, state, root) {
    for (const key of ['invocationId', 'taskId', 'stage', 'branch']) {
        const expected = key === 'invocationId' ? pending.id : key === 'stage' ? pending.stage : state[key];
        if (result[key] !== expected) throw new Error(`Result ${key} mismatch`);
    }
    if (!outcomes[pending.stage].includes(result.outcome)) throw new Error('Invalid outcome');
    if (result.report !== pending.report || !fs.existsSync(inside(root, result.report)) ||
        fs.statSync(inside(root, result.report)).size === 0) throw new Error('Missing normal Markdown report');
    if (!/^[a-f0-9]{40,64}$/.test(result.head)) throw new Error('Missing result HEAD');
    if (!Array.isArray(result.contractFiles)) throw new Error('Missing QA contract file list');
}

export class Orchestrator {
    constructor(root, task, runner = runCodex) {
        this.root = root;
        this.task = task.replaceAll('\\', '/');
        if (!/^\.agent\/tasks\/approved\/[a-zA-Z0-9_-]+\.md$/.test(this.task)) throw new Error('Expected one .agent/tasks/approved/<task-id>.md');
        this.id = path.basename(task, '.md');
        this.dir = inside(root, `.agent/artifacts/orchestrator/${this.id}`);
        this.stateFile = path.join(this.dir, 'state.json');
        this.runner = runner;
    }
    save() { this.state.updatedAt = now(); atomic(this.stateFile, this.state); }
    block(reason) {
        this.state.stage = 'BLOCKED'; this.state.humanRequired = true;
        this.state.decision = reason; this.save(); return this.state;
    }
    initialize() {
        if (fs.existsSync(this.stateFile)) { this.state = read(this.stateFile); return; }
        const taskFile = inside(this.root, this.task);
        if (!/^STATUS:\s*APPROVED\s*$/m.test(fs.readFileSync(taskFile, 'utf8'))) throw new Error('Task is not APPROVED');
        if (git(this.root, 'status', '--porcelain', '--untracked-files=all')) throw new Error('Start requires a clean checkout; commit the approved task first');
        const base = git(this.root, 'rev-parse', 'master-caio');
        if (git(this.root, 'rev-parse', 'HEAD') !== base) throw new Error('Start at latest local master-caio; no automatic checkout or reset');
        const branch = `codex/${this.id}-${now().replace(/[-:.]/g, '')}`;
        const protectedFiles = Object.fromEntries([this.task, ...Object.values(roles).map(role => `.agent/prompts/${role}.md`)].map(file => [file, hash(inside(this.root, file))]));
        this.state = { version: 1, taskId: this.id, task: this.task, stage: 'DEVELOP', branch,
            baseCommit: base, head: base, completedRepairs: 0, maxRepairAttempts: 3,
            humanRequired: false, createdAt: now(), protectedFiles, qaContract: null,
            pending: null, history: [], decision: null };
        this.save();
        // Persist intent first; restart can finish this exact branch creation safely.
        git(this.root, 'switch', '-c', branch, base);
    }
    guard() {
        const s = this.state;
        if (s.version !== 1 || s.task !== this.task || s.taskId !== this.id || s.maxRepairAttempts !== 3 ||
            !Number.isInteger(s.completedRepairs) || s.completedRepairs < 0 || s.completedRepairs > 3) throw new Error('Invalid persisted state');
        for (const [file, digest] of Object.entries({ ...s.protectedFiles, ...s.qaContract })) {
            if (hash(inside(this.root, file)) !== digest) throw new Error(`Protected task, role or QA contract changed: ${file}`);
        }
        for (const entry of s.history) {
            if (hash(inside(this.root, entry.result)) !== entry.resultHash || hash(inside(this.root, entry.report)) !== entry.reportHash) {
                throw new Error(`Prior independent evidence changed: ${entry.id}`);
            }
        }
        let branch = git(this.root, 'branch', '--show-current');
        if (branch !== s.branch && s.history.length === 0 && !s.pending && git(this.root, 'rev-parse', 'HEAD') === s.baseCommit &&
            !git(this.root, 'branch', '--list', s.branch)) {
            git(this.root, 'switch', '-c', s.branch, s.baseCommit); branch = s.branch;
        }
        if (branch !== s.branch) throw new Error('Feature branch changed; refusing to switch existing work');
        if (!s.pending && git(this.root, 'rev-parse', 'HEAD') !== s.head) throw new Error('HEAD changed outside an agent invocation');
    }
    prepare() {
        const s = this.state;
        const id = `${String(s.history.length + 1).padStart(3, '0')}-${roles[s.stage]}`;
        const relative = `.agent/artifacts/orchestrator/${this.id}/${id}`;
        const p = { id, stage: s.stage, branch: s.branch, startHead: s.head, startedAt: now(),
            repairAttempt: s.stage === 'REPAIR' ? s.completedRepairs + 1 : s.completedRepairs,
            report: `.agent/results/${this.id}/${id}.md`, result: `${relative}/result.json`,
            prompt: `${relative}/prompt.txt`, schema: `${relative}/schema.json`, log: `${relative}/events.jsonl`,
            sourceSnapshot: sourceSnapshot(this.root) };
        fs.mkdirSync(inside(this.root, relative), { recursive: true });
        const schema = { type: 'object', additionalProperties: false,
            properties: Object.fromEntries(['invocationId', 'taskId', 'stage', 'branch', 'head', 'report', 'outcome', 'reason'].map(key => [key, { type: 'string' }])),
            required: ['invocationId', 'taskId', 'stage', 'branch', 'head', 'report', 'outcome', 'reason', 'contractFiles'] };
        schema.properties.outcome = { type: 'string', enum: outcomes[s.stage] };
        schema.properties.contractFiles = { type: 'array', items: { type: 'string' } };
        atomic(inside(this.root, p.schema), schema);
        const prompt = `You are the ${roles[s.stage]} Agent. Read and follow .agent/prompts/${roles[s.stage]}.md.
Authoritative task: ${s.task}. Read it in full. Never edit it or weaken acceptance criteria.
This invocation performs ONLY ${s.stage}; do not launch other roles or the orchestrator.
The orchestrator already created branch ${s.branch} from latest local master-caio ${s.baseCommit}.
Stay on that branch; do not create another branch. Current HEAD: ${s.head}.
Write the normal detailed Markdown role report to ${p.report}, overriding example/hardcoded report paths.
Retain all evidence under .agent/artifacts/orchestrator/${this.id}/${id}/.
Prior independent results (read all applicable inputs): ${JSON.stringify(s.history)}
Completed repairs: ${s.completedRepairs}; this invocation repair attempt: ${p.repairAttempt}.
${s.qaContract ? `Rerun EXACTLY the frozen QA contract files: ${JSON.stringify(s.qaContract)}. Do not modify those files, tests, assertions or fixtures.` : 'For initial QA, write reusable independent acceptance test scripts and any fixtures under .agent/artifacts/orchestrator/' + this.id + '/qa-contract/. Include a contract.md with exact commands, environment and criteria; return every contract file in contractFiles. Freeze the contract even on FAIL. Non-QA roles return an empty contractFiles array.'}
Only Developer and Repair may modify application code and commit task changes locally.
QA, Triage and Regression must not modify application code or commit.
Never merge, push, deploy, change production infrastructure, Stripe, advertising, generate tasks, or run concurrent tasks.
Preserve authentication, security, AI generation, usage/accounting, database and regression protections.
Triage: BRANCH_REGRESSION requires experimentally confirmed base PASS / feature FAIL. Mixed or unsafe/inconclusive findings must BLOCK. Baseline failures alone are PRE_EXISTING, never BRANCH_REGRESSION.
Return structured final JSON matching the supplied schema AFTER saving the Markdown report.
invocationId=${id}; taskId=${this.id}; stage=${s.stage}; branch=${s.branch}; report=${p.report}.
head must be the actual full git HEAD. outcome must be one of ${outcomes[s.stage].join(', ')}.
reason must explain the decision. Keep the complete role evidence in Markdown.\n`;
        fs.writeFileSync(inside(this.root, p.prompt), prompt);
        s.pending = p; this.save(); return p;
    }
    consume(p) {
        const s = this.state;
        const result = read(inside(this.root, p.result));
        validateResult(result, p, s, this.root);
        if (git(this.root, 'rev-parse', 'HEAD') !== result.head) throw new Error('Result HEAD differs from checkout');
        git(this.root, 'merge-base', '--is-ancestor', p.startHead, result.head);
        if (!['DEVELOP', 'REPAIR'].includes(p.stage) && result.head !== p.startHead) throw new Error('Read-only role changed HEAD');
        if (!['DEVELOP', 'REPAIR'].includes(p.stage) && JSON.stringify(sourceSnapshot(this.root)) !== JSON.stringify(p.sourceSnapshot)) {
            throw new Error('Read-only role modified tracked source files');
        }
        if (['DEVELOP', 'REPAIR'].includes(p.stage) && result.outcome === 'READY_FOR_QA') {
            const dirtySource = git(this.root, 'diff', '--name-only', 'HEAD').split('\n')
                .filter(file => file && !file.startsWith('.agent/results/') && !file.startsWith('.agent/artifacts/'));
            const untrackedSource = git(this.root, 'ls-files', '--others', '--exclude-standard').split('\n')
                .filter(file => file && !file.startsWith('.agent/results/') && !file.startsWith('.agent/artifacts/'));
            if (dirtySource.length || untrackedSource.length) throw new Error('Developer/Repair left uncommitted source changes');
        }
        if (p.stage === 'QA' && !s.qaContract && result.outcome !== 'BLOCKED') {
            const prefix = `.agent/artifacts/orchestrator/${this.id}/qa-contract/`;
            if (!result.contractFiles.length || !result.contractFiles.includes(prefix + 'contract.md')) throw new Error('Initial QA did not preserve its contract');
            s.qaContract = Object.fromEntries(result.contractFiles.map(file => {
                if (!file.startsWith(prefix) || file.includes('..')) throw new Error('QA contract outside dedicated directory');
                return [file, hash(inside(this.root, file))];
            }));
        }
        if (p.stage === 'REPAIR' && result.outcome === 'READY_FOR_QA') s.completedRepairs++;
        s.stage = transition(p.stage, result.outcome, s.completedRepairs);
        const { sourceSnapshot: snapshot, ...evidence } = p;
        s.history.push({ ...evidence, completedAt: now(), outcome: result.outcome, head: result.head, reason: result.reason,
            resultHash: hash(inside(this.root, p.result)), reportHash: hash(inside(this.root, p.report)) });
        s.mostRecentResult = p.result; s.head = result.head; s.pending = null;
        s.humanRequired = s.stage === 'BLOCKED';
        s.decision = s.stage === 'BLOCKED' ? `HUMAN_REQUIRED: ${result.reason || result.outcome}; completed repairs=${s.completedRepairs}` : s.stage;
        this.save();
    }
    run() {
        this.initialize();
        if (terminal(this.state.stage)) return this.state;
        try {
            while (!terminal(this.state.stage)) {
                this.guard();
                let p = this.state.pending;
                if (!p) {
                    p = this.prepare();
                    this.runner(this.root, p);
                }
                // Never automatically repeat an invocation with uncertain completion.
                if (!fs.existsSync(inside(this.root, p.result))) throw new Error(`Incomplete invocation ${p.id}; inspect evidence before human recovery. No repair attempt consumed.`);
                this.guard(); this.consume(p);
            }
        } catch (error) { return this.block(error.message); }
        return this.state;
    }
}

export function codexArgs(root, p) {
    // This preset supplies workspace-write AND automatic approval review.
    // The CLI rejects combining it with an explicit --sandbox option.
    return ['exec', '--cd', root, '--approve-for-me', '--json',
        '--output-schema', inside(root, p.schema), '--output-last-message', inside(root, p.result), '-'];
}

function runCodex(root, p) {
    const log = fs.openSync(inside(root, p.log), 'a');
    try {
        const result = spawnSync('codex', codexArgs(root, p),
        { cwd: root, input: fs.readFileSync(inside(root, p.prompt), 'utf8'), stdio: ['pipe', log, log],
            timeout: 7200000, windowsHide: true });
        // The durable final result is authoritative even if the host exits after writing it.
        if (!fs.existsSync(inside(root, p.result)) && (result.error || result.status !== 0)) {
            throw new Error(`Codex invocation failed: ${result.error?.message || result.status}; see ${p.log}`);
        }
    } finally { fs.closeSync(log); }
}

export async function lockRepository(root) {
    const common = fs.realpathSync(path.resolve(root, git(root, 'rev-parse', '--git-common-dir'))).toLowerCase();
    const port = 20000 + (crypto.createHash('sha256').update(common).digest().readUInt32BE(0) % 40000);
    const server = net.createServer();
    await new Promise((resolve, reject) => { server.once('error', reject); server.listen({ port, host: '127.0.0.1', exclusive: true }, resolve); });
    return server;
}

// Human-only abandonment, never acceptance of an agent result. The single atomic
// state replacement contains both the original failure and its disposition.
export async function reconcile(root, task, request) {
    const lock = await lockRepository(root);
    try {
        const runner = new Orchestrator(root, task);
        const s = read(runner.stateFile);
        const { invocationId, disposition, operator, reason, childExited, exitEvidence } = request;
        if (disposition !== 'ABANDON_UNRESOLVED' || childExited !== true ||
            [invocationId, operator, reason, exitEvidence].some(v => typeof v !== 'string' || !v.trim())) {
            throw new Error('Explicit abandonment, operator, reason and verified child-exit evidence required');
        }
        if (s.task !== runner.task || s.taskId !== runner.id || s.stage !== 'BLOCKED' || s.humanRequired !== true) {
            throw new Error('Reconciliation requires the specified BLOCKED / HUMAN_REQUIRED lifecycle');
        }
        if (s.reconciliation) {
            if (JSON.stringify(s.reconciliation.request) !== JSON.stringify(request) || s.pending) throw new Error('Conflicting reconciliation');
            return s; // Exact retry after a successful atomic replacement is a no-op.
        }
        const active = read(path.join(path.resolve(root, git(root, 'rev-parse', '--git-common-dir')), 'designmode-orchestrator-active.json'));
        if (active.taskId !== runner.id || path.resolve(active.stateFile) !== runner.stateFile) throw new Error('Lifecycle does not own this repository checkout');
        const p = s.pending;
        if (!p || p.id !== invocationId || !roles[p.stage] || p.branch !== s.branch ||
            p.id !== `${String(s.history.length + 1).padStart(3, '0')}-${roles[p.stage]}`) throw new Error('Unresolved invocation mismatch');
        runner.state = s;
        runner.guard();
        const evidence = {};
        for (const key of ['prompt', 'schema', 'log', 'result', 'report']) {
            const file = inside(root, p[key]);
            evidence[p[key]] = fs.existsSync(file) ? hash(file) : null;
        }
        const originalState = structuredClone(s);
        s.reconciliation = { request, reconciledAt: now(), originalStateHash: hash(runner.stateFile), observedHead: git(root, 'rev-parse', 'HEAD'),
            originalState, evidence, outcome: 'HUMAN_ABANDONED_UNRESOLVED' };
        s.pending = null;
        // stage, decision, history, head and repair counts deliberately retain the failure.
        runner.save();
        return s;
    } finally { lock.close(); }
}

// Caller must hold lockRepository throughout claiming and running a lifecycle.
export function claimRepository(root, runner) {
    const common = path.resolve(root, git(root, 'rev-parse', '--git-common-dir'));
    const activeFile = path.join(common, 'designmode-orchestrator-active.json');
    if (fs.existsSync(activeFile)) {
        const active = read(activeFile);
        if (active.stateFile !== runner.stateFile && (!fs.existsSync(active.stateFile) || !terminal(read(active.stateFile).stage) || read(active.stateFile).pending)) {
            throw new Error(`HUMAN_REQUIRED: another lifecycle owns this repository: ${active.taskId}`);
        }
    }
    atomic(activeFile, { taskId: runner.id, stateFile: runner.stateFile, startedAt: now() });
}

async function main() {
    if (process.argv[2] === 'reconcile') {
        if (process.argv.length !== 5) throw new Error('Usage: node .agent/orchestrator/run.mjs reconcile <approved-task.md> <human-request.json>');
        const root = git(process.cwd(), 'rev-parse', '--show-toplevel');
        const state = await reconcile(root, process.argv[3], read(process.argv[4]));
        console.log(JSON.stringify({ taskId: state.taskId, stage: state.stage, reconciliation: state.reconciliation.outcome }, null, 2));
        return;
    }
    if (process.argv.length !== 3) throw new Error('Usage: node .agent/orchestrator/run.mjs .agent/tasks/approved/<task-id>.md');
    const root = git(process.cwd(), 'rev-parse', '--show-toplevel');
    const lock = await lockRepository(root);
    try {
        const runner = new Orchestrator(root, process.argv[2]);
        // Shared across worktrees: no second lifecycle while another task is nonterminal.
        claimRepository(root, runner);
        const state = runner.run();
        console.log(JSON.stringify({ taskId: state.taskId, stage: state.stage, humanRequired: state.humanRequired, decision: state.decision, stateFile: runner.stateFile }, null, 2));
        process.exitCode = state.stage === 'READY_FOR_HUMAN_REVIEW' ? 0 : 2;
    } finally { lock.close(); }
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    main().catch(error => { console.error(`BLOCKED\nHUMAN_REQUIRED\n${error.message}`); process.exitCode = 2; });
}
