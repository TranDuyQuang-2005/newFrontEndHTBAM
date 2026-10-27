// Behavioral checks for overlay keyboard/focus state; these do not render a browser.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { test } = require('node:test');
const ts = require('../apps/web/node_modules/typescript');
const source = fs.readFileSync(path.join(__dirname, '../apps/web/src/app/shared/overlay-focus.ts'), 'utf8');
const compiled = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS },
}).outputText;
const sandbox = { exports: {}, setTimeout, clearTimeout };
vm.runInNewContext(compiled, sandbox);
const { OverlayFocus } = sandbox.exports;

class TestDocument extends EventTarget {
  body = { style: { overflow: 'auto' } };
  activeElement = null;
}

class TestElement {
  isConnected = true;
  tabIndex = 0;
  visible = true;
  hiddenAncestor = false;
  children = [];
  constructor(document) { this.document = document; }
  focus() { this.document.activeElement = this; }
  closest() { return this.hiddenAncestor ? this : null; }
  getClientRects() { return this.visible ? [{}] : []; }
  querySelectorAll() { return this.children; }
}

function fixture() {
  const document = new TestDocument();
  const trigger = new TestElement(document);
  trigger.focus();
  const dialog = new TestElement(document);
  dialog.children = [new TestElement(document), new TestElement(document)];
  return { document, trigger, dialog, overlay: new OverlayFocus(document) };
}

function key(document, value, shiftKey = false) {
  const event = new Event('keydown', { cancelable: true });
  Object.assign(event, { key: value, shiftKey });
  document.dispatchEvent(event);
  return event;
}

const nextTick = () => new Promise((resolve) => setTimeout(resolve, 5));

test('opening focuses the preferred input and closing restores the trigger and scroll', async () => {
  const { document, trigger, dialog, overlay } = fixture();
  overlay.open(dialog, () => {}, dialog.children[1]);
  assert.equal(document.body.style.overflow, 'hidden');
  await nextTick();
  assert.equal(document.activeElement, dialog.children[1]);
  overlay.close();
  await nextTick();
  assert.equal(document.activeElement, trigger);
  assert.equal(document.body.style.overflow, 'auto');
});

test('Tab wraps within visible controls and recovers focus from outside', async () => {
  const { document, dialog, overlay, trigger } = fixture();
  const hidden = new TestElement(document);
  hidden.visible = false;
  const negativeTabIndex = new TestElement(document);
  negativeTabIndex.tabIndex = -1;
  dialog.children.push(hidden, negativeTabIndex);
  overlay.open(dialog, () => {});
  await nextTick();
  assert.equal(key(document, 'Tab', true).defaultPrevented, true);
  assert.equal(document.activeElement, dialog.children[1]);
  key(document, 'Tab');
  assert.equal(document.activeElement, dialog.children[0]);
  trigger.focus();
  key(document, 'Tab');
  assert.equal(document.activeElement, dialog.children[0]);
  overlay.close(false);
});

test('a dialog without controls keeps Tab focus on its container', async () => {
  const { document, dialog, overlay } = fixture();
  dialog.children = [];
  overlay.open(dialog, () => {});
  await nextTick();
  assert.equal(document.activeElement, dialog);
  assert.equal(key(document, 'Tab').defaultPrevented, true);
  assert.equal(document.activeElement, dialog);
  overlay.close(false);
});

test('a hidden preferred input does not receive initial focus', async () => {
  const { document, dialog, overlay } = fixture();
  dialog.children[0].visible = false;
  overlay.open(dialog, () => {}, dialog.children[0]);
  await nextTick();
  assert.equal(document.activeElement, dialog.children[1]);
  overlay.close(false);
});

test('Escape reaches only the top overlay and nested scroll locks remain active', async () => {
  const { document, dialog, overlay } = fixture();
  let outerClose = 0;
  let innerClose = 0;
  overlay.open(dialog, () => { outerClose++; overlay.close(); });
  await nextTick();
  const inner = new OverlayFocus(document);
  const innerDialog = new TestElement(document);
  inner.open(innerDialog, () => { innerClose++; inner.close(); });
  await nextTick();
  key(document, 'Escape');
  assert.equal(innerClose, 1);
  assert.equal(outerClose, 0);
  assert.equal(document.body.style.overflow, 'hidden');
  await nextTick();
  assert.equal(document.activeElement, dialog.children[0]);
  key(document, 'Escape');
  assert.equal(outerClose, 1);
  assert.equal(document.body.style.overflow, 'auto');
});

test('a busy close callback may keep its dialog open without losing the scroll lock', () => {
  const { document, dialog, overlay } = fixture();
  let requests = 0;
  overlay.open(dialog, () => requests++);
  key(document, 'Escape');
  assert.equal(requests, 1);
  assert.equal(document.body.style.overflow, 'hidden');
  overlay.close(false);
});

test('destroying before initial focus cancels pending work and removes key listeners', async () => {
  const { document, trigger, dialog, overlay } = fixture();
  let requests = 0;
  overlay.open(dialog, () => requests++);
  overlay.open(dialog, () => requests++);
  overlay.close(false);
  await nextTick();
  key(document, 'Escape');
  assert.equal(requests, 0);
  assert.equal(document.activeElement, trigger);
  assert.equal(document.body.style.overflow, 'auto');
});

test('closing an overlay does not focus a disconnected or hidden trigger', async () => {
  for (const property of ['isConnected', 'hiddenAncestor']) {
    const { document, trigger, dialog, overlay } = fixture();
    overlay.open(dialog, () => {});
    await nextTick();
    trigger[property] = property === 'hiddenAncestor';
    overlay.close();
    await nextTick();
    assert.notEqual(document.activeElement, trigger);
  }
});
