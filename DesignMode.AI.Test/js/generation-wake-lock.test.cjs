const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname,
    '../../DesignMode.AI.Client/wwwroot/js/generation-unload-guard.js'), 'utf8');
const flush = () => new Promise(resolve => setImmediate(resolve));

function sentinel() {
    const listeners = new Set();
    return {
        released: false,
        releaseCalls: 0,
        addEventListener(name, callback) { assert.equal(name, 'release'); listeners.add(callback); },
        async release() {
            this.releaseCalls++;
            this.released = true;
            for (const callback of listeners) callback();
            listeners.clear();
        }
    };
}

function setup({ request, supported = true, secure = true, visible = true } = {}) {
    const visibilityListeners = new Set();
    const unloadListeners = new Set();
    const locks = [];
    let requests = 0;
    const document = {
        visibilityState: visible ? 'visible' : 'hidden',
        addEventListener(name, callback) { assert.equal(name, 'visibilitychange'); visibilityListeners.add(callback); },
        removeEventListener(name, callback) { assert.equal(name, 'visibilitychange'); visibilityListeners.delete(callback); }
    };
    const window = {
        isSecureContext: secure,
        addEventListener(name, callback) { assert.equal(name, 'beforeunload'); unloadListeners.add(callback); },
        removeEventListener(name, callback) { assert.equal(name, 'beforeunload'); unloadListeners.delete(callback); }
    };
    const navigator = supported ? { wakeLock: { request(type) {
        assert.equal(type, 'screen');
        requests++;
        if (request) return request();
        const lock = sentinel();
        locks.push(lock);
        return Promise.resolve(lock);
    } } } : {};
    vm.runInNewContext(source, { window, document, navigator });
    return {
        guard: window.designModeGenerationGuard, locks, visibilityListeners, unloadListeners,
        get requests() { return requests; },
        visibility(value) {
            document.visibilityState = value;
            for (const callback of visibilityListeners) callback();
        }
    };
}

test('active modal work acquires one lock; finishing/closing releases it and removes listeners', async () => {
    const env = setup();
    env.guard.setActive(false);
    assert.equal(env.requests, 0);
    env.guard.setActive(true);
    env.guard.setActive(true);
    await flush();
    assert.equal(env.requests, 1);
    assert.equal(env.locks[0].released, false);
    env.guard.setActive(false);
    env.guard.setActive(false);
    await flush();
    assert.equal(env.locks[0].releaseCalls, 1);
    assert.equal(env.visibilityListeners.size, 0);
    assert.equal(env.unloadListeners.size, 0);
    env.visibility('visible');
    assert.equal(env.requests, 1);
});

test('backgrounding releases and returning reacquires only while generation remains active', async () => {
    const env = setup();
    env.guard.setActive(true);
    await flush();
    env.visibility('hidden');
    await flush();
    assert.equal(env.locks[0].released, true);
    env.visibility('visible');
    await flush();
    assert.equal(env.requests, 2);
    env.guard.setActive(false);
    await flush();
    assert.equal(env.locks[1].released, true);
});

test('generation starting while hidden waits until visible', async () => {
    const env = setup({ visible: false });
    env.guard.setActive(true);
    assert.equal(env.requests, 0);
    env.visibility('visible');
    await flush();
    assert.equal(env.requests, 1);
    env.guard.setActive(false);
});

test('request resolving after modal close releases its stale lock', async () => {
    let resolve;
    const env = setup({ request: () => new Promise(done => { resolve = done; }) });
    env.guard.setActive(true);
    env.guard.setActive(false);
    const lock = sentinel();
    resolve(lock);
    await flush();
    assert.equal(lock.released, true);
    assert.equal(env.requests, 1);
});

test('close/reopen during a pending request releases the old lock and acquires a fresh one', async () => {
    const pending = [];
    const env = setup({ request: () => new Promise(resolve => pending.push(resolve)) });
    env.guard.setActive(true);
    env.guard.setActive(false);
    env.guard.setActive(true);
    const stale = sentinel();
    pending[0](stale);
    await flush();
    assert.equal(stale.released, true);
    assert.equal(env.requests, 2);
    const current = sentinel();
    pending[1](current);
    await flush();
    assert.equal(current.released, false);
    env.guard.setActive(false);
    await flush();
    assert.equal(current.released, true);
});

test('denied requests do not fail generation or continuously retry', async () => {
    const env = setup({ request: () => Promise.reject(new Error('NotAllowedError')) });
    assert.doesNotThrow(() => env.guard.setActive(true));
    await flush();
    assert.equal(env.requests, 1);
    assert.equal(env.unloadListeners.size, 1);
    env.guard.setActive(true);
    await flush();
    assert.equal(env.requests, 1);
    env.visibility('hidden');
    env.visibility('visible');
    await flush();
    assert.equal(env.requests, 2);
    env.guard.setActive(false);
});

test('unsupported browsers and insecure pages keep the unload guard working', async () => {
    for (const options of [{ supported: false }, { secure: false }]) {
        const env = setup(options);
        env.guard.setActive(true);
        await flush();
        assert.equal(env.requests, 0);
        assert.equal(env.visibilityListeners.size, 0);
        assert.equal(env.unloadListeners.size, 1);
        env.guard.setActive(false);
        assert.equal(env.unloadListeners.size, 0);
    }
});

test('system release does not trigger a retry loop and a later visibility change can recover', async () => {
    const env = setup();
    env.guard.setActive(true);
    await flush();
    await env.locks[0].release();
    await flush();
    assert.equal(env.requests, 1);
    env.visibility('hidden');
    env.visibility('visible');
    await flush();
    assert.equal(env.requests, 2);
    env.guard.setActive(false);
});
