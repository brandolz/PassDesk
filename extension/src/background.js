// Service worker de PassDesk. Es el único componente que habla con el conector local (Native Messaging) y el único que
// decide el origen de una solicitud: lo deduce del remitente del mensaje que le da el navegador (sender), nunca de lo que
// diga el contenido de la página. No guarda nada: ni credenciales ni estado, y nunca escribe en la consola.
import { HOST_NAME, PROTOCOL_VERSION, httpsOrigin, isGuid, isScope, isToken, newRequestId, normalizeReply } from './shared.js';

const LIST_TIMEOUT_MS = 8000;
const FILL_TIMEOUT_MS = 50000;
const SITE_PREFIX = 'pd:';

// ---- Conector ----

async function callHost(message, timeoutMs, expectedOrigin) {
  const requestId = newRequestId();
  const request = { v: PROTOCOL_VERSION, rid: requestId, ...message };
  let timer;
  const timeout = new Promise((resolve) => {
    timer = setTimeout(() => resolve({ ok: false, error: 'unavailable' }), timeoutMs);
  });
  const call = (async () => {
    try {
      const reply = await chrome.runtime.sendNativeMessage(HOST_NAME, request);
      return normalizeReply(reply, requestId, expectedOrigin);
    } catch {
      // Conector no registrado, bloqueado o cerrado. El texto del error no se conserva.
      return { ok: false, error: 'host_missing' };
    }
  })();
  try {
    return await Promise.race([call, timeout]);
  } finally {
    clearTimeout(timer);
  }
}

// ---- Remitentes ----

function contentContext(sender) {
  if (sender.id !== chrome.runtime.id || !sender.tab || typeof sender.tab.id !== 'number' || typeof sender.documentId !== 'string') {
    return null;
  }
  if (sender.documentLifecycle && sender.documentLifecycle !== 'active') {
    return null;
  }
  const origin = httpsOrigin(sender.origin);
  if (!origin || httpsOrigin(sender.url) !== origin) {
    return null;
  }
  // Los marcos solo valen si son del mismo origen que la página principal (el script de contenido ya lo comprueba; aquí
  // se vuelve a comprobar con lo que sabe el navegador).
  if (sender.frameId !== 0 && httpsOrigin(sender.tab.url) !== origin) {
    return null;
  }
  return { tabId: sender.tab.id, frameId: sender.frameId, documentId: sender.documentId, origin };
}

const popupUrl = () => chrome.runtime.getURL('src/popup.html');

function isPopup(sender) {
  // sender.url lo fija el navegador: una página web nunca puede aparentar ser chrome-extension://…/popup.html. (En la ventana
  // emergente normal no hay pestaña; las pruebas abren la misma página en una pestaña.)
  return sender.id === chrome.runtime.id && typeof sender.url === 'string' && sender.url.split('?')[0] === popupUrl();
}

// ---- Operaciones ----

async function listFor(origin) {
  const reply = await callHost({ type: 'list', origin }, LIST_TIMEOUT_MS, origin);
  return reply.ok ? { ok: true, state: reply.state, matches: reply.matches || [] } : reply;
}

// Pide la credencial y la escribe en el documento exacto desde el que se eligió. Si la página navegó, la inyección falla y
// la credencial se descarta sin haberse guardado en ningún sitio.
async function fillDocument({ tabId, documentId, origin }, msg) {
  const reply = await callHost({ type: 'fill', origin, id: msg.id, token: msg.token, scope: msg.scope }, FILL_TIMEOUT_MS, origin);
  if (!reply.ok) {
    return reply;
  }
  try {
    const [injection] = await chrome.scripting.executeScript({
      target: { tabId, documentIds: [documentId] },
      world: 'ISOLATED',
      args: [origin, reply.username ?? null, reply.password ?? null],
      func: (expectedOrigin, username, password) => {
        const lib = globalThis.__passdeskLib;
        return lib ? lib.fillCredentials({ expectedOrigin, username, password }) : { ok: false, reason: 'no-lib' };
      },
    });
    const result = injection && injection.result;
    return result && result.ok ? { ok: true, filled: result.filled } : { ok: false, error: 'not_filled' };
  } catch {
    return { ok: false, error: 'not_filled' };
  }
}

// Documento principal de una pestaña: su origen (HTTPS), su identificador y qué campos de acceso tiene. Necesita el permiso
// activeTab (la persona acaba de abrir la ventana de la extensión) o el acceso concedido a ese sitio.
async function topDocument(tabId) {
  try {
    const tab = await chrome.tabs.get(tabId);
    const origin = httpsOrigin(tab.url);
    if (!origin) {
      return { error: 'unsupported_origin' };
    }
    await chrome.scripting.executeScript({ target: { tabId, frameIds: [0] }, world: 'ISOLATED', files: ['src/lib.js'] });
    const [info] = await chrome.scripting.executeScript({
      target: { tabId, frameIds: [0] },
      world: 'ISOLATED',
      func: () => ({ origin: location.origin, scope: globalThis.__passdeskLib ? globalThis.__passdeskLib.plan(null) : null }),
    });
    if (!info || !info.result || info.result.origin !== origin || typeof info.documentId !== 'string') {
      return { error: 'unsupported_origin' };
    }
    return { tabId, documentId: info.documentId, origin, scope: info.result.scope };
  } catch {
    return { error: 'no_access' };
  }
}

// ---- Sitios con sugerencias automáticas (scripts de contenido registrados por origen) ----

async function enabledSites() {
  const scripts = await chrome.scripting.getRegisteredContentScripts();
  return scripts.filter((s) => s.id.startsWith(SITE_PREFIX)).map((s) => s.id.slice(SITE_PREFIX.length));
}

async function enableSite(origin) {
  const pattern = `${origin}/*`;
  if (!(await chrome.permissions.contains({ origins: [pattern] }))) {
    return { ok: false, error: 'permission' };
  }
  const id = SITE_PREFIX + origin;
  if ((await enabledSites()).includes(origin)) {
    return { ok: true };
  }
  await chrome.scripting.registerContentScripts([{
    id,
    matches: [pattern],
    js: ['src/lib.js', 'src/content.js'],
    allFrames: true,
    runAt: 'document_idle',
    world: 'ISOLATED',
    persistAcrossSessions: true,
  }]);
  return { ok: true };
}

async function disableSite(origin) {
  try {
    await chrome.scripting.unregisterContentScripts({ ids: [SITE_PREFIX + origin] });
  } catch {
    // No estaba registrado.
  }
  try {
    await chrome.permissions.remove({ origins: [`${origin}/*`] });
  } catch {
    // Permiso ya retirado.
  }
  return { ok: true };
}

// ---- Mensajes ----

async function handleContent(ctx, msg) {
  switch (msg.t) {
    case 'list':
      return listFor(ctx.origin);
    case 'unlock': {
      const reply = await callHost({ type: 'unlock' }, LIST_TIMEOUT_MS, null);
      return reply.ok ? { ok: true, state: reply.state } : reply;
    }
    case 'fill':
      if (!isGuid(msg.id) || !isToken(msg.token) || !isScope(msg.scope)) {
        return { ok: false, error: 'bad_request' };
      }
      return fillDocument(ctx, msg);
    default:
      return { ok: false, error: 'bad_request' };
  }
}

async function handlePopup(msg) {
  const tabId = Number.isInteger(msg.tabId) ? msg.tabId : null;
  switch (msg.t) {
    case 'popup:list': {
      if (tabId === null) {
        return { ok: false, error: 'bad_request' };
      }
      const doc = await topDocument(tabId);
      if (doc.error) {
        return { ok: false, error: doc.error };
      }
      const sites = await enabledSites();
      const base = { origin: doc.origin, scope: doc.scope, siteEnabled: sites.includes(doc.origin) };
      if (!doc.scope) {
        return { ok: true, state: 'no_form', ...base, matches: [] };
      }
      const list = await listFor(doc.origin);
      return { ...list, ...base };
    }
    case 'popup:fill': {
      if (tabId === null || !isGuid(msg.id) || !isToken(msg.token)) {
        return { ok: false, error: 'bad_request' };
      }
      const doc = await topDocument(tabId);
      if (doc.error || !doc.scope) {
        return { ok: false, error: doc.error || 'not_filled' };
      }
      // La ficha nació para el origen que se listó: si la pestaña ya está en otro, el conector la rechaza.
      return fillDocument(doc, { id: msg.id, token: msg.token, scope: doc.scope });
    }
    case 'popup:unlock': {
      const reply = await callHost({ type: 'unlock' }, LIST_TIMEOUT_MS, null);
      return reply.ok ? { ok: true, state: reply.state } : reply;
    }
    case 'popup:enable-site': {
      const origin = httpsOrigin(msg.origin);
      return origin ? enableSite(origin) : { ok: false, error: 'bad_request' };
    }
    case 'popup:disable-site': {
      const origin = httpsOrigin(msg.origin);
      return origin ? disableSite(origin) : { ok: false, error: 'bad_request' };
    }
    default:
      return { ok: false, error: 'bad_request' };
  }
}

chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  if (!msg || typeof msg !== 'object') {
    return false;
  }
  let work;
  if (typeof msg.t === 'string' && msg.t.startsWith('popup:')) {
    work = isPopup(sender) ? handlePopup(msg) : Promise.resolve({ ok: false, error: 'bad_request' });
  } else {
    const ctx = contentContext(sender);
    work = ctx ? handleContent(ctx, msg) : Promise.resolve({ ok: false, error: 'bad_request' });
  }
  work.then(sendResponse, () => sendResponse({ ok: false, error: 'internal' }));
  return true;
});
