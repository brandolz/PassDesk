'use strict';
// Pruebas de extremo a extremo: Chromium real + extensión real + conector real + intermediario real con una bóveda FICTICIA.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { createEnvironment, VAULT, SENTINELS, ROOT } = require('./harness');

const byUser = (name) => VAULT.find((v) => v.username.startsWith(name));
const U = (host, p = '/login', port = '') => `https://${host}${port}${p}`;

let env;
let closedEarly = false;

test.before(async () => {
  env = await createEnvironment();
});

test.after(async () => {
  if (!closedEarly) {
    await env.close();
  }
  env.server.close();
  env.cleanup();
});

test.beforeEach(async () => {
  await command('unlock', /STATE unlocked/);
  await command('confirm yes', /CONFIRM-MODE yes/);
  await command('subdomains off', /SUBDOMAINS off/);
});

test.afterEach(async () => {
  for (const page of env.context.pages()) {
    if (page.url() !== 'about:blank') {
      await page.close().catch(() => {});
    }
  }
});

async function command(text, pattern) {
  const mark = env.sandbox.mark();
  env.sandbox.send(text);
  const deadline = Date.now() + 10000;
  while (!env.sandbox.since(mark).some((l) => pattern.test(l))) {
    assert.ok(Date.now() < deadline, `sin respuesta a «${text}»`);
    await new Promise((r) => setTimeout(r, 25));
  }
}

async function open(url) {
  const page = await env.context.newPage();
  await page.goto(url, { waitUntil: 'load' });
  return page;
}

async function popupFor(page) {
  const tabId = await env.tabIdOf(page);
  assert.ok(tabId !== null, `pestaña no encontrada: ${page.url()}`);
  const popup = await env.openPopup(tabId);
  await popup.locator('#status').filter({ hasNotText: 'Buscando' }).waitFor();
  return popup;
}

async function enableSite(page) {
  const popup = await popupFor(page);
  const enable = popup.getByRole('button', { name: /Mostrar sugerencias/ });
  if (await enable.count()) {
    await enable.click();
    await popup.getByText(/Sugerencias activadas/).waitFor();
  }
  await popup.close();
  await page.reload({ waitUntil: 'load' });
}

// La sugerencia se dibuja en cuanto el campo se enfoca y se rellena cuando contesta PassDesk.
async function waitForSuggestionText(page, pattern, timeout = 8000) {
  const deadline = Date.now() + timeout;
  let text = '';
  while (Date.now() < deadline) {
    text = await suggestionText(page);
    if (pattern.test(text)) {
      return text;
    }
    await settle(100);
  }
  assert.fail(`la sugerencia no mostró ${pattern}; texto: ${text}`);
}

const settle = (ms = 400) => new Promise((r) => setTimeout(r, ms));

async function suggestionHost(page, timeout = 6000) {
  await page.waitForFunction(() => Boolean(document.querySelector('div[popover]')), null, { timeout });
}

async function hasSuggestion(target) {
  return target.evaluate(() => Boolean(document.querySelector('div[popover]')));
}

// Texto de la sugerencia (raíz sombra CERRADA: solo se llega con el protocolo de depuración, nunca desde la página).
async function suggestionText(page) {
  const cdp = await env.context.newCDPSession(page);
  try {
    const { root } = await cdp.send('DOM.getDocument', { depth: -1, pierce: true });
    const found = [];
    const walk = (node) => {
      for (const shadow of node.shadowRoots ?? []) {
        if (shadow.shadowRootType === 'closed') {
          found.push(shadow);
        }
      }
      for (const child of [...(node.children ?? []), ...(node.shadowRoots ?? []), ...(node.contentDocument ? [node.contentDocument] : [])]) {
        walk(child);
      }
    };
    walk(root);
    const texts = [];
    for (const shadow of found) {
      const { object } = await cdp.send('DOM.resolveNode', { backendNodeId: shadow.backendNodeId });
      const { result } = await cdp.send('Runtime.callFunctionOn', { objectId: object.objectId, functionDeclaration: 'function(){ return this.textContent }', returnByValue: true });
      texts.push(result.value);
    }
    return texts.join('\n');
  } finally {
    await cdp.detach();
  }
}

function frames(buffer) {
  const out = [];
  for (let offset = 0; offset + 4 <= buffer.length;) {
    const length = buffer.readUInt32LE(offset);
    out.push(JSON.parse(buffer.subarray(offset + 4, offset + 4 + length).toString('utf8')));
    offset += 4 + length;
  }
  return out;
}

function exchanges() {
  const bySlot = new Map();
  const dir = env.logDir;
  for (const file of fs.readdirSync(dir)) {
    const [slot, kind] = file.split('.');
    const entry = bySlot.get(slot) ?? { slot };
    const content = frames(fs.readFileSync(path.join(dir, file)));
    entry[kind === 'in' ? 'request' : 'reply'] = content[0];
    bySlot.set(slot, entry);
  }
  return [...bySlot.values()].sort((a, b) => (a.slot < b.slot ? -1 : 1));
}

const lastFill = () => exchanges().filter((e) => e.request?.type === 'fill').at(-1);
const fillsCount = () => exchanges().filter((e) => e.request?.type === 'fill' && e.reply?.ok).length;

async function chooseWithKeyboard(page, index = 0) {
  await settle(500); // la ficha no vale hasta pasada una breve espera, para evitar clics por sorpresa
  await page.keyboard.press('ArrowDown');
  for (let i = 0; i < index; i++) {
    await page.keyboard.press('ArrowDown');
  }
  await page.keyboard.press('Enter');
}

// ---------------------------------------------------------------------------------------------------------------------

test('ventana de la extensión: lista la cuenta, rellena al elegirla y la contraseña no sale de la ventana', async () => {
  const user = byUser('ana');
  const page = await open(U('uno.example.test'));
  const popup = await popupFor(page);
  const account = popup.locator('.account');
  assert.equal(await account.count(), 1);
  const text = await account.innerText();
  assert.match(text, /Ejemplo Uno/);
  assert.match(text, /ana@uno\.example\.test/);
  assert.ok(!(await popup.content()).includes(user.password), 'la ventana no debe contener la contraseña');
  await account.click();
  await popup.waitForEvent('close');
  assert.equal(await page.inputValue('#user'), user.username);
  assert.equal(await page.inputValue('#pass'), user.password);
  // La página no recibió nada por red: ningún envío ni petición con la contraseña.
  assert.ok(env.server.requests.every((r) => !r.body.includes(user.password) && !r.url.includes(user.password)));
});

test('sugerencia en la página: aparece al enfocar, se elige con el teclado y confirma el origen', async () => {
  const user = byUser('ana');
  const page = await open(U('uno.example.test'));
  await enableSite(page);
  await page.click('#user');
  await suggestionHost(page);
  const text = await waitForSuggestionText(page, /ana@uno\.example\.test/);
  assert.match(text, /PassDesk/);
  assert.match(text, /https:\/\/uno\.example\.test/);
  assert.match(text, /Ejemplo Uno/);
  assert.match(text, /ana@uno\.example\.test/);
  assert.ok(!text.includes(user.password), 'la sugerencia no revela la contraseña');
  // La página no puede ver dentro de la interfaz.
  assert.equal(await page.evaluate(() => document.querySelector('div[popover]').shadowRoot), null);
  await page.screenshot({ path: path.join(env.root, 'sugerencia.png') });
  await chooseWithKeyboard(page);
  await page.waitForFunction(() => document.getElementById('pass').value !== '');
  assert.equal(await page.inputValue('#user'), user.username);
  assert.equal(await page.inputValue('#pass'), user.password);
  assert.equal(await hasSuggestion(page), false);
  await env.sandbox.waitFor(/CONFIRM-SHOWN title=Ejemplo Uno .*page=https:\/\/uno\.example\.test kind=Exact scope=Both/);
});

test('varias cuentas del mismo sitio: se elige una concreta y solo se entrega esa', async () => {
  const carla = byUser('carla');
  const page = await open(U('multi.example.test'));
  await enableSite(page);
  await page.click('#user');
  await suggestionHost(page);
  const text = await waitForSuggestionText(page, /carla@multi\.example\.test/);
  assert.match(text, /beto@multi\.example\.test/);
  assert.match(text, /carla@multi\.example\.test/);
  await chooseWithKeyboard(page, 1);
  await page.waitForFunction(() => document.getElementById('pass').value !== '');
  assert.equal(await page.inputValue('#user'), carla.username);
  assert.equal(await page.inputValue('#pass'), carla.password);
  assert.ok(!JSON.stringify(lastFill().reply).includes(byUser('beto').password));
});

test('el usuario cancela en PassDesk: no se rellena nada', async () => {
  await command('confirm no', /CONFIRM-MODE no/);
  const page = await open(U('uno.example.test'));
  await enableSite(page);
  await page.click('#user');
  await suggestionHost(page);
  await waitForSuggestionText(page, /ana@uno\.example\.test/);
  const before = fillsCount();
  await chooseWithKeyboard(page);
  await env.sandbox.waitFor(/CONFIRM-SHOWN/);
  await settle(600);
  assert.equal(await page.inputValue('#user'), '');
  assert.equal(await page.inputValue('#pass'), '');
  assert.equal(fillsCount(), before);
  assert.match(await suggestionText(page), /Cancelado/);
});

test('con PassDesk bloqueado no se lista ni se rellena, y pedir el desbloqueo solo abre la pantalla de PassDesk', async () => {
  await command('lock', /STATE locked/);
  const startExchanges = exchanges().length;
  const page = await open(U('uno.example.test'));
  const popup = await popupFor(page);
  assert.equal(await popup.locator('.account').count(), 0);
  assert.match(await popup.locator('#status').innerText(), /bloqueado/);
  const mark = env.sandbox.mark();
  await popup.getByRole('button', { name: /Abrir PassDesk para desbloquear/ }).click();
  await env.sandbox.waitFor(/UNLOCK-REQUESTED state=Locked/);
  assert.ok(env.sandbox.since(mark).every((l) => !/STATE unlocked/.test(l)), 'pedir el desbloqueo no desbloquea');
  assert.ok(!SENTINELS.some((s) => JSON.stringify(exchanges().slice(startExchanges)).includes(s)), 'ninguna contraseña circuló');
  await popup.close();

  // Tras desbloquear con los mecanismos de PassDesk, vuelve a funcionar.
  await command('unlock', /STATE unlocked/);
  const again = await popupFor(page);
  assert.equal(await again.locator('.account').count(), 1);
});

test('si se bloquea PassDesk mientras espera la confirmación, la contraseña no se entrega', async () => {
  await command('confirm ask', /CONFIRM-MODE ask/);
  const page = await open(U('uno.example.test'));
  const popup = await popupFor(page);
  const before = fillsCount();
  await popup.locator('.account').click();
  await env.sandbox.waitFor(/CONFIRM-SHOWN title=Ejemplo Uno/);
  await command('lock', /STATE locked/);
  await command('answer yes', /ANSWER yes/);
  await settle(800);
  assert.equal(await page.inputValue('#pass'), '');
  assert.equal(await page.inputValue('#user'), '');
  assert.equal(fillsCount(), before);
  const reply = lastFill().reply;
  assert.equal(reply.ok, false);
  assert.equal(reply.error, 'locked');
});

test('una página hostil no puede pedir credenciales a la extensión ni provocar la sugerencia', async () => {
  const page = await open(U('uno.example.test', '/hostile'));
  await enableSite(page);
  const probe = await page.evaluate(() => window.__probe());
  assert.equal(probe.runtime, 'undefined', 'la página no tiene chrome.runtime');
  assert.equal(probe.lib, 'undefined');
  assert.equal(probe.content, 'undefined');
  assert.equal(await page.evaluate(() => typeof (window.chrome && window.chrome.runtime && window.chrome.runtime.sendMessage)), 'undefined');
  const mark = env.sandbox.mark();
  const requests = exchanges().length;
  await page.evaluate(() => window.__attack());
  await settle(1500);
  assert.equal(await hasSuggestion(page), false, 'enfocar o pulsar desde el código de la página no abre la sugerencia');
  assert.equal(env.sandbox.since(mark).filter((l) => /^AUDIT /.test(l)).length, 0, 'ninguna solicitud llegó a PassDesk');
  assert.equal(exchanges().length, requests);
  assert.equal(await page.inputValue('#pass'), '');
});

test('sin interacción real del usuario no hay sugerencia (autofocus o foco por código)', async () => {
  const page = await open(U('uno.example.test'));
  await enableSite(page);
  const mark = env.sandbox.mark();
  await settle(2000);
  await page.evaluate(() => document.getElementById('user').focus());
  await settle(800);
  assert.equal(await hasSuggestion(page), false);
  assert.equal(env.sandbox.since(mark).filter((l) => /^AUDIT /.test(l)).length, 0);
});

test('marco de otro origen: no hay sugerencia ni relleno', async () => {
  const marco = await open(U('marco.example.test'));
  await enableSite(marco);
  await marco.close();
  const page = await open(U('evil.example.test', '/frame-parent'));
  const frame = page.frameLocator('#inner');
  await frame.locator('#user').waitFor();
  const mark = env.sandbox.mark();
  await frame.locator('#user').click();
  await settle(1200);
  const inner = page.frames().find((f) => f.url().includes('marco.example.test'));
  assert.ok(inner, 'el marco existe');
  assert.equal(await hasSuggestion(inner), false);
  assert.equal(env.sandbox.since(mark).filter((l) => /^AUDIT /.test(l)).length, 0);

  // La ventana de la extensión actúa sobre el documento principal: ni lista cuentas de otro sitio ni escribe en el marco.
  const popup = await popupFor(page);
  assert.equal(await popup.locator('.account').count(), 0);
  assert.equal(await frame.locator('#user').inputValue(), '');
});

test('marco del mismo origen: sí se puede usar', async () => {
  const user = byUser('ana');
  const first = await open(U('uno.example.test'));
  await enableSite(first);
  await first.close();
  const page = await open(U('uno.example.test', '/same-origin-frame'));
  const frame = page.frameLocator('#inner');
  await frame.locator('#user').click();
  const inner = page.frames().find((f) => f.url().endsWith('/login'));
  await inner.waitForFunction(() => Boolean(document.querySelector('div[popover]')));
  await chooseWithKeyboard(page);
  await inner.waitForFunction(() => document.getElementById('pass').value !== '');
  assert.equal(await inner.locator('#pass').inputValue(), user.password);
});

test('el acceso se concede por sitio, pero el origen completo (con puerto) decide qué cuentas salen', async () => {
  const first = await open(U('uno.example.test'));
  await enableSite(first);
  await first.close();
  const page = await open(U('uno.example.test', '/login', ':8443'));
  await page.click('#user');
  await suggestionHost(page);
  const text = await waitForSuggestionText(page, /dani@uno\.example\.test/);
  assert.match(text, /https:\/\/uno\.example\.test:8443/);
  assert.doesNotMatch(text, /ana@uno\.example\.test/, 'la cuenta del puerto 443 no se ofrece en el 8443');
});

test('dominios engañosos, puertos y HTTP: no se ofrece ninguna cuenta', async () => {
  const cases = [
    ['https://uno.example.test.evil.test/login', 'dominio que contiene el nombre'],
    ['https://unoexample.test/login', 'nombre parecido'],
    ['https://uno.example.test:9443/login', 'otro puerto'],
    ['https://login.uno.example.test/login', 'subdominio (desactivado por defecto)'],
    ['https://example.test/login', 'dominio padre'],
  ];
  for (const [url, why] of cases) {
    const page = await open(url);
    const popup = await popupFor(page);
    assert.equal(await popup.locator('.account').count(), 0, why);
    await popup.close();
    await page.close();
  }

  // Con puerto 8443 solo sale la cuenta guardada para ese puerto.
  const withPort = await open(U('uno.example.test', '/login', ':8443'));
  const popup = await popupFor(withPort);
  assert.equal(await popup.locator('.account').count(), 1);
  assert.match(await popup.locator('.account').innerText(), /Con puerto/);
  await popup.close();

  // HTTP plano: ni siquiera se consulta a PassDesk.
  const mark = env.sandbox.mark();
  const plain = await open('http://uno.example.test/login');
  const plainPopup = await popupFor(plain);
  assert.match(await plainPopup.locator('#status').innerText(), /HTTPS/);
  assert.equal(await plainPopup.locator('.account').count(), 0);
  assert.equal(env.sandbox.since(mark).filter((l) => /^AUDIT /.test(l)).length, 0);
});

test('subdominios: solo si se activan, y avisando de dónde se guardó la cuenta', async () => {
  const off = await open(U('login.padre-sub.test'));
  const offPopup = await popupFor(off);
  assert.equal(await offPopup.locator('.account').count(), 0, 'sin el ajuste no se ofrece');
  await offPopup.close();
  await off.close();

  await command('subdomains on', /SUBDOMAINS on/);
  const page = await open(U('login.padre-sub.test'));
  const popup = await popupFor(page);
  assert.equal(await popup.locator('.account').count(), 1);
  assert.match(await popup.locator('.account').innerText(), /padre-sub\.test, no para este sitio exacto/);
  // Y un sitio de otro dominio registrable sigue sin salir.
  const other = await open(U('login.otro-padre.test'));
  assert.equal(await (await popupFor(other)).locator('.account').count(), 0);
});

test('páginas internas del navegador y de la extensión: no se rellena', async () => {
  const internal = await env.context.newPage();
  await internal.goto('chrome://version');
  const popup1 = await popupFor(internal);
  assert.equal(await popup1.locator('.account').count(), 0);
  assert.match(await popup1.locator('#status').innerText(), /HTTPS|acceso/);
  const ownPage = await env.context.newPage();
  await ownPage.goto(`chrome-extension://${env.extensionId}/src/popup.html`);
  const popup2 = await env.openPopup(await env.tabIdOf(ownPage));
  await popup2.locator('#status').filter({ hasNotText: 'Buscando' }).waitFor();
  assert.equal(await popup2.locator('.account').count(), 0);
});

test('navegar a otro sitio tras abrir la ventana no reutiliza la elección', async () => {
  const page = await open(U('uno.example.test'));
  const popup = await popupFor(page);
  assert.equal(await popup.locator('.account').count(), 1);
  const before = fillsCount();
  await page.goto(U('multi.example.test')); // el mismo tipo de página, pero otro origen que también tiene cuentas
  await popup.locator('.account').click();
  await settle(900);
  assert.equal(await page.inputValue('#pass'), '');
  assert.equal(await page.inputValue('#user'), '');
  assert.equal(fillsCount(), before);
});

test('campos ocultos, transparentes, minúsculos o tapados nunca se rellenan', async () => {
  const user = byUser('fran');
  const page = await open(U('trampa.example.test', '/honeypot'));
  const popup = await popupFor(page);
  await popup.locator('.account').click();
  await popup.waitForEvent('close');
  assert.equal(await page.inputValue('#user'), user.username);
  assert.equal(await page.inputValue('#pass'), user.password);
  for (const id of ['h-user-opacity', 'h-user-offscreen', 'h-pass-hidden', 'h-pass-tiny', 'h-pass-covered']) {
    assert.equal(await page.inputValue(`#${id}`), '', `${id} debe quedar vacío`);
  }
});

test('acceso en dos pasos: primero solo el usuario y luego solo la contraseña', async () => {
  const user = byUser('elena');
  const page = await open(U('dospasos.example.test', '/twostep'));
  let popup = await popupFor(page);
  await popup.locator('.account').click();
  await popup.waitForEvent('close');
  assert.equal(await page.inputValue('#user'), user.username);
  const first = lastFill();
  assert.equal(first.request.scope, 'username');
  assert.equal(first.reply.password, undefined, 'la contraseña no se entrega en el primer paso');

  await page.click('#next');
  popup = await popupFor(page);
  await popup.locator('.account').click();
  await popup.waitForEvent('close');
  assert.equal(await page.inputValue('#pass'), user.password);
  const second = lastFill();
  assert.ok(['password', 'both'].includes(second.request.scope));
});

test('formularios de registro (contraseña nueva): no se ofrece nada', async () => {
  const page = await open(U('uno.example.test', '/register'));
  const mark = env.sandbox.mark();
  const popup = await popupFor(page);
  assert.match(await popup.locator('#status').innerText(), /formulario de acceso/);
  assert.equal(env.sandbox.since(mark).filter((l) => /^AUDIT /.test(l)).length, 0);
});

test('mensajes malformados, enormes o de tipos inesperados reciben un error genérico', async () => {
  const send = (message) => env.mainWorker.evaluate((m) => chrome.runtime.sendNativeMessage('com.passdesk.autofill', m), message);
  const cases = [
    [{}, 'bad_request'],
    [{ v: 1, type: 'export' }, 'bad_request'],
    [{ v: 1, type: 'listAll' }, 'bad_request'],
    [{ v: 2, type: 'status' }, 'bad_request'],
    [{ v: 1, type: 'status', extra: 1 }, 'bad_request'],
    [{ v: 1, type: 'list', origin: 'chrome://settings' }, 'unsupported_origin'],
    [{ v: 1, type: 'list', origin: 'https://uno.example.test/ruta' }, 'bad_request'],
    [{ v: 1, type: 'fill', origin: 'https://uno.example.test', id: 'x', token: 'y', scope: 'both' }, 'bad_request'],
    [{ v: 1, type: 'status', rid: 'x'.repeat(5000) }, 'too_large'],
  ];
  for (const [message, expected] of cases) {
    const reply = await send(message).catch((e) => ({ ok: false, error: `excepción: ${e.message}` }));
    assert.equal(reply.ok, false, JSON.stringify(message).slice(0, 80));
    assert.equal(reply.error, expected, JSON.stringify(message).slice(0, 80));
  }
});

test('demasiadas solicitudes seguidas se limitan', async () => {
  // Las pruebas anteriores ya han gastado parte del cupo: se espera a que se vacíe la ventana de un minuto.
  await settle(61000);
  const replies = [];
  for (let i = 0; i < 40; i++) {
    replies.push(await env.mainWorker.evaluate(() => chrome.runtime.sendNativeMessage('com.passdesk.autofill', { v: 1, type: 'list', origin: 'https://uno.example.test' })));
  }
  assert.ok(replies.slice(0, 30).every((r) => r.ok), 'las primeras se atienden');
  assert.ok(replies.slice(30).some((r) => r.error === 'rate_limited'), 'las siguientes se limitan');
});

test('una extensión no autorizada no puede usar el conector', async () => {
  const mark = env.sandbox.mark();
  const result = await env.rogueWorker.evaluate(async () => {
    try {
      return await chrome.runtime.sendNativeMessage('com.passdesk.autofill', { v: 1, type: 'status' });
    } catch (error) {
      return { rejected: true, text: String(error.message) };
    }
  });
  assert.equal(result.rejected, true);
  assert.match(result.text, /forbidden|not found|access/i);
  assert.equal(env.sandbox.since(mark).filter((l) => /^AUDIT /.test(l)).length, 0);
});

test('el conector no atiende a quien lo lanza sin ser la extensión autorizada', async () => {
  const { execFileSync, spawnSync } = require('node:child_process');
  const hostDll = path.join(ROOT, 'src', 'PassDesk.NativeHost', 'bin', 'Release', 'net10.0', 'PassDesk.NativeHost.dll');
  const body = Buffer.from(JSON.stringify({ v: 1, type: 'status' }));
  const frame = Buffer.concat([Buffer.from(new Uint32Array([body.length]).buffer), body]);
  const mark = env.sandbox.mark();
  for (const args of [[], ['chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/'], ['https://uno.example.test/']]) {
    const result = spawnSync('dotnet', [hostDll, ...args], { input: frame, env: { ...process.env, PASSDESK_DATA_DIR: env.dataDir } });
    assert.equal(result.status, 2);
    assert.equal(result.stdout.length, 0);
  }
  assert.equal(env.sandbox.since(mark).filter((l) => /^AUDIT /.test(l)).length, 0);
  assert.ok(execFileSync);
});

test('las respuestas de listado nunca llevan contraseñas, y cada relleno solo la pedida', async () => {
  const all = exchanges();
  assert.ok(all.length > 5);
  for (const { request, reply } of all) {
    if (request?.type === 'list') {
      const text = JSON.stringify(reply);
      assert.ok(!/password/i.test(text) && !SENTINELS.some((s) => text.includes(s)));
    }
    if (request?.type === 'fill' && reply?.ok) {
      const secrets = SENTINELS.filter((s) => JSON.stringify(reply).includes(s));
      assert.ok(secrets.length <= 1, 'un relleno entrega como mucho una contraseña');
    }
  }
});

test('la extensión no declara permisos ni capacidades de más', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(ROOT, 'extension', 'manifest.json'), 'utf8'));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual([...manifest.permissions].sort(), ['activeTab', 'nativeMessaging', 'scripting']);
  assert.deepEqual(manifest.optional_host_permissions, ['https://*/*']);
  for (const forbidden of ['host_permissions', 'content_scripts', 'externally_connectable', 'web_accessible_resources', 'optional_permissions', 'declarative_net_request', 'devtools_page', 'chrome_url_overrides', 'omnibox', 'sandbox']) {
    assert.equal(manifest[forbidden], undefined, `no debe declarar ${forbidden}`);
  }
  const csp = manifest.content_security_policy.extension_pages;
  assert.match(csp, /script-src 'self'/);
  assert.match(csp, /connect-src 'none'/);
  assert.doesNotMatch(csp, /unsafe-|https?:|\*/);
  assert.ok(!JSON.stringify(manifest).includes('<all_urls>'));
});

test('el código de la extensión no guarda datos, no usa la red y no escribe en la consola', () => {
  const dir = path.join(ROOT, 'extension');
  const files = [];
  const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).forEach((e) => (e.isDirectory() ? walk(path.join(d, e.name)) : files.push(path.join(d, e.name))));
  walk(dir);
  const banned = [/console\./, /localStorage/, /sessionStorage/, /indexedDB/, /chrome\.storage/, /\bfetch\s*\(/, /XMLHttpRequest/, /WebSocket/, /\beval\s*\(/, /new Function/, /\.innerHTML/, /\.outerHTML\s*=/, /document\.write/, /insertAdjacentHTML/, /window\.postMessage/, /addEventListener\(\s*['"]message['"]/, /onMessageExternal/, /sendNativeMessage\([^)]*password/i];
  for (const file of files.filter((f) => /\.(js|html|css|json)$/.test(f))) {
    const text = fs.readFileSync(file, 'utf8');
    for (const pattern of banned) {
      assert.doesNotMatch(text, pattern, `${path.relative(ROOT, file)} contiene ${pattern}`);
    }
  }
  const html = fs.readFileSync(path.join(dir, 'src', 'popup.html'), 'utf8');
  assert.doesNotMatch(html, /<script(?![^>]*src=)/, 'sin scripts en línea');
  assert.doesNotMatch(html, /\sstyle=|<style/, 'sin estilos en línea');
});

test('no quedan contraseñas ni en los registros de PassDesk ni en el perfil del navegador', async () => {
  // Los registros del banco de pruebas (todo lo que PassDesk "registraría") no contienen secretos.
  const logText = env.sandbox.lines.join('\n');
  for (const secret of SENTINELS) {
    assert.ok(!logText.includes(secret), 'la salida del intermediario no debe contener contraseñas');
  }
  assert.ok(!env.sandbox.lines.some((l) => /^STDERR/.test(l)), 'sin errores en el banco de pruebas');

  // Perfil del navegador: se cierra para vaciar a disco y se busca cada contraseña (UTF-8 y UTF-16) en todos los archivos.
  const storageKeys = await env.mainWorker.evaluate(() => typeof chrome.storage);
  assert.equal(storageKeys, 'undefined', 'la extensión no tiene acceso a chrome.storage');
  await env.context.close();
  closedEarly = true;
  await env.sandbox.stop();
  const needles = SENTINELS.flatMap((s) => [Buffer.from(s, 'utf8'), Buffer.from(s, 'utf16le')]);
  let scanned = 0;
  const scan = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        scan(full);
      } else if (entry.isFile() && !full.startsWith(env.logDir)) {
        let data;
        try {
          data = fs.readFileSync(full);
        } catch {
          continue;
        }
        scanned++;
        for (const needle of needles) {
          assert.equal(data.indexOf(needle), -1, `contraseña ficticia encontrada en ${full}`);
        }
      }
    }
  };
  scan(env.userDataDir);
  assert.ok(scanned > 50, `se escanearon ${scanned} archivos del perfil`);
  // Y la extensión copiada en disco tampoco contiene contraseñas (solo código).
  scan(env.mainDir);
});
