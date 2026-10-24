const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
(async () => {
  for (const page of ['DesignerIntro', 'DesignerIntro2']) {
    const source = fs.readFileSync(`DesignMode.AI.Client/Pages/${page}.razor`, 'utf8');
    const start = source.indexOf('let checkingAccountSession = false;');
    const end = source.indexOf('isEmailModalOpen = true;', start);
    assert.ok(start > 0 && end > start);
    const gate = source.slice(start, end) + 'prompted++; }';
    for (const mode of ['signed-in', 'anonymous', 'error']) {
      const scope = { isEmailModalOpen: false, isBrandModalOpen: false, isEmailResolved: false,
        prompted: 0, resumed: 0, failed: 0,
        dotNetHelper: { async invokeMethodAsync(method) {
          assert.equal(method, 'GetWorkspaceSessionEmail');
          if (mode === 'error') throw Error('unavailable');
          return mode === 'signed-in' ? 'person@example.com' : null;
        } },
        async completeAuthenticatedAccount(email) { assert.equal(email, 'person@example.com'); scope.resumed++; scope.isEmailResolved = true; },
        window: { designerIntroGenerationFailed() { scope.failed++; } }
      };
      vm.createContext(scope);
      vm.runInContext(gate, scope);
      await vm.runInContext('Promise.all([showEmailModal(), showEmailModal()])', scope);
      assert.equal(scope.prompted, mode === 'anonymous' ? 1 : 0);
      assert.equal(scope.resumed, mode === 'signed-in' ? 1 : 0);
      assert.equal(scope.failed, mode === 'error' ? 1 : 0);
    }
  }
  console.log('DesignerIntro session gates: PASS');
})().catch(error => { console.error(error); process.exitCode = 1; });
