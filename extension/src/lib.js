// Biblioteca de PassDesk para páginas web. Se ejecuta en el MUNDO AISLADO de la extensión: el JavaScript de la página no
// puede ver estas funciones, sus variables ni los elementos que guardan. No hace ninguna llamada de red, no guarda nada en
// el almacenamiento del navegador y nunca escribe en la consola.
(() => {
  'use strict';

  if (globalThis.__passdeskLib) {
    return;
  }

  const MIN_SIZE = 8;
  const MIN_OPACITY = 0.3;
  const TEXT_TYPES = new Set(['text', 'email', 'tel', 'url', 'search', '']);
  const USERNAME_HINT = /(user|usuario|login|log-?in|email|e-mail|mail|correo|account|cuenta|identificador|phone|tel[eé]fono|dni|nif)/i;
  const state = { anchor: null, overlays: new Set() };

  function isHttpsContext() {
    return location.protocol === 'https:' && location.origin !== 'null';
  }

  // Un marco de otro origen (o sin información sobre sus ancestros) nunca se rellena.
  function inCrossOriginFrame() {
    if (window === window.top) {
      return false;
    }
    const ancestors = location.ancestorOrigins;
    if (!ancestors) {
      return true;
    }
    for (let i = 0; i < ancestors.length; i++) {
      if (ancestors[i] !== location.origin) {
        return true;
      }
    }
    return false;
  }

  function effectiveOpacity(el) {
    let opacity = 1;
    for (let node = el; node; node = node.parentElement || (node.getRootNode() instanceof ShadowRoot ? node.getRootNode().host : null)) {
      opacity *= parseFloat(getComputedStyle(node).opacity);
      if (opacity < MIN_OPACITY) {
        return opacity;
      }
    }
    return opacity;
  }

  // Solo se rellenan campos que una persona ve y puede pulsar. Los campos ocultos, minúsculos, transparentes, fuera de la
  // pantalla o tapados por otro elemento son una trampa habitual para robar credenciales.
  function isVisible(el) {
    if (!el.isConnected) {
      return false;
    }
    if (typeof el.checkVisibility === 'function' && !el.checkVisibility({ checkOpacity: true, checkVisibilityCSS: true })) {
      return false;
    }
    const rect = el.getBoundingClientRect();
    if (rect.width < MIN_SIZE || rect.height < MIN_SIZE) {
      return false;
    }
    if (rect.bottom <= 0 || rect.right <= 0 || rect.top >= innerHeight || rect.left >= innerWidth) {
      return false;
    }
    if (effectiveOpacity(el) < MIN_OPACITY) {
      return false;
    }
    const root = el.getRootNode();
    const probe = root instanceof ShadowRoot || root instanceof Document ? root : document;
    const x = Math.min(Math.max(rect.left + rect.width / 2, 0), innerWidth - 1);
    const y = Math.min(Math.max(rect.top + rect.height / 2, 0), innerHeight - 1);
    // La sugerencia de PassDesk puede quedar justo encima del campo contiguo: no cuenta como algo que lo tape.
    const top = probe.elementsFromPoint(x, y).find((node) => !state.overlays.has(node));
    if (!top) {
      return false;
    }
    if (top === el || el.contains(top)) {
      return true;
    }
    const label = top.closest && top.closest('label');
    return Boolean(label && label.control === el);
  }

  // 'password' | 'username' | null. Los campos de contraseña nueva o de código de un solo uso no se ofrecen.
  function fieldKind(el) {
    if (!(el instanceof HTMLInputElement) || el.disabled || el.readOnly) {
      return null;
    }
    const type = (el.type || 'text').toLowerCase();
    const autocomplete = (el.getAttribute('autocomplete') || '').toLowerCase();
    if (autocomplete.includes('new-password') || autocomplete.includes('one-time-code') || autocomplete.includes('cc-')) {
      return null;
    }
    if (type === 'password') {
      return 'password';
    }
    if (!TEXT_TYPES.has(type)) {
      return null;
    }
    if (/\b(username|email)\b/.test(autocomplete)) {
      return 'username';
    }
    const hint = [el.name, el.id, el.getAttribute('aria-label'), el.placeholder].filter(Boolean).join(' ');
    return USERNAME_HINT.test(hint) ? 'username' : null;
  }

  function scopeOf(el) {
    if (el && el.form) {
      return el.form;
    }
    const root = el ? el.getRootNode() : document;
    return root instanceof ShadowRoot || root instanceof Document ? root : document;
  }

  function precedes(a, b) {
    return Boolean(a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING);
  }

  // Campos de un formulario de acceso: { username, password } (alguno puede faltar: accesos en dos pasos). Null si la página no
  // tiene un formulario de acceso claro (por ejemplo, registro o cambio de contraseña, con varias contraseñas).
  function findTargets(anchor) {
    const scope = scopeOf(anchor);
    const allPasswords = [...scope.querySelectorAll('input[type="password"]')];
    // Registro o cambio de contraseña (campos de contraseña nueva visibles): no es un acceso, no se ofrece nada.
    if (allPasswords.some((el) => /new-password/i.test(el.getAttribute('autocomplete') || '') && isVisible(el))) {
      return null;
    }
    const passwords = allPasswords.filter((el) => fieldKind(el) === 'password' && isVisible(el));
    if (passwords.length > 1) {
      return null;
    }
    const password = passwords[0] || null;
    let username = null;
    if (anchor && fieldKind(anchor) === 'username' && isVisible(anchor)) {
      username = anchor;
    } else if (password) {
      const candidates = [...scope.querySelectorAll('input')]
        .filter((el) => el !== password && precedes(el, password) && TEXT_TYPES.has((el.type || 'text').toLowerCase()) && !el.disabled && !el.readOnly && isVisible(el));
      // El más cercano que parezca de usuario; si ninguno lo parece, el campo de texto inmediatamente anterior.
      username = [...candidates].reverse().find((el) => fieldKind(el) === 'username') || candidates[candidates.length - 1] || null;
    } else if (!anchor) {
      username = [...scope.querySelectorAll('input')].find((el) => fieldKind(el) === 'username' && isVisible(el)) || null;
    }
    if (anchor && fieldKind(anchor) === 'password' && password !== anchor) {
      return null;
    }
    if (!username && !password) {
      return null;
    }
    return { username, password };
  }

  // Qué datos hacen falta: 'both' | 'username' | 'password' | null (no hay formulario de acceso).
  function plan(anchor) {
    const targets = findTargets(anchor || null);
    if (!targets) {
      return null;
    }
    if (targets.username && targets.password) {
      return 'both';
    }
    return targets.username ? 'username' : 'password';
  }

  function setValue(el, value) {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
    setter.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }

  // Escribe las credenciales en los campos visibles. Comprueba otra vez el origen del documento: si la página navegó entre la
  // elección y el relleno, no escribe nada. Devuelve solo cuántos campos rellenó, nunca los valores.
  function fillCredentials(request) {
    const { expectedOrigin, username, password } = request || {};
    if (typeof expectedOrigin !== 'string' || location.origin !== expectedOrigin || !isHttpsContext()) {
      return { ok: false, reason: 'origin' };
    }
    if (inCrossOriginFrame()) {
      return { ok: false, reason: 'frame' };
    }
    const anchor = state.anchor && state.anchor.isConnected ? state.anchor : null;
    const targets = findTargets(anchor);
    if (!targets) {
      return { ok: false, reason: 'no-target' };
    }
    let filled = 0;
    if (typeof username === 'string' && targets.username) {
      setValue(targets.username, username);
      filled++;
    }
    if (typeof password === 'string' && targets.password) {
      setValue(targets.password, password);
      filled++;
    }
    return filled > 0 ? { ok: true, filled } : { ok: false, reason: 'no-target' };
  }

  function setAnchor(el) {
    state.anchor = el || null;
  }

  // Elementos de la propia interfaz de PassDesk (solo los registra el script de contenido, que vive en el mundo aislado).
  function addOverlay(el) {
    state.overlays.add(el);
  }

  function removeOverlay(el) {
    state.overlays.delete(el);
  }

  Object.defineProperty(globalThis, '__passdeskLib', {
    value: Object.freeze({ isHttpsContext, inCrossOriginFrame, isVisible, fieldKind, findTargets, plan, fillCredentials, setAnchor, addOverlay, removeOverlay }),
    enumerable: false,
  });
})();
