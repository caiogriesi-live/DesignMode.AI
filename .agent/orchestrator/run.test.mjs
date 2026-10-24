import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { Orchestrator, transition, lockRepository, codexArgs, reconcile, claimRepository } from './run.mjs';

function fixture(t) {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'designmode-orchestrator-'));
    t.after(() => {
        const relative = path.relative(path.resolve(os.tmpdir()), path.resolve(root));
        assert.ok(!relative.startsWith('..') && !path.isAbsolute(relative) && relative.startsWith('designmode-orchestrator-'));
        fs.rmSync(root, { recursive: true, force: true });
    });
    const write = (file, content) => { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), content); };
    const git = (...args) => {
        const r = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
        assert.equal(r.status, 0, r.stderr); return r.stdout.trim();
    };
    git('init', '-b', 'master-caio');
    write('.gitignore', '.agent/artifacts/\n.agent/results/\n');
    write('.agent/tasks/approved/demo.md', 'STATUS: APPROVED\nDo the task.\n');
    for (const role of ['developer', 'qa', 'triage', 'repair', 'regression']) write(`.agent/prompts/${role}.md`, `Role: ${role}`);
    write('application.txt', 'original');
    git('add', '.'); git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-m', 'fixture');
    const task = '.agent/tasks/approved/demo.md';
    function result(p, outcome) {
        const prefix = '.agent/artifacts/orchestrator/demo/qa-contract/';
        if (p.stage === 'QA' && !fs.existsSync(path.join(root, prefix))) {
            write(prefix + 'contract.md', 'Frozen exact test commands'); write(prefix + 'acceptance.mjs', '// Independent contract fixture');
        }
        write(p.report, `STATUS: ${outcome}\n`);
        write(p.result, JSON.stringify({ invocationId: p.id, taskId: 'demo', stage: p.stage, branch: p.branch,
            head: git('rev-parse', 'HEAD'), report: p.report, outcome, reason: outcome,
            contractFiles: p.stage === 'QA' ? [prefix + 'contract.md', prefix + 'acceptance.mjs'] : [] }));
    }
    return { root, write, git, task, result };
}

test('launcher retains the reviewed workspace-write preset and fresh structured sessions', () => {
    const root = path.resolve('workspace with spaces');
    assert.deepEqual(codexArgs(root, { schema: 'role/schema.json', result: 'role/result.json' }), [
        'exec', '--cd', root, '--approve-for-me', '--json',
        '--output-schema', path.join(root, 'role/schema.json'),
        '--output-last-message', path.join(root, 'role/result.json'), '-'
    ]);
});

test('installed Codex accepts the actual launcher arguments before reading a prompt', t => {
    const f = fixture(t);
    const help = spawnSync('codex', ['exec', '--help'], { encoding: 'utf8', timeout: 10000, windowsHide: true });
    assert.ifError(help.error);
    assert.equal(help.status, 0, help.stderr);
    assert.match(help.stdout, /--approve-for-me\s+Route approval requests through automatic review using the workspace-write sandbox/);
    const orchestrator = new Orchestrator(f.root, f.task);
    orchestrator.initialize();
    const p = orchestrator.prepare();
    // Empty stdin exits before a model session or tool execution.
    // Unlike --help, this exercises the parser's conflicting-argument checks.
    for (const extra of [[], ['--ignore-user-config', '--ephemeral']]) {
        const args = codexArgs(f.root, p);
        args.splice(args.length - 1, 0, ...extra);
        const result = spawnSync('codex', args, {
            cwd: f.root, input: '', encoding: 'utf8', timeout: 10000, windowsHide: true
        });
        assert.ifError(result.error);
        assert.equal(result.status, 1, result.stderr);
        assert.match(result.stderr, /No prompt provided via stdin/);
        assert.equal(fs.existsSync(path.join(f.root, p.result)), false);
    }
});

test('all lifecycle transitions fail closed for unsafe outcomes', () => {
    assert.equal(transition('DEVELOP', 'READY_FOR_QA', 0), 'QA');
    assert.equal(transition('QA', 'PASS', 0), 'REGRESSION');
    assert.equal(transition('QA', 'FAIL', 0), 'TRIAGE');
    assert.equal(transition('TRIAGE', 'BRANCH_REGRESSION', 0), 'REPAIR');
    for (const outcome of ['PRE_EXISTING', 'TEST_INFRASTRUCTURE', 'FLAKY_OR_INCONCLUSIVE', 'BLOCKED']) assert.equal(transition('TRIAGE', outcome, 0), 'BLOCKED');
    assert.equal(transition('QA', 'FAIL', 3), 'BLOCKED');
    assert.equal(transition('TRIAGE', 'BRANCH_REGRESSION', 3), 'BLOCKED');
    assert.equal(transition('REGRESSION', 'FAIL', 0), 'BLOCKED');
    assert.throws(() => transition('QA', 'looks good', 0));
});

test('recovery uses separate roles, frozen QA contract and successful terminal state', t => {
    const f = fixture(t), seen = [];
    const expected = ['READY_FOR_QA', 'FAIL', 'BRANCH_REGRESSION', 'READY_FOR_QA', 'PASS', 'READY_FOR_HUMAN_REVIEW'];
    const s = new Orchestrator(f.root, f.task, (_, p) => { seen.push(p.stage); f.result(p, expected.shift()); }).run();
    assert.deepEqual(seen, ['DEVELOP', 'QA', 'TRIAGE', 'REPAIR', 'QA', 'REGRESSION']);
    assert.equal(s.stage, 'READY_FOR_HUMAN_REVIEW'); assert.equal(s.completedRepairs, 1);
    assert.equal(s.history.length, 6); assert.equal(Object.keys(s.qaContract).length, 2);
    assert.equal(new Orchestrator(f.root, f.task, () => assert.fail('terminal rerun')).run().stage, s.stage);
});

test('repair exhaustion stops after exactly three completed repairs', t => {
    const f = fixture(t), seen = [];
    const s = new Orchestrator(f.root, f.task, (_, p) => {
        seen.push(p.stage); f.result(p, { DEVELOP: 'READY_FOR_QA', QA: 'FAIL', TRIAGE: 'BRANCH_REGRESSION', REPAIR: 'READY_FOR_QA' }[p.stage]);
    }).run();
    assert.equal(s.stage, 'BLOCKED'); assert.equal(s.humanRequired, true);
    assert.equal(s.completedRepairs, 3); assert.equal(seen.filter(x => x === 'REPAIR').length, 3);
    assert.equal(seen.at(-1), 'QA');
});

test('restart consumes completed invocation without relaunch', t => {
    const f = fixture(t), first = new Orchestrator(f.root, f.task);
    first.initialize(); const p = first.prepare(); f.result(p, 'READY_FOR_QA');
    const seen = [];
    const s = new Orchestrator(f.root, f.task, (_, next) => { seen.push(next.stage); f.result(next, next.stage === 'QA' ? 'PASS' : 'READY_FOR_HUMAN_REVIEW'); }).run();
    assert.deepEqual(seen, ['QA', 'REGRESSION']); assert.equal(s.history.length, 3);
});

test('incomplete repair blocks without counting or relaunching it', t => {
    const f = fixture(t), first = new Orchestrator(f.root, f.task);
    first.initialize();
    for (const outcome of ['READY_FOR_QA', 'FAIL', 'BRANCH_REGRESSION']) {
        const p = first.prepare(); f.result(p, outcome); first.consume(p);
    }
    first.prepare();
    const s = new Orchestrator(f.root, f.task, () => assert.fail('must not relaunch')).run();
    assert.equal(s.stage, 'BLOCKED'); assert.equal(s.completedRepairs, 0); assert.equal(s.pending.stage, 'REPAIR');
});

test('restart consumes completed repair exactly once', t => {
    const f = fixture(t), first = new Orchestrator(f.root, f.task);
    first.initialize();
    for (const outcome of ['READY_FOR_QA', 'FAIL', 'BRANCH_REGRESSION']) {
        const p = first.prepare(); f.result(p, outcome); first.consume(p);
    }
    const p = first.prepare(); f.result(p, 'READY_FOR_QA');
    const s = new Orchestrator(f.root, f.task, (_, next) => f.result(next, next.stage === 'QA' ? 'PASS' : 'READY_FOR_HUMAN_REVIEW')).run();
    assert.equal(s.completedRepairs, 1); assert.equal(s.stage, 'READY_FOR_HUMAN_REVIEW');
});

for (const mutation of ['task', 'contract', 'source', 'identity', 'missing-report']) {
    test(`rejects ${mutation} mutation and retains evidence`, t => {
        const f = fixture(t);
        const s = new Orchestrator(f.root, f.task, (_, p) => {
            f.result(p, p.stage === 'DEVELOP' ? 'READY_FOR_QA' : 'PASS');
            if (p.stage !== 'QA') return;
            if (mutation === 'task') f.write(f.task, 'STATUS: APPROVED\nWeakened');
            if (mutation === 'source') f.write('application.txt', 'unauthorized');
            if (mutation === 'identity') { const file = path.join(f.root, p.result), r = JSON.parse(fs.readFileSync(file)); r.invocationId = 'stale'; fs.writeFileSync(file, JSON.stringify(r)); }
            if (mutation === 'missing-report') fs.unlinkSync(path.join(f.root, p.report));
            if (mutation === 'contract') {
                // Alter after the first QA is consumed, when the next role starts.
                return;
            }
        });
        if (mutation === 'contract') {
            s.runner = (_, p) => {
                f.result(p, p.stage === 'DEVELOP' ? 'READY_FOR_QA' : p.stage === 'QA' ? 'PASS' : 'READY_FOR_HUMAN_REVIEW');
                if (p.stage === 'REGRESSION') f.write('.agent/artifacts/orchestrator/demo/qa-contract/acceptance.mjs', 'weakened');
            };
        }
        const state = s.run(); assert.equal(state.stage, 'BLOCKED'); assert.equal(state.humanRequired, true); assert.ok(state.pending);
    });
}

test('repository lock rejects concurrent runners and releases on close', async t => {
    const f = fixture(t), lock = await lockRepository(f.root);
    try { await assert.rejects(lockRepository(f.root)); } finally { await new Promise(resolve => lock.close(resolve)); }
    const again = await lockRepository(f.root); await new Promise(resolve => again.close(resolve));
});

test('prior independent report cannot be overwritten by a later role', t => {
    const f = fixture(t);
    const s = new Orchestrator(f.root, f.task, (_, p) => {
        f.result(p, p.stage === 'DEVELOP' ? 'READY_FOR_QA' : 'PASS');
        if (p.stage === 'QA') f.write('.agent/results/demo/001-developer.md', 'changed evidence');
    }).run();
    assert.equal(s.stage, 'BLOCKED'); assert.match(s.decision, /evidence changed/);
});

test('successful developer must commit source changes', t => {
    const f = fixture(t);
    const s = new Orchestrator(f.root, f.task, (_, p) => {
        f.write('application.txt', 'uncommitted'); f.result(p, 'READY_FOR_QA');
    }).run();
    assert.equal(s.stage, 'BLOCKED'); assert.match(s.decision, /uncommitted/);
});

test('dirty initial checkout is preserved and no role launches', t => {
    const f = fixture(t); f.write('application.txt', 'user work');
    assert.throws(() => new Orchestrator(f.root, f.task, () => assert.fail('launched')).run(), /clean checkout/);
    assert.equal(fs.readFileSync(path.join(f.root, 'application.txt'), 'utf8'), 'user work');
});

function blockedFixture(t, repair = false) {
    const f = fixture(t), runner = new Orchestrator(f.root, f.task);
    runner.initialize();
    if (repair) for (const outcome of ['READY_FOR_QA', 'FAIL', 'BRANCH_REGRESSION']) {
        const p = runner.prepare(); f.result(p, outcome); runner.consume(p);
    }
    const p = runner.prepare();
    f.write(p.log, 'Original child failure');
    runner.block('Ambiguous child failure');
    const active = '.git/designmode-orchestrator-active.json';
    f.write(active, JSON.stringify({ taskId: 'demo', stateFile: runner.stateFile }));
    const request = { invocationId: p.id, disposition: 'ABANDON_UNRESOLVED', operator: 'Human tester',
        reason: 'Abandon failed invocation after evidence review', childExited: true,
        exitEvidence: 'Fixture child never launched; no live process exists' };
    return { ...f, runner, p, request, active };
}

for (const repair of [false, true]) test(`human reconciliation preserves failure and evidence (repair=${repair})`, async t => {
    const f = blockedFixture(t, repair);
    // Even an existing result is preserved, never consumed as success.
    if (repair) f.result(f.p, 'READY_FOR_QA');
    const before = JSON.parse(fs.readFileSync(f.runner.stateFile));
    const files = [f.p.prompt, f.p.schema, f.p.log, ...(repair ? [f.p.result, f.p.report] : [])];
    const bytes = files.map(file => fs.readFileSync(path.join(f.root, file)));
    const s = await reconcile(f.root, f.task, f.request);
    assert.deepEqual(s.reconciliation.originalState, before);
    assert.equal(s.pending, null); assert.equal(s.stage, 'BLOCKED'); assert.equal(s.humanRequired, true);
    assert.equal(s.completedRepairs, 0); assert.deepEqual(s.history, before.history);
    assert.equal(s.decision, before.decision);
    assert.equal(s.reconciliation.outcome, 'HUMAN_ABANDONED_UNRESOLVED');
    assert.deepEqual(s.reconciliation.request, f.request);
    assert.ok(s.reconciliation.reconciledAt); assert.ok(s.reconciliation.evidence[f.p.log]);
    files.forEach((file, i) => assert.deepEqual(fs.readFileSync(path.join(f.root, file)), bytes[i]));
    assert.equal(fs.existsSync(path.join(f.root, f.p.result)), repair);
    const saved = fs.readFileSync(f.runner.stateFile);
    await reconcile(f.root, f.task, f.request);
    assert.deepEqual(fs.readFileSync(f.runner.stateFile), saved);
    assert.equal(new Orchestrator(f.root, f.task, () => assert.fail('must not restart')).run().stage, 'BLOCKED');
    await assert.rejects(reconcile(f.root, f.task, { ...f.request, reason: 'Different disposition' }), /Conflicting/);
});

for (const invalid of ['invocation', 'task', 'exit', 'reason', 'disposition', 'owner', 'stage', 'branch', 'contract', 'lock']) {
    test(`reconciliation refuses unsafe ${invalid} without changing state`, async t => {
        const f = blockedFixture(t);
        let task = f.task, lock;
        if (invalid === 'invocation') f.request.invocationId = '999-repair';
        if (invalid === 'task') task = '.agent/tasks/approved/other.md';
        if (invalid === 'exit') f.request.childExited = false;
        if (invalid === 'reason') f.request.reason = ' ';
        if (invalid === 'disposition') f.request.disposition = 'PASS';
        if (invalid === 'owner') f.write(f.active, JSON.stringify({ taskId: 'other', stateFile: f.runner.stateFile }));
        if (invalid === 'stage') { f.runner.state.stage = 'REPAIR'; f.runner.save(); }
        if (invalid === 'branch') f.git('switch', 'master-caio');
        if (invalid === 'contract') f.write(f.task, 'Changed task');
        if (invalid === 'lock') lock = await lockRepository(f.root);
        const before = fs.readFileSync(f.runner.stateFile);
        try { await assert.rejects(reconcile(f.root, task, f.request)); }
        finally { if (lock) await new Promise(resolve => lock.close(resolve)); }
        assert.deepEqual(fs.readFileSync(f.runner.stateFile), before);
    });
}

test('CLI ownership blocks before reconciliation and permits a valid next lifecycle afterward', async t => {
    const f = blockedFixture(t);
    const script = path.resolve('.agent/orchestrator/run.mjs');
    const cli = (...args) => spawnSync(process.execPath, [script, ...args], { cwd: f.root, encoding: 'utf8' });
    // A separately approved fixture lifecycle terminates without a real child.
    const other = '.agent/tasks/approved/other.md';
    f.write(other, 'STATUS: APPROVED\n');
    const next = new Orchestrator(f.root, other);
    f.write(path.relative(f.root, next.stateFile), JSON.stringify({ taskId: 'other', stage: 'READY_FOR_HUMAN_REVIEW', pending: null }));
    assert.match(cli(other).stderr, /another lifecycle owns/);
    f.write('human-request.json', JSON.stringify(f.request));
    const result = cli('reconcile', f.task, 'human-request.json');
    assert.equal(result.status, 0, result.stderr);
    assert.equal(cli(other).status, 0);
    assert.equal(JSON.parse(fs.readFileSync(path.join(f.root, f.active))).taskId, 'other');
    // Exact retry remains harmless after ownership moved.
    assert.equal(cli('reconcile', f.task, 'human-request.json').status, 0);
});

test('a fresh approved lifecycle can actually start after reconciliation', async t => {
    const f = blockedFixture(t);
    await reconcile(f.root, f.task, f.request);
    f.git('switch', 'master-caio');
    const task = '.agent/tasks/approved/next.md';
    f.write(task, 'STATUS: APPROVED\nNew approved fixture task');
    f.git('add', task); f.git('-c', 'user.name=Test', '-c', 'user.email=test@example.invalid', 'commit', '-m', 'approve fixture');
    let launched = false;
    const runner = new Orchestrator(f.root, task, () => { launched = true; throw new Error('Fixture child stopped'); });
    const lock = await lockRepository(f.root);
    try {
        claimRepository(f.root, runner);
        const s = runner.run();
        assert.equal(launched, true); assert.equal(s.pending.id, '001-developer');
        assert.equal(s.taskId, 'next');
    } finally { await new Promise(resolve => lock.close(resolve)); }
});

test('failed atomic reconciliation leaves ownership blocked and can be retried', async t => {
    const f = blockedFixture(t);
    const before = fs.readFileSync(f.runner.stateFile);
    const rename = fs.renameSync;
    fs.renameSync = (from, to) => {
        if (to === f.runner.stateFile) throw new Error('Simulated interruption before replacement');
        return rename(from, to);
    };
    try { await assert.rejects(reconcile(f.root, f.task, f.request), /Simulated interruption/); }
    finally { fs.renameSync = rename; }
    assert.deepEqual(fs.readFileSync(f.runner.stateFile), before);
    const lock = await lockRepository(f.root);
    try { assert.throws(() => claimRepository(f.root, new Orchestrator(f.root, '.agent/tasks/approved/other.md')), /another lifecycle owns/); }
    finally { await new Promise(resolve => lock.close(resolve)); }
    const s = await reconcile(f.root, f.task, f.request);
    assert.equal(s.pending, null); assert.equal(s.completedRepairs, 0);
    assert.deepEqual(s.reconciliation.originalState, JSON.parse(before));
});
