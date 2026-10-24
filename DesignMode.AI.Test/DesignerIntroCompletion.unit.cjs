const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
for (const page of ['DesignerIntro', 'DesignerIntro2']) {
  const source = fs.readFileSync(`DesignMode.AI.Client/Pages/${page}.razor`, 'utf8');
  const start = source.indexOf('function updateProgress(');
  const end = source.indexOf('function showContent()', start);
  const scope = { MAX_PROGRESS: 95, EASE: 'ease', isGenerationComplete: false,
    progressFill: { style: {} }, progressPercent: {}, progressLabel: {} };
  vm.createContext(scope);
  vm.runInContext(source.slice(start, end), scope);
  vm.runInContext('updateProgress(100)', scope);
  assert.equal(scope.progressPercent.textContent, '95%');
  scope.isGenerationComplete = true;
  vm.runInContext('updateProgress(100)', scope);
  assert.equal(scope.progressPercent.textContent, '100%');
  const navigation = source.slice(source.indexOf('private async Task TryNavigateWhenReadyAsync()'), source.indexOf('private static readonly byte[] Key'));
  assert.ok(navigation.includes('!_allInviteSavesCompleted'));
  assert.ok(navigation.includes('_inviteSaveFailed'));
  assert.ok(!navigation.includes('designerIntroImagesReady'), 'Decorative images must not block a saved workspace');
}
console.log('DesignerIntro completion progress and redirect prerequisites: PASS');
