# Seguridad de PassDesk

Este documento describe cómo protege PassDesk tus credenciales, qué amenazas cubre y cuáles no.

## Modelo de amenazas

**Protege frente a:**

- Robo o copia del archivo de la bóveda, de copias de seguridad del disco o del equipo apagado.
- Otros usuarios de Windows del mismo PC.
- Miradas ajenas, capturas de pantalla, grabaciones y pantalla compartida (Teams, Zoom…).
- Fugas por el portapapeles (historial `Win+V`, portapapeles en la nube, contenido olvidado).
- Fugas en registros, mensajes de error o notificaciones.
- Adivinar la contraseña maestra probando muchas veces en la propia app.
- Páginas web que intentan obtener credenciales a través de la extensión del navegador, o que se hacen pasar por otro sitio
  (si activas el autorrelleno, ver «Autorrelleno en navegadores»).

**No puede proteger frente a** (ningún gestor de contraseñas puede):

- Malware ejecutándose con tu usuario mientras la bóveda está desbloqueada (registradores de teclas, lectura de
  memoria del proceso, lectura del portapapeles).
- Un administrador del equipo con acceso a tu sesión iniciada.
- Alguien que use tu PC con tu sesión de Windows y PassDesk desbloqueados (por eso existe el bloqueo automático).

## Cifrado

| Elemento | Mecanismo |
|---|---|
| Contenido de la bóveda | AES‑256‑GCM (`System.Security.Cryptography.AesGcm`, CNG de Windows). Nonce aleatorio de 96 bits en cada guardado; etiqueta de 128 bits. La cabecera completa se autentica como datos asociados (AAD). |
| Clave de datos (DEK) | 256 bits aleatorios (`RandomNumberGenerator`). Se guarda envuelta con AES‑256‑GCM bajo la KEK. Se regenera al cambiar la contraseña maestra. |
| Clave de cifrado de claves (KEK) | **Argon2id** (RFC 9106) con **64 MiB** de memoria, 3 pasadas y 1 hilo, calculado con libsodium (mediante NSec); sal aleatoria de 128 bits. Las bóvedas de versiones anteriores usan PBKDF2‑HMAC‑SHA256 con 600.000 iteraciones y se pasan a Argon2id al desbloquearlas (antes de guardar se comprueba que el nuevo llavero abre con la contraseña y recupera la misma clave de datos). Esa conversión **nunca impide desbloquear**: si falla por cualquier motivo (libsodium no carga, el guardado falla…), la bóveda se abre con PBKDF2 y se reintenta en el siguiente desbloqueo. |
| Capa local | **DPAPI** (`ProtectedData`, ámbito `CurrentUser`) con entropía propia de la aplicación sobre el sobre ya cifrado. |
| En memoria | Contraseñas, notas, semillas TOTP y la DEK se guardan cifradas con `CryptProtectMemory` (solo descifrables por este proceso) en búferes fijados que se ponen a cero al liberarse. Al guardar, esos secretos se escriben en Base64 directamente en un búfer fijado: como texto JSON, .NET los escaparía (tildes, `+`, `&`…) en un búfer compartido (`ArrayPool`) que no se borra. La contraseña maestra se lee del `PasswordBox` como `SecureString` y pasa a la derivación de claves (Argon2id; PBKDF2 solo en bóvedas antiguas) desde memoria fijada, sin crear cadenas. |
| Documentos | Cada archivo se cifra con **su propia clave** AES‑256‑GCM aleatoria (nonce aleatorio de 96 bits, etiqueta de 128 bits). La cabecera y el identificador del documento son datos asociados (AAD), así que un archivo cifrado no puede cambiarse por otro. Las claves y los metadatos (título, nombre original, tamaño, SHA‑256, fechas) viven **dentro de la bóveda cifrada**; encima va la misma capa DPAPI. |

No se ha inventado ningún algoritmo: solo se combinan primitivas estándar en el esquema habitual de «cifrado por
sobre». Argon2id es «memory-hard»: cada intento necesita 64 MiB, lo que encarece mucho los ataques con GPU contra una
copia robada. Importa sobre todo en las copias `.pdbackup`, que no llevan la capa DPAPI. En la bóveda local, además,
la capa DPAPI impide atacar un archivo robado sin las credenciales de Windows del usuario. El KDF y su coste forman parte de los datos
autenticados de la cabecera: no se pueden rebajar sin que falle el descifrado. Una prueba fija la salida de Argon2id
para detectar cualquier cambio de la biblioteca que pudiera dejar bóvedas sin abrir.

Si libsodium no se puede cargar (por ejemplo, porque un antivirus o AppLocker bloquean la DLL), las bóvedas antiguas se
siguen abriendo sin convertirse. Lo que necesita Argon2id (abrir una bóveda ya convertida, crear una, cambiar la
contraseña maestra o restaurar una copia) muestra «No se pudo cargar el componente de cifrado (libsodium). Reinstala
PassDesk o revisa el antivirus» en lugar de un error genérico, no cuenta como intento fallido y nunca pasa a una
protección más débil.

### Formatos de archivo

- `vault.pdv` (bóveda local): `"PDLV"` | versión | tipo de protección | blob DPAPI( sobre ).
- Sobre (`"PDE1"`): versión | KDF (1 = PBKDF2, 2 = Argon2id 64 MiB) | coste (iteraciones o pasadas) | sal | nonce + DEK
  envuelta + etiqueta | nonce de datos | etiqueta | longitud | datos cifrados.
- Datos cifrados: JSON con `"v":2`, con contraseñas, notas, semillas TOTP y claves de documentos en Base64. El formato
  `"v":1` (secretos como texto) se sigue leyendo y pasa al 2 en el siguiente guardado. Las versiones de PassDesk
  anteriores a este cambio no abren una bóveda guardada con `"v":2`: la dan por dañada (no lo está).
- `recuperacion\vault-<fecha>-<motivo>.pdv`: puntos de recuperación, copias exactas de `vault.pdv` (misma protección)
  guardadas antes de cada importación o restauración; se conservan las 10 más recientes.
- `docs\<id>.pdd` (un archivo por documento, con nombre aleatorio): `"PDDL"` | versión | tipo de protección | blob
  DPAPI( `"PDD1"` | versión | nonce | etiqueta | longitud | datos cifrados ).
- `*.pdbackup` (copia de seguridad): `"PDBK"` | versión | sobre | (versión 2) documentos cifrados con su identificador.
  **Sin DPAPI**, para poder restaurarla en otro equipo: su protección es solo la contraseña maestra que tenías al
  crearla (las claves de los documentos van dentro del sobre). Las copias de la versión 1 siguen restaurándose.
- `vault.pdv.bak`: versión anterior de la bóveda (también cifrada), por si una escritura fallara. Al cambiar la
  contraseña maestra se guarda dos veces, para que el `.bak` tampoco se abra ya con la contraseña antigua (si el
  segundo guardado fallara, el cambio sigue hecho y el `.bak` se sustituye en el siguiente guardado).
- `security.json`: contador de intentos fallidos, cifrado con DPAPI (ver «Autenticación y bloqueo»).
- La escritura es atómica (archivo temporal + reemplazo), para no dejar nunca una bóveda a medias.

Al abrir la bóveda, cualquier manipulación (un solo bit) se detecta por la verificación de integridad de GCM.

## DPAPI: cambios de usuario, migraciones y copias

DPAPI cifra con claves derivadas de las credenciales de **tu cuenta de Windows en este equipo** (guardadas en
`%APPDATA%\Microsoft\Protect`). Consecuencias prácticas:

| Situación | ¿Se puede abrir la bóveda? | Qué hacer |
|---|---|---|
| Cambias tu contraseña de Windows de forma normal (o usas PIN/Windows Hello para iniciar sesión) | ✅ Sí | Nada: Windows vuelve a proteger sus claves automáticamente. |
| Otro usuario de Windows del mismo PC | ❌ No | Cada usuario tiene su propia bóveda en su perfil. |
| Un administrador **restablece** (no cambia) la contraseña de tu cuenta local | ❌ Normalmente no | Restaura una copia `.pdbackup`. |
| Copias `vault.pdv` a otro equipo | ❌ No | Usa *Ajustes › Exportar copia cifrada* y restáurala en el nuevo equipo. |
| Reinstalas Windows o creas un perfil nuevo | ❌ No | Igual: restaura una copia `.pdbackup` (o vuelve a importar desde Apple). |
| Restauras una copia del perfil completo (incluido `%APPDATA%\Microsoft\Protect`) en el mismo usuario y equipo | ✅ Normalmente sí | — |

Si PassDesk no puede descifrar la capa DPAPI, lo indica en la pantalla de bloqueo y ofrece **archivar** la bóveda
ilegible (se renombra, no se borra) para crear una nueva y restaurar la copia desde *Ajustes*.

Recomendación: exporta una copia `.pdbackup` después de cada importación importante y guárdala en un lugar distinto del
PC (USB o nube). Al cambiar la contraseña maestra, las copias `.pdbackup` y los puntos de recuperación anteriores siguen
abriéndose con la contraseña antigua (es lo que permite restaurarlos); `vault.pdv.bak`, no. PassDesk lo explica en ese
momento y ofrece borrar los puntos de recuperación; las copias `.pdbackup` tienes que borrarlas tú (ver «Guardado y
recuperación»).

## Autenticación y bloqueo

- Contraseña maestra obligatoria al iniciar: mínimo **12 caracteres y fortaleza estimada «Fuerte»** (una estimación
  orientativa por longitud y variedad; dos palabras comunes en minúsculas ya no se aceptan). Las bóvedas creadas con la
  política anterior se siguen abriendo; si la contraseña no llega al nivel actual, PassDesk lo detecta al desbloquear y
  avisa antes de exportar una copia de seguridad, que es lo que podría atacarse sin límite de intentos.
- Bloqueo automático por inactividad (1–60 min), al bloquear Windows (`Win+L`) o cerrar la sesión remota, al suspender
  el equipo, opcionalmente al minimizar o al pasar a la bandeja, y manual (`Ctrl+L` o *Bloquear ahora* en el menú del
  icono). Al bloquear se descartan de memoria todas las credenciales descifradas y se borra el portapapeles si contiene
  algo copiado por PassDesk.
- Intentos fallidos: sin espera hasta el 3.º; después 5 s, 15 s, 30 s, 1 min y 5 min. El contador se guarda en disco
  (`security.json`, cifrado con DPAPI), así que reiniciar la app no lo evita. Los mensajes son genéricos.
  - Si el archivo existe pero está dañado, no se puede leer o tiene valores imposibles (contador negativo o una espera
    de más de 5 min), se aplica la **espera máxima** (5 min) en lugar de volver a cero, y se guarda para que reiniciar
    no la acorte. Si no existe, es el estado inicial. El formato anterior, sin cifrar, se sigue leyendo.
  - Si lo que este equipo no puede descifrar es la propia bóveda (por ejemplo, tras copiar la carpeta de datos a otro
    PC), se avisa de eso antes de cualquier espera, porque leer el archivo no comprueba la contraseña. Al crear una
    bóveda nueva, el contador vuelve a cero.
  - La cuenta atrás solo dice «Contraseña incorrecta» si de verdad se ha comprobado la contraseña.
  - Límite: quien controle tu sesión de Windows puede borrar el archivo y volver al estado inicial. Con ese acceso
    también podría copiar la bóveda y atacarla sin conexión, sin límite de intentos: frente a eso solo protege una
    contraseña maestra fuerte.
- **Windows Hello** (`UserConsentVerifier`), **desactivado por defecto**. Si se activa, solo permite *volver* a
  desbloquear: la DEK se conserva cifrada con `CryptProtectMemory` y Windows Hello verifica tu presencia. Límites:
  - Como mucho **8 horas** desde la última vez que se escribió la contraseña maestra; una comprobación periódica olvida
    la clave al caducar, y también si el reloj retrocede.
  - **Nunca** después de bloquear Windows o suspender el equipo: la clave se olvida aunque PassDesk ya estuviera
    bloqueado, e incluso si está desactivado «Bloquear al bloquear Windows».
  - Al reiniciar PassDesk, la DEK no existe en ningún sitio y se exige la contraseña maestra.

  Windows Hello no está ligado criptográficamente a la clave. Mientras está disponible, quien conozca el PIN de Windows
  puede abrir PassDesk; por eso viene desactivado.

## Portapapeles

- Al copiar, PassDesk publica también los formatos `ExcludeClipboardContentFromMonitorProcessing`,
  `CanIncludeInClipboardHistory = 0` y `CanUploadToCloudClipboard = 0`, que Windows usa para **no guardar el contenido
  en el historial (`Win+V`) ni sincronizarlo** con otros dispositivos.
- Se borra tras el intervalo configurado (10–120 s), al bloquear y al salir, **solo si el número de secuencia del
  portapapeles no ha cambiado** desde la copia: nunca se borra algo que hayas copiado después desde otra aplicación.
- Si otra aplicación tiene el portapapeles abierto en ese momento, PassDesk **conserva la propiedad y lo reintenta**
  cada 2 s (hasta 1 minuto), de nuevo al bloquear y varias veces al salir. Antes de la auditoría, un solo fallo hacía que
  la contraseña se quedara copiada indefinidamente.
- **Copiar desde cuadros de texto** (notas, contraseña visible en el editor, generador, documentos de texto) con
  `Ctrl+C`, `Ctrl+X`, `Ctrl+Insert`, `Mayús+Supr` o el menú contextual pasa por la misma protección, en lugar del
  portapapeles normal de WPF.

## Interfaz

- Contraseñas ocultas por defecto; al revelarlas se ocultan solas a los 30 s, al cambiar de cuenta o de ventana.
- `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`: las ventanas de PassDesk no aparecen en capturas, grabaciones ni
  pantalla compartida (activable en Ajustes).
- La vista previa de importación nunca muestra contraseñas, notas ni códigos.
- Los enlaces solo se abren si son `http`/`https` (nunca `file:`, `javascript:` ni protocolos de terceros que podrían
  venir en datos importados).
- Los iconos de los servicios se generan localmente (inicial + color): no se descargan favicons, así ningún servidor
  sabe qué cuentas tienes.

## Bandeja del sistema e inicio con Windows

- **El icono no revela datos:** su texto («PassDesk») y su menú (*Abrir*, *Bloquear ahora*, *Búsqueda rápida*,
  *Salir*) son fijos; nunca muestran cuentas, usuarios ni cuántas hay. Lo único que cambia es que *Bloquear ahora*
  aparece desactivado si la bóveda ya está bloqueada. PassDesk no muestra notificaciones de Windows. Una prueba
  automática lo comprueba con una bóveda con datos, abierta y bloqueada.
- **Pasar a la bandeja equivale a minimizar:** se ocultan las contraseñas reveladas y se aplican las mismas reglas de
  bloqueo («Bloquear al minimizar la ventana o pasarla a la bandeja», inactividad, `Win+L`, suspensión). Por eso, con
  el ajuste por defecto, **cerrar la ventana ya no bloquea al momento**: hasta el bloqueo por inactividad (5 min por
  defecto), la búsqueda rápida sigue dando acceso a las contraseñas. Si quieres que se bloquee al cerrarla, activa esa
  opción.
- **Salir** (menú del icono) hace la salida de siempre: quita el icono, bloquea la bóveda, borra el portapapeles si
  contiene algo de PassDesk, escribe el registro y, si se usó el visor PDF, termina el proceso (ver «Documentos»).
- Si el icono no se pudo añadir (por ejemplo, porque el Explorador de Windows no se está ejecutando), cerrar la ventana
  sale de PassDesk en lugar de dejarla oculta y sin forma de volver a ella; cuando el Explorador arranca, el icono se
  añade.
- **Iniciar con Windows** (desactivado por defecto) crea el valor `PassDesk` en
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`: la ruta del ejecutable instalado entre comillas y `--tray`, nada
  más. Es una clave de tu usuario, sin permisos de administrador. Con `--tray`, PassDesk arranca sin ventana y
  **bloqueado** (al arrancar siempre lo está: la clave de la bóveda no se guarda en ningún sitio).
  - Solo se ofrece en la copia instalada (`%LOCALAPPDATA%\Programs\PassDesk`) y con la carpeta de datos por defecto: una
    compilación de prueba o un perfil de `PASSDESK_DATA_DIR` nunca tocan la entrada (y al iniciar sesión no se abriría
    ese perfil).
  - PassDesk solo borra la entrada si apunta a su propio ejecutable, el registro solo se toca si cambias la casilla, y
    `uninstall.ps1` la quita. Las pruebas automáticas usan una clave en memoria y nunca escriben en el registro real.
  - Límite: como cualquier entrada de esa clave, un programa que se ejecute con tu usuario puede cambiarla para que
    apunte a otro ejecutable; PassDesk mostraría entonces la casilla desmarcada, porque la ruta ya no es la suya.

## Documentos

- Al añadir un archivo se lee una vez, se cifra y se guarda; si es idéntico a uno ya guardado (mismo SHA‑256) no se
  duplica. Límite: 50 MB por archivo. Después, PassDesk ofrece **eliminar de forma segura los originales** sin cifrar
  (mismas limitaciones en SSD y carpetas sincronizadas que en la importación).
- **Visor sin archivos temporales:** el contenido se descifra en memoria fijada que se pone a cero en cuanto la imagen,
  el PDF o el texto se ha decodificado. Al bloquear o cambiar de documento se descarta la vista previa. La protección
  contra capturas de pantalla cubre también el visor.
- Los PDF se dibujan con el motor PDF integrado en Windows (`Windows.Data.Pdf`), en memoria y en un hilo de fondo; a la
  interfaz solo llegan las imágenes de las páginas, cuyas copias intermedias se ponen a cero. Ese motor mantiene un
  dispositivo gráfico global que, con algunos controladores (comprobado con Intel), bloquea el cierre del proceso; por
  eso, si se ha usado el visor PDF, PassDesk termina su proceso directamente al salir, después de bloquear la bóveda,
  limpiar el portapapeles y escribir el registro.
- **Exportar** es la única forma de obtener el archivo sin cifrar, siempre por acción explícita y con aviso.
- Eliminar un documento borra su archivo cifrado; sin la clave (que desaparece de la bóveda) ya no se puede descifrar.

## Autorrelleno en navegadores (opcional)

Desactivado por defecto. Detalle y modelo de amenazas: [NAVEGADOR.md](NAVEGADOR.md) y
[NAVEGADOR-AMENAZAS.md](NAVEGADOR-AMENAZAS.md). Resumen:

- **Sin servidor de red.** No hay HTTP ni WebSocket. Con la casilla activada, PassDesk abre una **tubería local con nombre**
  accesible solo para el usuario actual (`CurrentUserOnly`, sin clientes remotos) y que solo admite al conector
  `PassDesk.NativeHost.exe` instalado junto a `PassDesk.exe` (se comprueba la ruta del proceso cliente). Desactivada la casilla,
  la tubería no existe. «PassDesk no abre puertos de red» sigue siendo cierto.
- **PassDesk decide.** La extensión y el conector son un relé: no descifran nada, no guardan nada y no tienen operación alguna
  para listar o exportar la bóveda. Reutilizan `VaultSession`; no hay segunda bóveda ni segunda autenticación, y la
  integración **no puede desbloquear**: pedir el desbloqueo solo muestra la pantalla de bloqueo de siempre.
- **Solo la extensión autorizada.** El navegador solo lanza el conector para la extensión del manifiesto
  (`allowed_origins`); el conector exige además ese origen exacto, y valida cada mensaje (4 KiB, campos conocidos, tipos
  exactos). PassDesk vuelve a validar todo.
- **Sitio correcto.** Se compara el origen entero (HTTPS, host, puerto) con la URL guardada, nunca por texto
  (`ejemplo.com.evil.test` ≠ `ejemplo.com`); sin HTTP, sin páginas internas del navegador, sin marcos de otro origen. Otro
  subdominio solo si lo activas. El origen que da el navegador no basta: PassDesk entrega con una **ficha de un solo uso**
  (60 s) ligada a la cuenta, al origen y al desbloqueo actual, y vuelve a comprobar el origen contra la bóveda al entregar.
- **Bloqueo.** Con la bóveda bloqueada no se lista ni se rellena. La lectura de la contraseña se hace bajo el mismo cerrojo
  que el bloqueo (`VaultSession.TryRead`); si se bloquea mientras se espera la confirmación, no se entrega (aunque se vuelva a
  desbloquear). Al bloquear caducan todas las fichas.
- **Confirmación del usuario.** Por defecto, cada relleno pide **Permitir** en una ventana de PassDesk (cuenta, usuario y
  página; `Cancelar` por defecto; se cierra sola al bloquear). La extensión además solo rellena tras un clic o una tecla
  reales sobre una cuenta concreta, nunca al cargar la página.
- **Menos datos.** Solo se devuelve lo pedido: en un acceso en dos pasos, primero el usuario y después la contraseña. Las
  listas llevan servicio y usuario, nunca contraseñas.
- **Límites.** Peticiones limitadas por minuto, un relleno a la vez, cuatro conexiones, tiempos máximos, y bloqueo temporal
  de los rellenos tras cinco fichas inválidas.
- **Sin rastro.** El registro solo recibe códigos de evento y contadores (nunca sitios, cuentas ni URL); las respuestas con
  contraseña se escriben en memoria fijada que se pone a cero; la extensión no usa consola ni almacenamiento del navegador, y
  una prueba automática comprueba que las contraseñas no quedan en el perfil del navegador.
- **Límites:** malware con tu usuario sigue fuera de alcance (puede simular clics o leer memoria); una página legítima
  comprometida del mismo origen puede leer lo rellenado; las cadenas de JavaScript no se pueden borrar de memoria.

## Registros, errores y red

- El registro (`%LOCALAPPDATA%\PassDesk\logs`, máx. ~1 MB) solo acepta **códigos de evento y contadores numéricos**.
  Tampoco guarda títulos ni nombres de documentos.
  De las excepciones se guarda el tipo y el HRESULT, nunca el mensaje. Una prueba automática recorre todos los flujos
  con un secreto ficticio y verifica que no aparece ni en memoria ni en el archivo de registro.
- Los errores mostrados al usuario son genéricos y no incluyen contenido de archivos.
- Ante un error inesperado, PassDesk se bloquea y muestra un aviso genérico.
- Sin telemetría, sin analítica y sin conexiones de red. (La tubería local del autorrelleno, si la activas, no es una
  conexión de red: es un canal entre procesos del mismo usuario.)

## Importación

- Durante la lectura y la vista previa, el archivo de Apple se abre en **solo lectura**. Los ZIP de Safari se leen en
  memoria, sin extraerse al disco. Hay límites de tamaño y de filas, y se detectan ZIP incoherentes. (Los datos de Apple
  en el iPhone o en iCloud nunca se tocan: el archivo exportado es una copia.)
- Los campos sensibles pasan del búfer del archivo a memoria protegida sin crear cadenas intermedias; el búfer se pone
  a cero después. Al leer se guarda la **huella** del archivo (tamaño y SHA‑256).
- **Cambios en cuentas existentes:** la opción recomendada nunca pierde un cambio hecho en PassDesk.
  - Con una exportación de Apple (sin fechas), actualiza solo las cuentas que no se han modificado en PassDesk desde su
    última importación; las editadas o creadas a mano se conservan y la versión de Apple se añade aparte.
  - Con una copia de seguridad (con fechas), solo sustituye una cuenta si la versión de la copia es más reciente.
  - La vista previa calcula exactamente cuántas se actualizarán, cuántas se añadirán y cuántas se quedan como están.
- Antes de aplicar una importación o restauración se guarda un **punto de recuperación**; si no se puede guardar, no se
  importa nada.
- Tras importar, PassDesk **sobrescribe el CSV/ZIP con datos aleatorios y lo borra** (sin Papelera) si la casilla
  correspondiente está marcada (lo está por defecto). Solo lo hace si se cumplen dos condiciones:
  - todo lo que traía quedó guardado: ninguna fila no válida y ningún conflicto descartado;
  - el archivo, ya abierto en exclusiva, tiene **la misma huella** que el que se leyó, así que nunca se destruye otra
    exportación que lo haya sustituido.
  
  Si no, se explica el motivo y el archivo se deja. Se avisa de que en SSD, con instantáneas del sistema o en carpetas
  sincronizadas (OneDrive, iCloud Drive) puede haber copias.
- **Detección de exportaciones:** PassDesk vigila Escritorio, Descargas y Documentos (sin subcarpetas). Para reconocer
  una exportación examina solo los **nombres de columna** de los CSV o los **nombres de archivo** dentro de los ZIP. De
  los 4 KB que lee solo convierte en texto la línea de cabecera: el salto de línea se busca en los bytes (UTF‑8 o
  UTF‑16), así que las primeras filas, con contraseñas, nunca pasan a una cadena. El búfer se pone a cero de inmediato.
  Nada se importa sin tu confirmación en la vista previa. Se puede desactivar en Ajustes.
- PassDesk **no abre puertos de red**: para pasar la exportación desde el iPhone se recomienda una herramienta local
  y cifrada (LocalSend), un pendrive o iCloud Drive, en lugar de añadir un servidor de red a un gestor de contraseñas.

## Guardado y recuperación

- Cada cambio se aplica en memoria y se guarda cifrado de inmediato. **Si el guardado falla** (disco lleno, antivirus,
  archivo bloqueado…), la bóveda en memoria vuelve exactamente al estado guardado en disco: no quedan cambios a medias,
  y un fallo puntual ya no impide guardar después. Antes de la auditoría, un fallo al añadir un documento hacía que
  ningún cambio posterior se pudiera guardar hasta reiniciar.
- **Puntos de recuperación:** antes de cada importación o restauración se copia `vault.pdv` a `recuperacion\`. Se
  restauran desde *Ajustes › Puntos de recuperación…* con la contraseña que tenía la bóveda en ese momento.
- **Copias antiguas tras cambiar la contraseña maestra:** el cambio no vuelve a cifrar las copias que ya existen, así
  que los puntos de recuperación y las copias `.pdbackup` anteriores se siguen abriendo con la contraseña antigua. Tras
  un cambio correcto, PassDesk lo explica y:
  - ofrece borrar **todos los puntos de recuperación anteriores al cambio** con una sola pregunta, con «No» como
    respuesta por defecto;
  - pregunta **aparte** por la copia guardada antes de pasar a Argon2id (`vault-…-antesdeargon2id.pdv`), que además está
    protegida con PBKDF2, más fácil de atacar por fuerza bruta;
  - recuerda que las copias `.pdbackup` hay que borrarlas a mano: PassDesk no sabe dónde se guardaron (pueden estar en
    un USB o en la nube).

  Sin un «Sí» no se borra nada (aparte de la limpieza automática de siempre: al guardar un punto nuevo se conservan
  solo los 10 más recientes, y eso incluye la copia previa a Argon2id). Solo se borran los `vault-*.pdv` de
  `recuperacion\` que había al terminar el cambio (nunca la bóveda ni otros archivos), y si alguno no se puede borrar
  (por ejemplo, porque está abierto en otro programa) se indica cuál para borrarlo a mano. Es un borrado normal, sin
  Papelera y sin sobrescribir: como con cualquier archivo borrado, en el disco pueden quedar restos recuperables con
  herramientas forenses, que siguen cifrados con DPAPI y con la contraseña antigua.

## Corregido tras la auditoría

Los cuatro puntos de riesgo bajo que la auditoría del 16/10/2026 dejó pendientes se corrigieron ese mismo día, cada
uno con pruebas automáticas que fallaban antes del cambio:

- La detección de exportaciones ya no convierte en texto las filas del CSV (ver «Importación»).
- Las contraseñas, notas y semillas se guardan en Base64 y no dejan copias en el búfer compartido de .NET (ver
  «Cifrado»); una prueba de control demuestra que el texto JSON sí las dejaba.
- Al cambiar la contraseña maestra, `vault.pdv.bak` deja de abrirse con la antigua (ver «Formatos de archivo»).
- El contador de intentos fallidos va cifrado con DPAPI y, si está dañado, aplica la espera máxima en lugar de volver a
  cero (ver «Autenticación y bloqueo», también su límite).
