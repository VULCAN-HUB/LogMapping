const fs = require('fs'), vm = require('vm'), path = require('path'), assert = require('assert/strict');
const script = fs.readFileSync(path.join(__dirname, '../LogMapping/resources/about.js'), 'utf8');
let passed = 0;
async function test(name, fn) { await fn(); console.log('PASS ' + name); passed++; }
function context() {
  const nodes = {}, calls = [];
  const c = { document: { getElementById(id) { return nodes[id] ??= { textContent: '', style: {}, hidden: false, disabled: false }; } },
    chrome: { webview: { addEventListener(type, fn) { c.receive = fn; } } },
    _closingForExit: false, _catalogLoading: false, _mutations: 0, _jobs: 0, _scanProgressCb: null,
    confirm() { calls.push('confirm'); return false; },
    async callNative(type, args) { calls.push({ type, args }); return { status: state('idle') }; },
    async fetch(name) { return { ok: true, async text() { return name + ' SIL Open Font License'; } }; }
  };
  c.window = c; vm.createContext(c); vm.runInContext(script, c);
  return { c, nodes, calls };
}
function state(State, extra = {}) { return { State, Message: State, CurrentVersion: '0.2.5-beta.1', Channel: 'beta', Received: 2, Total: 8, CanOpen: State === 'ready', ...extra }; }
(async () => {
  await test('all states render controls and unsupported stays distinct', async () => {
    const { c, nodes } = context();
    for (const phase of ['idle', 'checking', 'current', 'available', 'downloading', 'ready', 'installing', 'error', 'unavailable']) {
      c.renderUpdate(state(phase));
      assert.equal(nodes['update-download'].hidden, phase !== 'available');
      assert.equal(nodes['update-cancel'].hidden, phase !== 'downloading');
      assert.equal(nodes['update-open'].hidden, phase !== 'ready');
      assert.equal(nodes['update-check'].disabled, ['checking', 'downloading', 'installing'].includes(phase));
      assert.ok(nodes['update-status'].textContent.startsWith(phase));
    }
  });
  await test('runtime About version and platform, beta visible', async () => {
    const { c, nodes } = context(); c.callNative = async type => type === 'appInfo' ? { version: '9.8.7-beta.2', channel: 'beta', platform: 'Windows · X64' } : { status: state('idle') };
    await c.openAbout(); assert.match(nodes['about-version'].textContent, /9.8.7-beta.2.*PREVIEW/); assert.equal(nodes['about-platform'].textContent, 'Windows · X64');
  });
  await test('font license returns to About', async () => {
    const { c, nodes } = context(); await c.showFontLicenses();
    assert.equal(nodes['about-details'].hidden, true); assert.match(nodes['font-license-text'].textContent, /SIL Open Font License/);
    await c.showFontLicenses(); assert.equal(nodes['about-details'].hidden, false); assert.equal(nodes['font-licenses'].hidden, true);
  });
  await test('duplicate download prevented, cancel remains available', async () => {
    const { c, calls } = context(); let finish;
    c.callNative = (type) => { calls.push(type); return type === 'updateDownload' ? new Promise(r => finish = r) : Promise.resolve({ status: state('available') }); };
    c.renderUpdate(state('available')); const first = c.updateAction('updateDownload');
    await c.updateAction('updateDownload'); await c.updateAction('updateCancel');
    assert.deepEqual(calls, ['updateDownload', 'updateCancel']); finish({ status: state('available') }); await first;
  });
  await test('scan save export and close block file handoff before confirmation', async () => {
    for (const key of ['_closingForExit', '_catalogLoading', '_mutations', '_jobs', '_scanProgressCb']) {
      const { c, calls, nodes } = context(); c[key] = true; await c.openUpdateFolder();
      assert.equal(calls.length, 0); assert.match(nodes['about-error'].textContent, /진행 중/);
    }
  });
  await test('handoff cancellation does nothing; accepted handoff never launches exe', async () => {
    const { c, calls } = context(); await c.openUpdateFolder(); assert.deepEqual(calls, ['confirm']);
    c.confirm = () => true; await c.openUpdateFolder(); assert.equal(calls[1].type, 'updateOpenFolder');
  });
  await test('native failure leaves UI usable and retry works', async () => {
    const { c, nodes } = context(); c.renderUpdate(state('idle')); c.callNative = async () => { throw Error('offline'); };
    await c.updateAction('updateCheck'); assert.equal(nodes['update-check'].disabled, false); assert.match(nodes['about-error'].textContent, /offline/);
    c.callNative = async () => ({ status: state('current') }); await c.updateAction('updateCheck'); assert.equal(nodes['about-error'].textContent, ''); assert.equal(nodes['update-status'].textContent, 'current');
  });
  await test('remote status text is never interpreted as HTML', async () => {
    const { c, nodes } = context(); c.receive({ data: { type: 'updateState', data: state('error', { Message: '<img onerror=alert(1)>' }) } });
    assert.equal(nodes['update-status'].textContent, '<img onerror=alert(1)>'); assert.equal(nodes['update-status'].innerHTML, undefined);
  });
  await test('restart button dispatches only when ready and work is idle', async () => {
    const { c, calls, nodes } = context(); c.renderUpdate(state('ready'));
    assert.equal(nodes['update-apply'].hidden, false);
    c._jobs=1;await c.applyUpdate();assert.equal(calls.length,0);
    c._jobs=0;await c.applyUpdate();assert.equal(calls[0].type,'updateApply');
    c.renderUpdate(state('installing'));assert.equal(c._closingForExit,true);assert.equal(nodes['update-apply'].hidden,true);
  });
  console.log(`Passed ${passed}; failed 0`);
})().catch(e => { console.error(e); process.exitCode = 1; });
