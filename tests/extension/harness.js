'use strict';
// Arnés de las pruebas de la extensión: Chromium real + extensión real + conector real (PassDesk.NativeHost) + intermediario y
// tubería reales (PassDesk.AutofillSandbox) con una bóveda FICTICIA. No se toca ninguna bóveda real.
const { chromium } = require('playwright-core');
const { spawn, execFileSync } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const net = require('node:net');
const os = require('node:os');
const path = require('node:path');

const ROOT = path.resolve(__dirname, '..', '..');
const EXTENSION_DIR = path.join(ROOT, 'extension');
const HOST_NAME = 'com.passdesk.autofill';

// Credenciales FICTICIAS con valores inconfundibles, para buscarlas en registros y en el disco.
const VAULT = [
  { title: 'Ejemplo Uno', url: 'https://uno.example.test', username: 'ana@uno.example.test', password: 'SENTINEL-Uno-Ficticia#2026' },
  { title: 'Multi (personal)', url: 'https://multi.example.test', username: 'beto@multi.example.test', password: 'SENTINEL-Beto-Ficticia#2026' },
  { title: 'Multi (trabajo)', url: 'https://multi.example.test', username: 'carla@multi.example.test', password: 'SENTINEL-Carla-Ficticia#2026' },
  { title: 'Con puerto', url: 'https://uno.example.test:8443', username: 'dani@uno.example.test', password: 'SENTINEL-Puerto-Ficticia#2026' },
  { title: 'Dos pasos', url: 'https://dospasos.example.test', username: 'elena@dospasos.example.test', password: 'SENTINEL-DosPasos-Ficticia#2026' },
  { title: 'Trampa', url: 'https://trampa.example.test', username: 'fran@trampa.example.test', password: 'SENTINEL-Trampa-Ficticia#2026' },
  { title: 'Marco', url: 'https://marco.example.test', username: 'gema@marco.example.test', password: 'SENTINEL-Marco-Ficticia#2026' },
  { title: 'Padre', url: 'https://padre-sub.test', username: 'hugo@padre-sub.test', password: 'SENTINEL-Padre-Ficticia#2026' },
];
const SENTINELS = VAULT.map((v) => v.password);

function run(cmd, args, options = {}) {
  return execFileSync(cmd, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], ...options });
}

function buildDotnet() {
  const sandbox = path.join(ROOT, 'tools', 'PassDesk.AutofillSandbox');
  const host = path.join(ROOT, 'src', 'PassDesk.NativeHost');
  run('dotnet', ['build', '-c', 'Release', '-v', 'q', '--nologo', sandbox]);
  run('dotnet', ['build', '-c', 'Release', '-v', 'q', '--nologo', host]);
  return {
    sandboxDll: path.join(sandbox, 'bin', 'Release', 'net10.0', 'PassDesk.AutofillSandbox.dll'),
    hostDll: path.join(host, 'bin', 'Release', 'net10.0', 'PassDesk.NativeHost.dll'),
  };
}

function extensionIdFromKey(keyBase64) {
  const hash = crypto.createHash('sha256').update(Buffer.from(keyBase64, 'base64')).digest('hex').slice(0, 32);
  return [...hash].map((c) => String.fromCharCode('a'.charCodeAt(0) + parseInt(c, 16))).join('');
}

// ---- Servidor de páginas de prueba (HTTP y HTTPS en el mismo puerto) ----

function makeCertificate(dir) {
  const key = path.join(dir, 'key.pem');
  const cert = path.join(dir, 'cert.pem');
  run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '2', '-subj', '/CN=passdesk-test', '-addext', 'subjectAltName=DNS:*.test,DNS:test']);
  return { key: fs.readFileSync(key), cert: fs.readFileSync(cert) };
}

const page = (title, body, head = '') => `<!doctype html><html lang="es"><head><meta charset="utf-8"><title>${title}</title>${head}</head><body>${body}</body></html>`;

const PAGES = {
  '/login': page('Acceso', `<h1>Acceso</h1><form id="f" action="/submit" method="post"><label>Usuario <input id="user" name="username" autocomplete="username" type="text"></label><br><label>Contraseña <input id="pass" name="password" autocomplete="current-password" type="password"></label><br><button type="submit">Entrar</button></form>`),
  '/twostep': page('Dos pasos', `<h1>Dos pasos</h1><form id="f" action="/submit" method="post"><label>Correo <input id="user" name="email" type="email" autocomplete="username"></label><button type="button" id="next">Siguiente</button><div id="step2" hidden><label>Contraseña <input id="pass" name="password" type="password" autocomplete="current-password"></label></div></form><script>document.getElementById('next').addEventListener('click',()=>{document.getElementById('step2').hidden=false;});</script>`),
  '/register': page('Registro', `<h1>Registro</h1><form><input id="user" name="email" type="email" autocomplete="username"><input id="p1" name="p1" type="password" autocomplete="new-password"><input id="p2" name="p2" type="password" autocomplete="new-password"></form>`),
  '/honeypot': page('Trampa', `<h1>Acceso</h1><form id="f" action="/submit" method="post">
      <input id="h-user-opacity" name="email_confirm" type="text" autocomplete="username" style="opacity:0;position:absolute;left:10px;top:10px;width:200px;height:30px">
      <input id="h-user-offscreen" name="login_alt" type="text" style="position:absolute;left:-5000px;top:0;width:200px;height:30px">
      <input id="h-pass-hidden" name="pw_hidden" type="password" style="display:none">
      <input id="h-pass-tiny" name="pw_tiny" type="password" style="position:absolute;left:300px;top:10px;width:1px;height:1px">
      <input id="h-pass-covered" name="pw_covered" type="password" style="position:absolute;left:20px;top:200px;width:200px;height:30px">
      <div style="position:absolute;left:0;top:190px;width:400px;height:60px;background:#fff"></div>
      <label>Usuario <input id="user" name="username" autocomplete="username" type="text"></label><br>
      <label>Contraseña <input id="pass" name="password" autocomplete="current-password" type="password"></label>
      <button type="submit">Entrar</button></form>`),
  '/frame-parent': page('Marco', `<h1>Página con marco</h1><iframe id="inner" src="https://marco.example.test/login" width="500" height="300" title="Acceso"></iframe>`),
  '/same-origin-frame': page('Marco mismo origen', `<h1>Página con marco del mismo origen</h1><iframe id="inner" src="/login" width="500" height="300" title="Acceso"></iframe>`),
  '/hostile': page('Hostil', `<h1>Página hostil</h1><form id="f"><input id="user" name="username" type="text" autocomplete="username"><input id="pass" name="password" type="password"></form>
      <script>
        window.__events = [];
        window.addEventListener('message', (e) => window.__events.push(['message', typeof e.data]));
        window.__probe = () => { const out = {}; out.chrome = typeof window.chrome; out.runtime = typeof (window.chrome && window.chrome.runtime); out.lib = typeof window.__passdeskLib; out.content = typeof window.__passdeskContent; return out; };
        window.__attack = () => {
          const user = document.getElementById('user'); const pass = document.getElementById('pass');
          window.postMessage({ t: 'list' }, '*'); window.postMessage({ t: 'fill', id: '00000000-0000-0000-0000-000000000001', token: 'AAAAAAAAAAAAAAAAAAAAAA', scope: 'both' }, '*');
          for (const name of ['passdesk', 'pd:list', 'pd:fill', 'list', 'fill']) { document.dispatchEvent(new CustomEvent(name, { detail: { t: 'list' } })); window.dispatchEvent(new CustomEvent(name, { detail: { t: 'list' } })); }
          user.focus(); pass.focus(); user.click(); pass.click();
          user.dispatchEvent(new FocusEvent('focusin', { bubbles: true })); pass.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
          user.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true })); user.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
          return 'ok';
        };
      </script>`),
};

function startPageServer(tlsOptions) {
  const requests = [];
  const handler = (req, res) => {
    const chunks = [];
    req.on('data', (c) => chunks.push(c));
    req.on('end', () => {
      const url = new URL(req.url, 'http://x');
      requests.push({ host: req.headers.host, method: req.method, url: req.url, body: Buffer.concat(chunks).toString('utf8'), headers: JSON.stringify(req.headers) });
      const html = PAGES[url.pathname] ?? (url.pathname === '/submit' ? page('Enviado', '<p>Enviado</p>') : null);
      if (html === null) {
        res.writeHead(404, { 'content-type': 'text/plain' });
        res.end('no encontrado');
        return;
      }
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      res.end(html);
    });
  };
  const httpsServer = https.createServer(tlsOptions, handler);
  const httpServer = http.createServer(handler);
  // Un solo puerto para HTTP y HTTPS: el primer byte de una conexión TLS es 0x16.
  const sockets = new Set();
  const front = net.createServer((socket) => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
    socket.once('readable', () => {
      const first = socket.read(1);
      if (first === null) {
        return;
      }
      socket.unshift(first);
      (first[0] === 0x16 ? httpsServer : httpServer).emit('connection', socket);
    });
    socket.on('error', () => {});
  });
  return new Promise((resolve) => {
    front.listen(0, '127.0.0.1', () => resolve({ port: front.address().port, requests, close: () => { front.close(); sockets.forEach((s) => s.destroy()); } }));
  });
}

// ---- Bóveda ficticia (proceso .NET) ----

function startSandbox(sandboxDll, vaultFile, env) {
  const child = spawn('dotnet', [sandboxDll, '--vault', vaultFile], { env: { ...process.env, ...env }, stdio: ['pipe', 'pipe', 'pipe'] });
  const lines = [];
  const waiters = [];
  let buffer = '';
  child.stdout.on('data', (data) => {
    buffer += data.toString('utf8');
    let index;
    while ((index = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, index).trimEnd();
      buffer = buffer.slice(index + 1);
      lines.push(line);
      for (const waiter of [...waiters]) {
        if (waiter.pattern.test(line)) {
          waiters.splice(waiters.indexOf(waiter), 1);
          waiter.resolve(line);
        }
      }
    }
  });
  child.stderr.on('data', (data) => lines.push(`STDERR ${data.toString('utf8').trimEnd()}`));
  const api = {
    lines,
    send: (command) => child.stdin.write(`${command}\n`),
    waitFor: (pattern, timeoutMs = 15000) => {
      const existing = lines.find((l) => pattern.test(l));
      if (existing) {
        return Promise.resolve(existing);
      }
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`Tiempo agotado esperando ${pattern}`)), timeoutMs);
        waiters.push({ pattern, resolve: (line) => { clearTimeout(timer); resolve(line); } });
      });
    },
    count: (pattern) => lines.filter((l) => pattern.test(l)).length,
    mark: () => lines.length,
    since: (mark) => lines.slice(mark),
    stop: async () => {
      child.stdin.write('quit\n');
      await new Promise((resolve) => { child.once('exit', resolve); setTimeout(() => { child.kill(); resolve(); }, 3000); });
    },
  };
  return api.waitFor(/^READY /).then(() => api);
}

// ---- Entorno completo ----

async function createEnvironment({ patchHostPermissions = true } = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'passdesk-e2e-'));
  const dataDir = path.join(root, 'data');
  const userDataDir = path.join(root, 'chromium-profile');
  fs.mkdirSync(dataDir);
  fs.mkdirSync(path.join(userDataDir, 'NativeMessagingHosts'), { recursive: true });

  const { sandboxDll, hostDll } = buildDotnet();
  const server = await startPageServer(makeCertificate(root));
  const vaultFile = path.join(root, 'vault.json');
  fs.writeFileSync(vaultFile, JSON.stringify(VAULT));
  const envVars = { PASSDESK_DATA_DIR: dataDir };
  const sandbox = await startSandbox(sandboxDll, vaultFile, envVars);

  // Copia de la extensión para las pruebas: igual que la real, más acceso a *.test (solo para este entorno) para no tener que
  // pulsar el aviso de permisos del navegador. Una segunda copia con otra clave simula una extensión NO autorizada.
  const manifest = JSON.parse(fs.readFileSync(path.join(EXTENSION_DIR, 'manifest.json'), 'utf8'));
  const extensionId = extensionIdFromKey(manifest.key);
  const copyExtension = (name, mutate) => {
    const target = path.join(root, name);
    fs.cpSync(EXTENSION_DIR, target, { recursive: true });
    const copy = JSON.parse(fs.readFileSync(path.join(target, 'manifest.json'), 'utf8'));
    mutate(copy);
    fs.writeFileSync(path.join(target, 'manifest.json'), JSON.stringify(copy, null, 2));
    return target;
  };
  const mainDir = copyExtension('ext-main', (m) => {
    if (patchHostPermissions) {
      // También http://*.test para poder comprobar que la extensión rechaza páginas HTTP aunque tenga acceso a ellas.
      m.host_permissions = ['https://*.test/*', 'http://*.test/*'];
    }
  });
  const rogueKey = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 }).publicKey.export({ type: 'spki', format: 'der' }).toString('base64');
  const rogueDir = copyExtension('ext-rogue', (m) => {
    m.key = rogueKey;
    m.name = 'Extension no autorizada';
    m.host_permissions = ['https://*.test/*'];
  });

  // Conector: se lanza con `dotnet` y registra lo que entra y sale para poder comprobar qué se pidió y qué se devolvió.
  const logDir = path.join(root, 'hostlog');
  fs.mkdirSync(logDir);
  const wrapper = path.join(root, 'passdesk-host.sh');
  fs.writeFileSync(wrapper, `#!/bin/sh\nn=$(date +%s%N)\ntee "${logDir}/$n.in" | dotnet "${hostDll}" "$@" | tee "${logDir}/$n.out"\n`, { mode: 0o755 });
  fs.writeFileSync(path.join(userDataDir, 'NativeMessagingHosts', `${HOST_NAME}.json`), JSON.stringify({
    name: HOST_NAME,
    description: 'PassDesk (pruebas)',
    path: wrapper,
    type: 'stdio',
    allowed_origins: [`chrome-extension://${extensionId}/`],
  }));

  const context = await chromium.launchPersistentContext(userDataDir, {
    channel: 'chromium',
    headless: true,
    ignoreHTTPSErrors: true,
    env: { ...process.env, ...envVars },
    args: [
      `--disable-extensions-except=${mainDir},${rogueDir}`,
      `--load-extension=${mainDir},${rogueDir}`,
      `--host-resolver-rules=MAP *.test 127.0.0.1:${server.port}`,
      '--ignore-certificate-errors',
      '--no-proxy-server',
      '--no-first-run',
      '--no-default-browser-check',
    ],
  });

  const workers = async () => {
    for (let i = 0; i < 100 && context.serviceWorkers().length < 2; i++) {
      await context.waitForEvent('serviceworker', { timeout: 500 }).catch(() => {});
    }
    return context.serviceWorkers();
  };
  const all = await workers();
  const mainWorker = all.find((w) => w.url().includes(extensionId));
  const rogueWorker = all.find((w) => !w.url().includes(extensionId));
  // Las API de chrome.* se enlazan un instante después de que el worker aparezca.
  for (const worker of [mainWorker, rogueWorker]) {
    for (let i = 0; i < 50; i++) {
      if (await worker.evaluate(() => Boolean(globalThis.chrome && chrome.runtime && chrome.runtime.id)).catch(() => false)) {
        break;
      }
      await new Promise((resolve) => setTimeout(resolve, 100));
    }
  }

  const env = {
    root, dataDir, userDataDir, logDir, extensionId, server, sandbox, context, mainWorker, rogueWorker, mainDir,
    hostLogs: () => fs.readdirSync(logDir).sort().map((f) => ({ file: f, text: fs.readFileSync(path.join(logDir, f), 'utf8') })),
    // Id de pestaña del navegador (no se puede buscar por URL: sin permiso del sitio, el navegador no la revela).
    async tabIdOf(page) {
      await page.bringToFront();
      return mainWorker.evaluate(async () => (await chrome.tabs.query({ active: true }))[0]?.id ?? null);
    },
    async openPopup(tabId) {
      const popup = await context.newPage();
      await popup.goto(`chrome-extension://${extensionId}/src/popup.html?tab=${tabId}`);
      return popup;
    },
    async close() {
      await context.close().catch(() => {});
      await sandbox.stop();
      server.close();
    },
    cleanup: () => fs.rmSync(root, { recursive: true, force: true }),
  };
  return env;
}

module.exports = { createEnvironment, VAULT, SENTINELS, HOST_NAME, EXTENSION_DIR, ROOT, extensionIdFromKey };
