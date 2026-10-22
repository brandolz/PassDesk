# Autorrelleno en el navegador

PassDesk puede rellenar tu usuario y contraseña en Chrome, Edge y Brave **cuando tú eliges la cuenta**. Es opcional, está
**desactivado por defecto** y todo ocurre en tu equipo: no hay servidor, nube ni conexión a internet.

> Modelo de amenazas y decisiones de seguridad: [NAVEGADOR-AMENAZAS.md](NAVEGADOR-AMENAZAS.md). Resumen general de
> seguridad de PassDesk: [SEGURIDAD.md](SEGURIDAD.md).

## Qué hace (y qué no)

- Al pulsar el icono de la extensión en una página con formulario de acceso, muestra las cuentas de PassDesk que
  corresponden a **esa página** (servicio y usuario; nunca la contraseña). Eliges una y rellena.
- Opcionalmente (por sitio, con tu permiso) muestra una sugerencia al enfocar el campo de usuario o de contraseña.
- Si hay varias cuentas para el mismo sitio, eliges cuál. Siempre puedes cancelar sin que se escriba nada.
- **No rellena nada al cargar una página.** No lee lo que escribes en los formularios ni lo envía a PassDesk. No guarda
  contraseñas nuevas.
- No rellena en páginas HTTP, en páginas internas del navegador (`chrome://`, `edge://`…), en páginas de extensiones ni en
  marcos (`iframe`) de otro origen. Los campos ocultos, transparentes, minúsculos o tapados nunca se rellenan.
- Con PassDesk **bloqueado** no entrega nada: pide que lo desbloquees con la contraseña maestra o Windows Hello, como
  siempre. Si se bloquea mientras esperas, la contraseña no sale.

## Estado de las pruebas (qué se ha probado de verdad)

| Componente | Cómo se ha comprobado |
|---|---|
| Lógica de orígenes, protocolo, intermediario, tubería y conector | Pruebas automáticas multiplataforma (`tests/PassDesk.Browser.Tests`), ejecutadas en Linux con .NET 10. |
| Extensión real + conector real + tubería real + bóveda **ficticia** | Pruebas de extremo a extremo (`tests/extension`) con **Chromium 141 en Linux** (modo headless). |
| Bóveda real (`VaultSession`, DPAPI, memoria protegida), ajustes, ventana de confirmación, verificación del conector, scripts `.ps1` | **Compilado**, pero **no ejecutado**: el entorno de desarrollo no era Windows. Las pruebas están escritas (`tests/PassDesk.Tests/BrowserAutofillTests.cs`); ejecútalas en tu equipo (ver «Probarlo en tu equipo»). |
| Chrome estable, Edge, Brave **en Windows** | **No probados.** Se consideran compatibles porque comparten Chromium, Manifest V3 y Native Messaging, pero no se ha verificado. |
| Firefox, Safari | No compatibles (otra API de extensiones). |

## Instalación (Windows, sin permisos de administrador)

Requisitos: PassDesk instalado con `scripts\publish.ps1` y `scripts\install.ps1` (la publicación incluye el conector
`PassDesk.NativeHost.exe` junto a `PassDesk.exe` y la extensión en `browser\extension`).

1. **Registrar el conector** para los navegadores que uses:

   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\install-browser.ps1                       # Chrome, Edge y Brave
   powershell -ExecutionPolicy Bypass -File scripts\install-browser.ps1 -Browsers Chrome,Edge  # solo algunos
   ```

   Escribe `%LOCALAPPDATA%\Programs\PassDesk\browser\com.passdesk.autofill.json` (con la ruta absoluta del conector, admite
   espacios y nombres de usuario con caracteres especiales) y lo registra en `HKCU` (no toca `HKLM`). El manifiesto solo
   admite el origen de la extensión de PassDesk.
2. **Cargar la extensión** (una vez por navegador). La carpeta es `%LOCALAPPDATA%\Programs\PassDesk\browser\extension`:
   - **Chrome:** `chrome://extensions` › activa *Modo de desarrollador* › *Cargar descomprimida* › elige la carpeta.
   - **Edge:** `edge://extensions` › activa *Modo de desarrollador* › *Cargar desempaquetada* › elige la carpeta.
   - **Brave:** `brave://extensions` › activa *Modo de desarrollador* › *Cargar descomprimida* › elige la carpeta.

   La extensión tiene un identificador fijo (`lemjmmcbbcdkkkpmchohlehobcljhcff`, derivado de la clave pública incluida en su
   manifiesto); el conector solo atiende a ese. Comprueba que el que muestra el navegador coincide.
3. **Activar la integración** en PassDesk › *Ajustes* › *Navegador*. Deja marcada *Pedir confirmación en PassDesk antes de
   cada relleno* (recomendado).
4. Deja PassDesk abierto (puede estar en la bandeja del sistema). Si está bloqueado, la extensión te lo dirá.

Si la extensión dice que PassDesk «no está abierto o la integración está desactivada», revisa: PassDesk en ejecución, casilla
activada en Ajustes, `PassDesk.NativeHost.exe` junto a `PassDesk.exe`, y que el identificador de la extensión coincide.

### Desinstalar

- **Solo el autorrelleno:** desmarca la casilla en PassDesk › Ajustes › Navegador (la tubería deja de existir al instante),
  o quita la extensión desde el navegador, o ejecuta:

  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts\uninstall-browser.ps1
  ```

  Quita únicamente las entradas del registro de PassDesk, el manifiesto y `allowed-origins.txt`.
- `scripts\uninstall.ps1` también llama a este script antes de borrar el programa. **Nunca** elimina la bóveda, los
  documentos ni las copias de seguridad (`%LOCALAPPDATA%\PassDesk`), salvo que tú lo pidas con `-RemoveData`, como antes.

## Uso

1. Abre la página de acceso y pulsa el icono de PassDesk (o `Ctrl+Mayús+L`, si tu navegador asignó el atajo).
2. Elige la cuenta. Con la confirmación activada, PassDesk te enseña una ventana con la cuenta, el usuario y la página;
   pulsa **Permitir** (o **Cancelar**, la opción por defecto: una pulsación de `Intro` despistada no autoriza).
3. *Sugerencias al enfocar los campos* (opcional): en la ventana de la extensión, pulsa «Mostrar sugerencias al enfocar los
   campos de este sitio» y concede el permiso a ese sitio. Después, al pulsar un campo de acceso aparece una sugerencia con
   el servicio y el usuario; `↓`/`↑` para moverte, `Intro` para elegir, `Esc` para cerrar. Se puede retirar en cualquier momento.

Accesos en dos pasos (primero el usuario, luego la contraseña): se rellena solo lo que hay en pantalla en cada paso, y a
la extensión solo llega lo que pide cada paso (en el primero, la contraseña no sale de PassDesk).

Formularios de registro o de cambio de contraseña (campos de «contraseña nueva») no se ofrecen.

## Arquitectura

```
 Página web ──(no puede hablar con la extensión)
     │ DOM
 ┌───▼───────────────────────────┐   sendNativeMessage    ┌──────────────────────┐  tubería local con nombre
 │ Extensión (Manifest V3)       │ ─────────────────────► │ PassDesk.NativeHost  │ ────────────────────────┐
 │  · content script (solo en    │  JSON ≤ 4 KiB          │  · solo la extensión │  solo el usuario actual │
 │    sitios que tú activas)     │ ◄───────────────────── │    autorizada        │                         ▼
 │  · service worker (único que  │                        │  · valida cada       │             ┌──────────────────────┐
 │    habla con el conector)     │                        │    mensaje           │             │ PassDesk (WPF)       │
 │  · ventana emergente          │                        │  · un proceso por    │             │  AutofillPipeServer  │
 └───────────────────────────────┘                        │    solicitud         │             │  AutofillBroker      │
                                                          └──────────────────────┘             │  SessionAutofillVault│
                                                                                               │  → VaultSession      │
                                                                                               └──────────────────────┘
```

- **Extensión** (`extension/`): detecta formularios, muestra la sugerencia y rellena. El service worker deduce el origen
  del remitente que le da el navegador (nunca de lo que diga la página).
- **Conector** (`src/PassDesk.NativeHost`): el navegador lo lanza por solicitud. Comprueba que lo lanzó la extensión
  autorizada (primer argumento), valida el mensaje (tamaño, tipos, campos), lo reenvía a PassDesk y devuelve la respuesta.
  No descifra, no guarda, no registra y termina tras responder. No hay procesos persistentes.
- **PassDesk** (`src/PassDesk.Browser`, `src/PassDesk.Auth/SessionAutofillVault.cs`, `src/PassDesk.App/Infrastructure/AutofillService.cs`):
  el **único** que decide. Reutiliza la sesión existente: no hay segunda bóveda ni segunda autenticación. El conector no
  puede desbloquear la bóveda; solo puede pedir que PassDesk muestre su pantalla de bloqueo.
- **Protocolo:** mensajes JSON con prefijo de longitud (formato de Native Messaging) y cuatro tipos: `status`, `list`,
  `fill` y `unlock`. No existe ninguna operación para listar o exportar toda la bóveda.

### Cómo se evita el relleno en el sitio equivocado

1. `list`: el navegador indica el origen de la pestaña. PassDesk lo vuelve a analizar de forma estricta (esquema HTTPS, host
   ASCII, puerto) y lo compara con la URL guardada de cada cuenta. Devuelve solo las coincidencias (servicio y usuario) y,
   para cada una, una **ficha de selección** aleatoria de un solo uso (válida 60 s) ligada a esa cuenta, a ese origen y a la
   «época» de desbloqueo actual.
2. `fill`: solo se acepta con una ficha válida. Si el origen, la cuenta o la época no coinciden, la ficha se destruye. Al
   entregar, PassDesk **vuelve a comprobar** el origen contra la URL guardada, y con confirmación activada, espera a que
   pulses **Permitir** (y vuelve a comprobar que sigue desbloqueada). Si te vas a otra página o te redirigen, la elección
   anterior no vale.
3. La contraseña se inyecta en el documento exacto desde el que se eligió (`documentId`), que vuelve a comprobar su origen.

Reglas de coincidencia (por defecto, **estrictas**):

| Caso | Resultado |
|---|---|
| Mismo host (con `www.` equivalente), mismo puerto, página HTTPS | Coincide |
| `ejemplo.com.evil.test`, `miejemplo.com`, `ejemplo.com@evil.test`, homógrafos IDN | No coincide |
| Otro puerto o página HTTP | No coincide |
| Otro subdominio (`login.ejemplo.com` ↔ `ejemplo.com`) | No coincide, salvo que actives *Ofrecer también cuentas guardadas para otro subdominio*; entonces se ofrece avisando de dónde se guardó |
| Plataformas de alojamiento compartido (`a.github.io` ↔ `b.github.io`) | Nunca coinciden entre sí |
| Direcciones IP | Solo coincidencia exacta |

## Permisos de la extensión

| Permiso | Para qué | Alcance |
|---|---|---|
| `nativeMessaging` | Hablar con el conector local `com.passdesk.autofill` | Solo ese conector, y el navegador solo deja usarlo a esta extensión |
| `activeTab` | Leer el origen y rellenar la pestaña cuando pulsas el icono | Temporal, solo la pestaña actual |
| `scripting` | Inyectar el código de relleno en el documento elegido | Solo donde ya hay acceso (`activeTab` o un sitio que tú activaste) |
| `optional_host_permissions: https://*/*` | Pedir, **sitio a sitio**, permiso para las sugerencias al enfocar | Opcional: el navegador te lo pregunta por cada sitio; nada se concede de antemano |

No tiene `host_permissions`, ni `content_scripts` estáticos, ni `storage`, ni `tabs`, ni `externally_connectable`, ni recursos
accesibles desde la web. No usa red (CSP con `connect-src 'none'`). Una prueba automática verifica todo esto.

## Limitaciones conocidas

- **Windows Hello / contraseña maestra:** desbloquear sigue haciéndose en PassDesk. La extensión no puede desbloquear.
- **PassDesk debe estar en ejecución.** No se lanza solo desde el navegador (para no crear procesos persistentes).
- **La página puede leer lo que se rellena.** Es inevitable en cualquier autorrelleno: por eso solo se rellena tras tu
  elección, solo en el origen guardado y solo en campos visibles. Una página legítima comprometida (XSS) del mismo origen
  podría leerlo.
- **Malware con tu usuario** (que lea memoria del navegador o de PassDesk, o simule clics) queda fuera de lo que cualquier
  gestor puede impedir; la confirmación nativa y la comprobación del conector lo dificultan, no lo impiden.
- Las cadenas de JavaScript no se pueden borrar de memoria: la contraseña vive en el navegador durante unos instantes.
- La lista de dominios públicos para coincidencias por subdominio es **aproximada** (no es la Public Suffix List completa);
  por eso esa opción está desactivada por defecto.
- Formularios no estándar (campos creados en `canvas`, accesos en ventanas emergentes de otro dominio, marcos de otro origen)
  no se rellenan. Los accesos con varios campos de contraseña (registro, cambio) tampoco.
- Si la página enfoca el campo sola (`autofocus`), no aparece la sugerencia hasta que pulses; el icono de la extensión
  funciona siempre.
- Navegadores no Chromium (Firefox, Safari) no están soportados.
- No hay extensión publicada en ninguna tienda: se carga sin empaquetar, y Chrome puede recordarte que está en modo desarrollador.

## Ajustes (PassDesk › Ajustes › Navegador)

| Ajuste | Por defecto | Efecto |
|---|---|---|
| Permitir que la extensión rellene contraseñas | **Desactivado** | Crea o cierra la tubería local al instante |
| Pedir confirmación en PassDesk antes de cada relleno | Activado | Ventana nativa con cuenta, usuario y página; `Cancelar` por defecto |
| Ofrecer cuentas de otro subdominio del mismo sitio | Desactivado | Coincidencias «relacionadas» con aviso |

## Pruebas

```powershell
# Lógica multiplataforma (también en Linux/macOS): orígenes, protocolo, intermediario, tubería, conector
dotnet test tests\PassDesk.Browser.Tests\PassDesk.Browser.Tests.csproj

# Integración con la bóveda real y el resto de PassDesk (solo Windows)
dotnet test tests\PassDesk.Tests\PassDesk.Tests.csproj

# Extensión real en Chromium + conector real + bóveda ficticia (necesita .NET 10, Node 20+ y OpenSSL; ver tests\extension)
cd tests\extension; npm install; npm test
```

Las pruebas de extremo a extremo usan **credenciales ficticias** (`SENTINEL-…`) y un perfil de navegador temporal; nunca tu
bóveda. Comprueban, entre otras cosas, que ninguna contraseña queda en el perfil del navegador ni en los registros.

### Probar el prototipo sin tocar tu bóveda

`tools\PassDesk.AutofillSandbox` es el mismo intermediario y la misma tubería con una bóveda ficticia en memoria:

```powershell
dotnet run --project tools\PassDesk.AutofillSandbox      # órdenes: lock | unlock | confirm yes|no|ask | answer yes|no | subdomains on|off | quit
```

(El conector `PassDesk.NativeHost.exe` y la extensión se registran igual que arriba; no arranques PassDesk a la vez si usas la
misma carpeta de datos, porque ambos quieren la misma tubería.)

### Probarlo en tu equipo (lista de comprobación)

1. `dotnet test` de los dos proyectos .NET: deben pasar **todas** (las 236 de siempre + las nuevas).
2. `scripts\publish.ps1`, `scripts\install.ps1`, `scripts\install-browser.ps1`; cargar la extensión; activar la casilla.
3. Con una cuenta de prueba (no la real): rellenar desde el icono; con *Pedir confirmación*, comprobar que aparece la
   ventana, que `Cancelar` no rellena y que `Permitir` sí.
4. Bloquear PassDesk (`Ctrl+L`): el icono debe decir «bloqueado» y no rellenar; «Abrir PassDesk» muestra la pantalla de bloqueo.
5. Abrir la misma página por `http://` y con un dominio parecido: no debe ofrecer la cuenta.
6. Comprobar con `%LOCALAPPDATA%\PassDesk\logs\passdesk.log` que solo hay códigos y contadores.
7. Si `PassDesk.NativeHost.exe` no está junto a `PassDesk.exe`, o el navegador no lo lanza, el registro mostrará
   `BrowserAutofillClientRejected`; revisa la ruta del conector y que lo lanza el navegador (no a mano).
