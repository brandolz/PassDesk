# Autorrelleno en el navegador: modelo de amenazas

Complementa [SEGURIDAD.md](SEGURIDAD.md). Describe qué se protege, de quién, con qué controles y qué riesgos quedan. Las
pruebas que respaldan cada control se indican entre corchetes: `[B]` = `tests/PassDesk.Browser.Tests`, `[W]` =
`tests/PassDesk.Tests/BrowserAutofillTests.cs` (solo Windows), `[E]` = `tests/extension/e2e.test.js`.

## Qué se protege

1. Las contraseñas y usuarios de la bóveda (no deben salir sin una elección explícita del usuario).
2. La propia bóveda: ni la integración ni la extensión pueden desbloquearla, exportarla ni enumerarla.
3. La contraseña maestra y la clave de datos: no pasan nunca por la extensión ni por el conector.

## Fronteras de confianza

```
página web (no fiable) ─x─ extensión (script de contenido, mundo aislado)
extensión ─ Native Messaging (solo la extensión autorizada) ─ conector (valida) ─ tubería local (solo el usuario) ─ PassDesk (decide)
```

- **No fiable:** el contenido y el JavaScript de las páginas, todo lo que un navegador diga sobre una URL sin comprobar
  contra la bóveda, cualquier proceso que no sea el conector instalado.
- **Fiable (con matices):** el navegador (origen del remitente, aislamiento de extensiones), el sistema de usuarios de
  Windows, PassDesk.

## Amenazas y controles

| # | Amenaza | Control | Riesgo residual |
|---|---|---|---|
| 1 | Una página pide credenciales a la extensión (mensajes, eventos, `postMessage`) | La extensión no escucha nada de la página; el script de contenido vive en el mundo aislado; `externally_connectable` no existe. Solo reacciona a clics y teclas **reales** (`isTrusted`). [E: página hostil] | Ninguno conocido |
| 2 | Una página provoca la sugerencia con `focus()`/`click()` sintéticos para espiar | La sugerencia exige una interacción real reciente del usuario y no abre nada si la página enfoca por código. La interfaz está en una raíz sombra **cerrada**: la página no puede leerla. [E] | Una página puede saber que existe un elemento superpuesto, no su contenido |
| 3 | Clickjacking: la página tapa o vuelve transparente la sugerencia | La sugerencia usa la capa superior (`popover`); antes de aceptar un clic se comprueba que sigue visible, opaca y sin transformaciones, y que lleva visible ≥ 350 ms. Además, con la confirmación nativa, la decisión final está en una ventana de PassDesk que la página no controla | Mitigación de mejor esfuerzo; la ventana nativa es el control firme |
| 4 | Sitio equivocado: dominio engañoso (`ejemplo.com.evil.test`), homógrafo, usuario en la URL, otro puerto, otro esquema | Comparación por host/puerto/esquema enteros, nunca por texto; punycode; origen estricto (sin usuario, ruta ni puntos finales). [B: más de 100 casos] [E] | La opción de subdominios usa una lista de sufijos públicos aproximada (desactivada por defecto) |
| 5 | Página HTTP o interna del navegador | Se exige HTTPS en la extensión **y** en PassDesk; `chrome://`, `edge://`, `chrome-extension://`, `file:`, `about:` se rechazan. [B] [E] | Ninguno: no hay excepción para HTTP |
| 6 | `iframe` de otro origen (p. ej. un marco hostil que embebe tu banco) | El script de contenido no actúa si algún ancestro es de otro origen; el service worker lo vuelve a comprobar con la URL de la pestaña; la ventana de la extensión solo opera en el documento principal. [E] | Marcos del mismo origen sí se admiten |
| 7 | Campos trampa (ocultos, transparentes, fuera de pantalla, tapados) para robar el relleno | Solo se rellenan campos visibles, de tamaño razonable, no tapados y con opacidad real. [E: trampa] | Un campo visible pero engañoso en la propia página sigue siendo visible para el usuario |
| 8 | La elección se reutiliza tras una navegación o redirección | Fichas de un solo uso (60 s) ligadas a cuenta + origen + época de desbloqueo; el relleno apunta al `documentId` exacto y se vuelve a comprobar el origen en el documento. [B] [E: navegar tras abrir] | Ninguno conocido |
| 9 | Suplantar a la extensión (otra extensión o programa habla con el conector) | `allowed_origins` del navegador (una sola extensión); el conector exige el origen exacto como primer argumento; PassDesk solo acepta conexiones del conector instalado junto a `PassDesk.exe`. [B] [E: extensión no autorizada] | Malware con tu usuario puede lanzar el conector a mano (ver 12) |
| 10 | Mensajes malformados, enormes o de tipos inesperados | Validación estricta en el conector **y** en PassDesk: 4 KiB, campos conocidos, sin duplicados, tipos y formatos exactos; JSON con profundidad limitada. Error genérico. [B] [E] | Ninguno conocido |
| 11 | Abuso por repetición (adivinar fichas, inundar la tubería, robar el foco) | Límites por tipo y global, bloqueo de rellenos tras 5 fichas inválidas en un minuto, un relleno a la vez, 4 conexiones, tiempos máximos de lectura y de atención. [B] [E] | Denegación de servicio local de la propia función (el usuario puede reintentar) |
| 12 | Otro proceso del mismo usuario usa la tubería como «oráculo» para sacar contraseñas | Tubería solo del usuario actual; PassDesk verifica la ruta del ejecutable cliente; las fichas obligan a listar antes de rellenar; **confirmación nativa** por relleno (activada por defecto) | Un programa con tu usuario puede simular clics en la ventana de confirmación o leer la memoria: queda fuera del alcance de cualquier gestor de contraseñas (ya lo dice SEGURIDAD.md) |
| 13 | Entregar un secreto con la bóveda bloqueada | Lectura bajo el cerrojo de la sesión; fichas y época anuladas al bloquear; se revisa de nuevo tras la confirmación. [B] [W] [E: bloqueo durante la confirmación] | Ninguno conocido |
| 14 | Eludir la contraseña maestra / Windows Hello | La integración no desbloquea: pedir el desbloqueo solo muestra la pantalla de PassDesk. No hay copia descifrada para «acelerar» | Ninguno |
| 15 | Secretos en registros, consola, errores o disco | Registros solo con códigos y contadores; errores genéricos; respuestas con contraseña escritas en memoria fijada que se pone a cero; la extensión no usa consola ni almacenamiento; sin ajustes con secretos. [B] [W] [E: perfil del navegador] | Cadenas de JavaScript y de .NET no borrables: vida corta, «mejor esfuerzo» |
| 16 | Servidor local expuesto a cualquier página | No hay HTTP ni WebSocket; tubería con nombre solo para el usuario actual y sin clientes remotos | Ninguno |
| 17 | La extensión pide permisos de más | Sin permisos de host por defecto; acceso por sitio, a petición. [E: manifiesto] | Ninguno |

## Fuera de alcance

- Malware con tu usuario mientras PassDesk está desbloqueado (memoria, teclado, portapapeles, simulación de clics). Igual que
  el resto de PassDesk.
- Un navegador comprometido (un exploit del motor de renderizado puede saltarse el aislamiento de extensiones).
- Sitios legítimos comprometidos (XSS) que lean el formulario una vez rellenado.
- Ataques de red: no hay red.

## Decisiones que conviene conocer

- **Confirmación nativa activada por defecto.** Añade un clic, pero cierra el hueco más importante: que cualquier cosa que
  hable con la tubería obtenga contraseñas solo con listar y rellenar. Se puede desactivar en Ajustes.
- **Coincidencia estricta por defecto.** Un sitio que inicia sesión en otro subdominio no se rellena hasta que se activa la
  opción. Es menos cómodo y más seguro.
- **Integración desactivada por defecto.** Hasta que la activas, PassDesk no abre ninguna tubería.
- **El conector no es persistente.** Un proceso por solicitud, sin estado.
- **Lo que no se hizo:** excepción para HTTP en `localhost` (no se ha encontrado una necesidad que la justifique), lanzar
  PassDesk desde el navegador, rellenar al cargar la página, guardar contraseñas nuevas desde el navegador.
