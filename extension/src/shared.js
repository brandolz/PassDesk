// Validación común (módulo ES). Todo lo que llega de PassDesk o de otros contextos de la extensión se trata como no fiable:
// se comprueba el tipo, la longitud y el formato, y solo se conservan los campos conocidos.

export const HOST_NAME = 'com.passdesk.autofill';
export const PROTOCOL_VERSION = 1;

const GUID = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const TOKEN = /^[A-Za-z0-9_-]{22,64}$/;
const REQUEST_ID = /^[A-Za-z0-9_-]{1,64}$/;
const SCOPES = new Set(['both', 'username', 'password']);
const KNOWN_ERRORS = new Set([
  'bad_request', 'too_large', 'unsupported_origin', 'insecure_origin', 'disabled', 'not_created', 'locked', 'not_found',
  'denied', 'rate_limited', 'busy', 'expired', 'internal', 'unavailable', 'unauthorized_caller',
]);

export const isGuid = (value) => typeof value === 'string' && GUID.test(value);
export const isToken = (value) => typeof value === 'string' && TOKEN.test(value);
export const isScope = (value) => typeof value === 'string' && SCOPES.has(value);

export function newRequestId() {
  const bytes = crypto.getRandomValues(new Uint8Array(12));
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

// Origen HTTPS de una cadena (la de sender.origin o la de una URL). Devuelve null para cualquier otra cosa: páginas internas
// del navegador, extensiones, http, archivos, «null»…
export function httpsOrigin(text) {
  if (typeof text !== 'string' || text.length > 300) {
    return null;
  }
  try {
    const url = new URL(text);
    if (url.protocol !== 'https:' || url.username || url.password || !url.hostname) {
      return null;
    }
    return url.origin;
  } catch {
    return null;
  }
}

// Los patrones de coincidencia de las extensiones no llevan puerto: el acceso se concede al sitio (host), y PassDesk sigue
// comprobando el origen completo (con puerto) en cada solicitud.
export function siteKey(origin) {
  return new URL(origin).hostname;
}

export function sitePattern(origin) {
  return `https://${siteKey(origin)}/*`;
}

const clip = (value, max) => (typeof value === 'string' ? value.slice(0, max) : '');

// Convierte una respuesta del conector en un objeto simple y acotado, o en un error genérico.
export function normalizeReply(reply, requestId, expectedOrigin) {
  if (!reply || typeof reply !== 'object' || reply.v !== PROTOCOL_VERSION || typeof reply.ok !== 'boolean' || (reply.rid !== undefined && reply.rid !== requestId)) {
    return { ok: false, error: 'bad_reply' };
  }
  if (!reply.ok) {
    return { ok: false, error: KNOWN_ERRORS.has(reply.error) ? reply.error : 'internal' };
  }
  const result = { ok: true };
  if (typeof reply.state === 'string') {
    result.state = ['ready', 'locked', 'not_created', 'disabled'].includes(reply.state) ? reply.state : 'internal';
  }
  if (reply.unlockRequested === true) {
    result.unlockRequested = true;
  }
  if (reply.matches !== undefined) {
    if (!Array.isArray(reply.matches) || reply.matches.length > 20 || reply.origin !== expectedOrigin) {
      return { ok: false, error: 'bad_reply' };
    }
    result.matches = [];
    for (const match of reply.matches) {
      if (!match || !isGuid(match.id) || !isToken(match.token) || (match.kind !== 'exact' && match.kind !== 'related')) {
        return { ok: false, error: 'bad_reply' };
      }
      result.matches.push({
        id: match.id,
        token: match.token,
        kind: match.kind,
        title: clip(match.title, 200),
        username: clip(match.username, 320),
        site: clip(match.site, 253),
      });
    }
  }
  // Un secreto nunca se recorta: o se entrega entero o no se entrega.
  for (const field of ['username', 'password']) {
    if (reply[field] !== undefined) {
      if (typeof reply[field] !== 'string' || reply[field].length > 4096) {
        return { ok: false, error: 'bad_reply' };
      }
      result[field] = reply[field];
    }
  }
  return result;
}

export { REQUEST_ID };
