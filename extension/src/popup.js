// Ventana de la extensión: muestra las cuentas de PassDesk que corresponden a la pestaña actual y rellena solo la que se
// elige. Con el permiso temporal activeTab no necesita acceso permanente a ningún sitio.
import { httpsOrigin } from './shared.js';

const $ = (id) => document.getElementById(id);
const status = $('status');
const accounts = $('accounts');
const actions = $('actions');
const siteLine = $('site');

const send = (message) => chrome.runtime.sendMessage(message).catch(() => ({ ok: false, error: 'host_missing' }));

function setStatus(text) {
  status.textContent = text;
}

function button(label, onClick, className) {
  const node = document.createElement('button');
  node.type = 'button';
  node.textContent = label;
  if (className) {
    node.className = className;
  }
  node.addEventListener('click', onClick);
  return node;
}

async function activeTabId() {
  // «?tab=N» solo lo usan las pruebas automáticas; la ventana de la extensión no es accesible desde páginas web.
  const forced = Number.parseInt(new URLSearchParams(location.search).get('tab') ?? '', 10);
  if (Number.isInteger(forced)) {
    return forced;
  }
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab ? tab.id : null;
}

function renderSiteToggle(origin, enabled) {
  actions.replaceChildren();
  if (!origin) {
    return;
  }
  if (enabled) {
    actions.append(button('Dejar de mostrar sugerencias en este sitio', async () => {
      await send({ t: 'popup:disable-site', origin });
      renderSiteToggle(origin, false);
      setStatus('Sugerencias automáticas desactivadas en este sitio.');
    }));
  } else {
    actions.append(button('Mostrar sugerencias al enfocar los campos de este sitio', async () => {
      // El permiso solo se puede pedir desde un gesto del usuario, es decir, desde este clic.
      const granted = await chrome.permissions.request({ origins: [`${origin}/*`] }).catch(() => false);
      if (!granted) {
        setStatus('No se concedió el permiso para este sitio.');
        return;
      }
      const reply = await send({ t: 'popup:enable-site', origin });
      if (reply.ok) {
        renderSiteToggle(origin, true);
        setStatus('Sugerencias activadas en este sitio. Recarga la página para verlas.');
      } else {
        setStatus('No se pudo activar este sitio.');
      }
    }));
  }
}

async function fill(tabId, account) {
  setStatus('Esperando a PassDesk…');
  const reply = await send({ t: 'popup:fill', tabId, id: account.id, token: account.token });
  if (reply.ok) {
    window.close();
    return;
  }
  switch (reply.error) {
    case 'denied':
      setStatus('Cancelado. No se ha rellenado nada.');
      break;
    case 'locked':
      setStatus('PassDesk está bloqueado.');
      break;
    case 'expired':
    case 'bad_request':
      setStatus('La elección caducó. Vuelve a abrir esta ventana.');
      break;
    default:
      setStatus('No se pudo rellenar este formulario.');
  }
}

function renderAccounts(tabId, matches) {
  accounts.replaceChildren();
  for (const match of matches) {
    const item = document.createElement('li');
    const choose = button('', () => fill(tabId, match), 'account');
    const title = document.createElement('span');
    title.className = 'title';
    title.textContent = match.title || match.site;
    const user = document.createElement('span');
    user.className = 'user';
    user.textContent = match.username || '(sin usuario)';
    choose.append(title, user);
    if (match.kind === 'related') {
      const note = document.createElement('span');
      note.className = 'note';
      note.textContent = `Guardada para ${match.site}, no para este sitio exacto`;
      choose.append(note);
    }
    choose.setAttribute('aria-label', `${match.title || match.site}, ${match.username || 'sin usuario'}`);
    item.append(choose);
    accounts.append(item);
  }
  accounts.querySelector('button')?.focus();
}

async function main() {
  $('close').addEventListener('click', () => window.close());
  const tabId = await activeTabId();
  if (tabId === null) {
    setStatus('No hay ninguna pestaña activa.');
    return;
  }
  const reply = await send({ t: 'popup:list', tabId });
  const origin = httpsOrigin(reply.origin);
  siteLine.textContent = origin ?? '';
  if (!reply.ok) {
    switch (reply.error) {
      case 'unsupported_origin':
        setStatus('PassDesk solo rellena páginas HTTPS normales, no páginas internas del navegador ni sitios sin cifrar.');
        break;
      case 'no_access':
        setStatus('Abre esta ventana desde el icono de la extensión para dar acceso a la pestaña actual.');
        break;
      case 'host_missing':
      case 'unavailable':
        setStatus('PassDesk no está abierto, o la integración con el navegador está desactivada o sin instalar.');
        break;
      case 'locked':
        setStatus('PassDesk está bloqueado.');
        actions.replaceChildren(button('Abrir PassDesk para desbloquear', async () => {
          await send({ t: 'popup:unlock' });
          setStatus('Desbloquea PassDesk y vuelve a abrir esta ventana.');
        }));
        break;
      case 'not_created':
        setStatus('Crea tu bóveda en PassDesk para usar el autorrelleno.');
        break;
      case 'disabled':
        setStatus('La integración con el navegador está desactivada en los ajustes de PassDesk.');
        break;
      case 'rate_limited':
        setStatus('Demasiadas solicitudes seguidas. Espera un momento.');
        break;
      default:
        setStatus('No se pudo consultar PassDesk.');
    }
    return;
  }
  if (reply.state === 'no_form') {
    setStatus('No se ve ningún formulario de acceso en esta página.');
  } else if (reply.matches.length === 0) {
    setStatus('PassDesk no tiene ninguna cuenta guardada para este sitio.');
  } else {
    setStatus(reply.matches.length === 1 ? 'Elige la cuenta para rellenar:' : 'Hay varias cuentas. Elige una:');
    renderAccounts(tabId, reply.matches);
  }
  renderSiteToggle(origin, reply.siteEnabled);
}

main();
