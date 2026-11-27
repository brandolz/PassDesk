# Autorrelleno del navegador: comprobaciones pendientes en Windows

Checklist para marcar a mano. Se basa en la auditoría estática del autorrelleno, ya fusionado en `main`
(`docs/NAVEGADOR.md`, `docs/NAVEGADOR-AMENAZAS.md`).

**Marcado el 27/11/2026** a partir de lo que `docs/NAVEGADOR.md` («Estado de las pruebas») recoge como probado a mano en
Windows 11 con PassDesk real, una bóveda ficticia y Edge, Brave y Chrome (Chrome el 18/11/2026). Las casillas de la
sección 0 se comprobaron ese mismo día. Una casilla sin marcar **no consta como probada**; las notas en cursiva dicen qué
parte falta cuando solo hay cobertura parcial.

Reglas:

- Usa una **copia ficticia** de la bóveda (perfil o carpeta de datos aparte). Nunca la bóveda real ni cuentas reales.
- Ejecuta los scripts `.ps1` desde el **panel de Terminal** (las escrituras en `HKCU` desde las herramientas de Claude no
  las ven tus aplicaciones).
- Anota en «Resultado» lo observado (OK / fallo / no aplica) y la fecha. No pegues contraseñas ni URL reales.

Estado de partida: ya probado, con una bóveda de pruebas, que el relleno en el navegador funciona. **Anota aquí con qué
componentes** (Sandbox o PassDesk real), porque cambia lo que sigue pendiente:

- [ ] Se probó con `tools/PassDesk.AutofillSandbox` (no cubre `VaultSession`, DPAPI, ventana de confirmación ni `HostProcessVerifier`).
- [x] Se probó con PassDesk real y un perfil ficticio (entonces las secciones A y C ya están parcialmente cubiertas).

## 0. Preparación

- [x] Estás en `main` (el autorrelleno ya está fusionado; se comprueba con `git branch --show-current`); el árbol de trabajo está limpio.
- [x] `dotnet test PassDesk.slnx` pasa **completo**: 293 de `PassDesk.Tests` + 314 de `PassDesk.Browser.Tests`.
- [ ] `scripts\publish.ps1` y `scripts\install.ps1` terminan sin errores; `PassDesk.NativeHost.exe` está junto a `PassDesk.exe`.
      *Las pruebas a mano usaron la instalación de prueba (ZIP); no consta una ejecución de estos dos scripts.*

## A. Bóveda real (con datos ficticios)

- [x] Se rellena una cuenta desde una bóveda desbloqueada de verdad (contraseña maestra), no desde el Sandbox.
- [ ] Contraseñas con caracteres no ASCII, emojis y longitud grande se rellenan idénticas.
- [ ] Cuentas con URL guardada en distintos formatos: `ejemplo.com`, `www.ejemplo.com`, `https://ejemplo.com/login`,
      `http://ejemplo.com` (debe ofrecerse solo en la página HTTPS), con puerto, y vacía (no debe ofrecerse). Anota cuáles no coinciden.
      *Solo consta el rechazo de la página por HTTP (ver F).*
- [ ] Entradas importadas (CSV de Apple/iCloud/navegador) con sus URL tal cual vienen: ¿aparecen en la lista?
- [ ] Cuentas sin usuario o sin contraseña: comportamiento correcto en el acceso en dos pasos.
- [x] El registro (`%LOCALAPPDATA%\PassDesk\logs\passdesk.log`) solo contiene códigos y contadores: sin sitios, cuentas ni URL.

## B. Bloqueo

- [x] Bloqueado (`Ctrl+L`): el icono dice «bloqueado», no lista ni rellena; «Abrir PassDesk» muestra la pantalla de bloqueo.
- [ ] Listar, bloquear, desbloquear de nuevo y usar la ficha anterior: debe rechazarse (la época cambió).
- [x] Bloquear **con la ventana de confirmación abierta**: la ventana se cierra y no se rellena nada; tras desbloquear no se retoma.
      *Probado con bloqueo manual y con `Win+L`.*
- [ ] Bloqueo por inactividad con una solicitud pendiente.
      *Consta el bloqueo por inactividad, pero no que hubiese una solicitud pendiente.*
- [ ] Bloqueo al suspender el equipo / bloquear la sesión de Windows y volver.
      *`Win+L` probado (durante la confirmación); falta la suspensión.*
- [ ] Desbloqueo con Windows Hello (si está activado): el conector no puede desbloquear por sí solo.
- [ ] Decisión pendiente: ¿la actividad de autorrelleno debe contar como actividad para el temporizador de inactividad?

## C. Confirmación nativa

- [x] La ventana aparece **por delante del navegador** (foco y primer plano), también con PassDesk en la bandeja y con `--tray`.
- [x] Si queda oculta o sin foco: tras 30 s la solicitud se deniega y el navegador lo indica; anota si es aceptable.
- [x] Muestra cuenta, usuario y página correctos; nunca la contraseña.
- [x] `Intro`, `Espacio` y `Esc` equivalen a **Cancelar**; solo `Permitir` (clic, o `Tab` + `Espacio`; no tiene atajo) rellena,
      y no se activa hasta un segundo con la ventana activa. Se ve la línea «Para rellenar, pulsa «Permitir» con el ratón…».
      *También se comprobó que `Permitir` vuelve a quedar en gris tras `Alt+Tab`. No consta que se mirase la línea de ayuda.*
- [x] Cancelar o cerrar la ventana no rellena nada.
- [ ] (No aplica en la beta: la coincidencia por subdominio está desactivada.) Con subdominios activados, la advertencia de «otro subdominio» se ve.
- [ ] Capturas de pantalla / grabación: la ventana respeta la protección de pantalla de PassDesk (`WindowSecurity.Protect`).
- [ ] Con la confirmación desactivada en Ajustes, el relleno ocurre solo tras elegir la cuenta (comportamiento esperado y documentado).

## D. Comunicación entre procesos

- [x] El navegador lanza `PassDesk.NativeHost.exe` y el registro **no** muestra `BrowserAutofillClientRejected`.
- [ ] Con PassDesk instalado en una ruta con espacios, acentos o carpeta redirigida/enlazada: el conector sigue aceptándose
      (`HostProcessVerifier` no resuelve rutas cortas 8.3 ni enlaces; si falla, falla cerrado).
- [x] Lanzar el conector a mano desde otra ruta: PassDesk lo rechaza.
      *Probado también al revés: el conector rechaza un servidor que no es PassDesk.*
- [x] Desactivar la casilla en Ajustes cierra la tubería al instante; reactivarla la reabre.
- [ ] PassDesk cerrado o casilla desactivada: la extensión dice «PassDesk no responde…». Sin registrar el conector
      (`uninstall-browser.ps1`): dice que «no encuentra el conector».
      *PassDesk cerrado e integración desactivada, probados; falta el mensaje con el conector sin registrar.*
- [ ] No arrancar a la vez PassDesk y el Sandbox con la misma carpeta de datos (comparten nombre de tubería).
- [ ] Solicitudes en paralelo: más de 4 conexiones devuelven «ocupado» sin colgar PassDesk.
- [ ] Cerrar el navegador a mitad de la confirmación: PassDesk no se queda con la ventana ni con una conexión abierta.

## E. Navegadores e instalación

- [x] `scripts\install-browser.ps1` escribe el manifiesto y las claves de `HKCU` (comprobar con `reg query`); no toca `HKLM`.
- [x] Chrome estable: cargar la extensión descomprimida; el identificador es `lemjmmcbbcdkkkpmchohlehobcljhcff`.
- [x] Edge: ídem.
- [x] Brave: lee la clave de Chrome; registrar Brave y Chrome escribe la misma entrada.
- [x] Permiso `activeTab` al **pulsar el icono real** de la barra (el modo headless no lo cubrió).
      *En Chrome, Edge y Brave.*
- [x] Sugerencias al enfocar: conceder y retirar el permiso de un sitio.
      *En Chrome.*
- [ ] `scripts\uninstall-browser.ps1` quita solo las entradas de PassDesk; `scripts\uninstall.ps1` no borra la bóveda, los
      documentos ni las copias de seguridad.
      *`uninstall-browser.ps1` probado con el registro real; falta `uninstall.ps1`.*

## F. Sitio equivocado (con dominios ficticios que controles)

- [x] Misma página por `http://`: no ofrece la cuenta.
- [ ] Dominio parecido (`ejemplo.com.otro.test`, `miejemplo.com`): no ofrece la cuenta.
- [ ] Otro puerto: no ofrece la cuenta.
- [ ] Redirección tras elegir la cuenta: la ficha ya no vale.
- [ ] `iframe` de otro origen: no rellena.

## Cierre

- [ ] Resultados anotados y `docs/NAVEGADOR.md` («Estado de las pruebas») actualizado con lo ejecutado de verdad en Windows.
- [ ] Fallos abiertos listados como incidencias antes de repartir el siguiente paquete de prueba.
