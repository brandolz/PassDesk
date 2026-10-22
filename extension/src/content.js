// Sugerencias de PassDesk en la página. Se ejecuta en el mundo aislado de la extensión.
//
// - Solo actúa en páginas HTTPS y en marcos del mismo origen que la página principal.
// - Muestra la sugerencia cuando una persona enfoca un campo de acceso (pulsando o con el teclado), nunca por una orden de
//   la página. Nada de lo que escribe el usuario en los formularios se lee ni se envía.
// - Solo rellena tras un clic o una pulsación REALES (isTrusted) sobre una cuenta concreta. La interfaz vive en una raíz
//   sombra cerrada: el JavaScript de la página no puede leerla ni pulsarla.
// - No escucha mensajes de la página (ni «message» ni eventos propios): la página no puede pedir nada a la extensión.
(() => {
  'use strict';

  const lib = globalThis.__passdeskLib;
  if (!lib || globalThis.__passdeskContent || !lib.isHttpsContext() || lib.inCrossOriginFrame()) {
    return;
  }
  Object.defineProperty(globalThis, '__passdeskContent', { value: true, enumerable: false });

  const TRUSTED_INTERACTION_MS = 1500;
  const MIN_DWELL_MS = 350;
  const POLL_MS = 3000;
  const POLL_MAX = 20;

  let lastTrusted = -Infinity;
  let ui = null;
  let pollTimer = 0;
  let announcedUnavailable = false;

  const CSS = `
    :host { all: initial; }
    .panel { font: 14px/1.35 system-ui, "Segoe UI", sans-serif; color: #1b1b1f; background: #ffffff; border: 1px solid #c9ccd6;
      border-radius: 10px; box-shadow: 0 8px 28px rgba(0,0,0,.28); min-width: 260px; max-width: 360px; overflow: hidden; }
    @media (prefers-color-scheme: dark) { .panel { color: #f1f1f4; background: #25262b; border-color: #44464f; } }
    .head { display: flex; justify-content: space-between; gap: 12px; padding: 8px 12px; font-size: 12px; background: rgba(120,120,140,.14); }
    .logo { font-weight: 600; }
    .site { opacity: .8; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .opt { display: block; width: 100%; box-sizing: border-box; text-align: left; font: inherit; color: inherit; background: transparent;
      border: 0; border-top: 1px solid rgba(120,120,140,.25); padding: 9px 12px; cursor: pointer; }
    .opt:hover, .opt:focus-visible { background: rgba(60,120,220,.18); outline: 2px solid #3b78e7; outline-offset: -2px; }
    .title { display: block; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .user { display: block; opacity: .85; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .note { display: block; margin-top: 2px; font-size: 12px; color: #a05a00; }
    @media (prefers-color-scheme: dark) { .note { color: #f2b25c; } }
    .msg { padding: 10px 12px; }
    .foot { padding: 5px 12px; font-size: 11px; opacity: .65; border-top: 1px solid rgba(120,120,140,.25); }
  `;
  const sheet = new CSSStyleSheet();
  sheet.replaceSync(CSS);

  const send = (message) => {
    try {
      return chrome.runtime.sendMessage(message).catch(() => ({ ok: false, error: 'unavailable' }));
    } catch {
      return Promise.resolve({ ok: false, error: 'unavailable' });
    }
  };

  function markTrusted(event) {
    if (event.isTrusted) {
      lastTrusted = performance.now();
    }
  }
  addEventListener('pointerdown', markTrusted, { capture: true, passive: true });
  addEventListener('keydown', markTrusted, { capture: true, passive: true });

  function trustedRecently() {
    return performance.now() - lastTrusted < TRUSTED_INTERACTION_MS;
  }

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) {
      node.className = className;
    }
    if (text !== undefined) {
      node.textContent = text;
    }
    return node;
  }

  function closeUi() {
    clearTimeout(pollTimer);
    if (ui) {
      const { host } = ui;
      ui = null;
      lib.removeOverlay(host);
      try {
        host.hidePopover();
      } catch {
        // Ya estaba oculto.
      }
      host.remove();
    }
  }

  function openUi(anchor, origin) {
    closeUi();
    const host = document.createElement('div');
    host.popover = 'manual';
    const rect = anchor.getBoundingClientRect();
    const below = rect.bottom + 4;
    const top = below + 260 > innerHeight && rect.top > 270 ? rect.top - 264 : below;
    const left = Math.max(4, Math.min(rect.left, innerWidth - 372));
    const style = {
      position: 'fixed', inset: 'auto', top: `${Math.max(4, top)}px`, left: `${left}px`, margin: '0', padding: '0', border: '0',
      background: 'transparent', overflow: 'visible', width: 'auto', height: 'auto', opacity: '1', visibility: 'visible',
      transform: 'none', filter: 'none', 'pointer-events': 'auto', display: 'block', 'z-index': '2147483647',
    };
    for (const [name, value] of Object.entries(style)) {
      host.style.setProperty(name, value, 'important');
    }
    const shadow = host.attachShadow({ mode: 'closed' });
    shadow.adoptedStyleSheets = [sheet];
    const panel = el('div', 'panel');
    panel.setAttribute('role', 'dialog');
    panel.setAttribute('aria-label', 'PassDesk');
    const head = el('div', 'head');
    head.append(el('span', 'logo', 'PassDesk'), el('span', 'site', origin));
    const body = el('div', 'body');
    const status = el('div', 'msg');
    status.setAttribute('role', 'status');
    status.hidden = true;
    panel.append(head, body, status, el('div', 'foot', 'Esc para cerrar'));
    shadow.append(panel);
    document.documentElement.append(host);
    lib.addOverlay(host);
    host.showPopover();
    ui = { host, shadow, body, status, anchor, origin, shownAt: performance.now() };
    return ui;
  }

  function showMessage(text, action) {
    if (!ui) {
      return;
    }
    ui.body.replaceChildren();
    ui.status.hidden = false;
    ui.status.replaceChildren(el('span', '', text));
    if (action) {
      const button = el('button', 'opt', action.label);
      button.type = 'button';
      button.addEventListener('click', (event) => {
        if (event.isTrusted) {
          action.run();
        }
      });
      ui.body.append(button);
    }
  }

  // La sugerencia solo cuenta si sigue siendo visible tal como la dibujamos: la página podría intentar taparla o hacerla
  // transparente para que se pulse sin verla.
  function uiIsGenuine() {
    if (!ui) {
      return false;
    }
    const { host } = ui;
    const computed = getComputedStyle(host);
    return (
      host.isConnected &&
      host.matches(':popover-open') &&
      computed.opacity === '1' &&
      computed.visibility === 'visible' &&
      computed.display === 'block' &&
      computed.transform === 'none' &&
      computed.filter === 'none' &&
      computed.pointerEvents === 'auto' &&
      host.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })
    );
  }

  function renderMatches(matches, scope) {
    ui.status.hidden = true;
    ui.body.replaceChildren();
    ui.body.setAttribute('role', 'listbox');
    ui.body.setAttribute('aria-label', 'Cuentas de PassDesk para este sitio');
    for (const match of matches) {
      const option = el('button', 'opt');
      option.type = 'button';
      option.setAttribute('role', 'option');
      option.append(el('span', 'title', match.title || match.site), el('span', 'user', match.username || '(sin usuario)'));
      if (match.kind === 'related') {
        option.append(el('span', 'note', `Guardada para ${match.site}, no para este sitio exacto`));
      }
      option.setAttribute('aria-label', `${match.title || match.site}, ${match.username || 'sin usuario'}${match.kind === 'related' ? `, guardada para ${match.site}` : ''}`);
      option.addEventListener('click', (event) => onChoose(event, match, scope));
      ui.body.append(option);
    }
  }

  async function onChoose(event, match, scope) {
    if (!event.isTrusted || !ui || performance.now() - ui.shownAt < MIN_DWELL_MS || !uiIsGenuine()) {
      return;
    }
    const current = ui;
    showMessage('Esperando a PassDesk…');
    const reply = await send({ t: 'fill', id: match.id, token: match.token, scope });
    if (ui !== current) {
      return;
    }
    if (reply && reply.ok) {
      closeUi();
      return;
    }
    switch (reply && reply.error) {
      case 'denied':
        showMessage('Cancelado. No se ha rellenado nada.');
        break;
      case 'locked':
        showLocked(current.anchor);
        break;
      case 'expired':
        refresh(current.anchor);
        break;
      case 'rate_limited':
      case 'busy':
        showMessage('Demasiadas solicitudes seguidas. Espera un momento.');
        break;
      default:
        showMessage('No se pudo rellenar este formulario.');
    }
  }

  function showLocked(anchor) {
    showMessage('PassDesk está bloqueado.', {
      label: 'Abrir PassDesk para desbloquear',
      run: async () => {
        await send({ t: 'unlock' });
        showMessage('Desbloquea PassDesk; la sugerencia aparecerá aquí.');
        poll(anchor, 0);
      },
    });
  }

  function poll(anchor, count) {
    clearTimeout(pollTimer);
    if (!ui || count >= POLL_MAX) {
      return;
    }
    pollTimer = setTimeout(async () => {
      if (!ui || ui.anchor !== anchor) {
        return;
      }
      const reply = await send({ t: 'list' });
      if (!ui) {
        return;
      }
      if (reply && reply.ok && reply.state === 'ready') {
        refresh(anchor, reply);
      } else {
        poll(anchor, count + 1);
      }
    }, POLL_MS);
  }

  async function refresh(anchor, knownReply) {
    const scope = lib.plan(anchor);
    if (!scope) {
      closeUi();
      return;
    }
    const reply = knownReply || (await send({ t: 'list' }));
    if (!ui || ui.anchor !== anchor) {
      return;
    }
    if (reply && reply.ok && reply.state === 'ready') {
      if (reply.matches.length === 0) {
        closeUi();
      } else {
        renderMatches(reply.matches, scope);
      }
      return;
    }
    switch (reply && reply.error) {
      case 'locked':
        showLocked(anchor);
        break;
      case 'not_created':
        showMessage('Crea tu bóveda en PassDesk para usar el autorrelleno.');
        break;
      case 'disabled':
      case 'unavailable':
      case 'host_missing':
        if (announcedUnavailable) {
          closeUi();
        } else {
          announcedUnavailable = true;
          showMessage('PassDesk no está abierto o la integración con el navegador está desactivada.');
        }
        break;
      case 'rate_limited':
        showMessage('Demasiadas solicitudes seguidas. Espera un momento.');
        break;
      default:
        closeUi();
    }
  }

  async function onFocus(event) {
    const target = event.target;
    if (!(target instanceof HTMLInputElement) || !trustedRecently() || !lib.fieldKind(target)) {
      return;
    }
    if (ui && ui.anchor === target) {
      return;
    }
    const scope = lib.plan(target);
    if (!scope) {
      return;
    }
    lib.setAnchor(target);
    openUi(target, location.origin);
    await refresh(target);
  }

  addEventListener('focusin', (event) => {
    onFocus(event);
    if (ui && event.target !== ui.anchor && event.target !== ui.host) {
      closeUi();
    }
  }, true);

  addEventListener('pointerdown', (event) => {
    if (ui && event.isTrusted && !event.composedPath().includes(ui.host) && event.target !== ui.anchor) {
      closeUi();
    }
  }, true);

  addEventListener('keydown', (event) => {
    if (!ui || !event.isTrusted) {
      return;
    }
    const options = [...ui.shadow.querySelectorAll('.opt')];
    if (event.key === 'Escape') {
      const anchor = ui.anchor;
      closeUi();
      anchor.focus({ preventScroll: true });
      event.preventDefault();
      event.stopPropagation();
    } else if ((event.key === 'ArrowDown' || event.key === 'ArrowUp') && options.length > 0) {
      const index = options.indexOf(ui.shadow.activeElement);
      const next = event.key === 'ArrowDown' ? (index + 1) % options.length : (index <= 0 ? options.length - 1 : index - 1);
      options[next].focus();
      event.preventDefault();
      event.stopPropagation();
    }
  }, true);

  addEventListener('scroll', () => closeUi(), { capture: true, passive: true });
  addEventListener('resize', () => closeUi(), { passive: true });
  addEventListener('pagehide', () => closeUi());
})();
