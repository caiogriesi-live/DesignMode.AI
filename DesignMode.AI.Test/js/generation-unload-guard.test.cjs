const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('native unload handler is idempotent, uses browser text, and is removable', () => {
    const handlers = new Set();
    let added = 0;
    let removed = 0;
    const window = {
        addEventListener(name, handler) {
            assert.equal(name, 'beforeunload'); handlers.add(handler); added++;
        },
        removeEventListener(name, handler) {
            assert.equal(name, 'beforeunload'); handlers.delete(handler); removed++;
        }
    };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname,
        '../../DesignMode.AI.Client/wwwroot/js/generation-unload-guard.js'), 'utf8'), { window });
    const guard = window.designModeGenerationGuard;
    guard.setActive(false);
    assert.equal(handlers.size, 0);
    guard.setActive(true);
    guard.setActive(true);
    assert.equal(added, 1);
    let prevented = false;
    const event = { preventDefault() { prevented = true; } };
    [...handlers][0](event);
    assert.equal(prevented, true);
    assert.equal(event.returnValue, '');
    guard.setActive(false);
    guard.setActive(false);
    assert.equal(removed, 1);
    assert.equal(handlers.size, 0);
    guard.setActive(true);
    guard.setActive(false);
    assert.equal(handlers.size, 0);
});
