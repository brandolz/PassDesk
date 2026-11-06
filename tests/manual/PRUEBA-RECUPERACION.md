# Prueba manual: recuperación de la bóveda (P-1)

Comprueba a mano la pantalla de recuperación con **perfiles ficticios**. El script trabaja solo en
`%TEMP%\PassDesk-prueba-recuperacion` y se niega a usar `%LOCALAPPDATA%\PassDesk` (tu bóveda real).

Reglas:

- Ejecuta todo desde el **panel de Terminal** (en la carpeta del repositorio), no desde las herramientas de Claude.
- Solo datos inventados. Contraseña maestra de prueba: **`Prueba-Recuperacion#2026`**.
- Puedes tener tu PassDesk real abierto: cada perfil es una instancia aparte.
- Anota en cada casilla OK / fallo y lo que veas. No pegues contraseñas reales.

## Preparación

```powershell
powershell -ExecutionPolicy Bypass -File tests\manual\prueba-recuperacion.ps1 -Base
```

- [ ] Se abre PassDesk con «Bienvenido a PassDesk». Crea la bóveda con la contraseña de prueba.
- [ ] Añade **Cuenta A** y después **Cuenta B** (usuario y contraseña inventados). Sal: bandeja › **Salir**.

```powershell
powershell -ExecutionPolicy Bypass -File tests\manual\prueba-recuperacion.ps1 -Escenarios
```

Para cada escenario: `-Estado <escenario>` antes, `-Abrir <escenario>`, la prueba, salir de PassDesk y `-Estado` otra vez.

## Escenarios

### 1-normal (regresión)
- [ ] «PassDesk está bloqueado», botón **Desbloquear**. No hay textos de recuperación.
- [ ] Se desbloquea con la contraseña: cuentas A y B. `-Estado`: no aparece `recuperacion\conservada-…`.

### 2-sin-principal-bak
- [ ] Título **«Recuperar la bóveda»**: «No se encuentra el archivo principal…». Botón **Recuperar bóveda** y enlace
      «Apartar las copias y crear una bóveda nueva…». No aparece «Crear bóveda» ni Windows Hello.
- [ ] Contraseña **incorrecta**: «Contraseña maestra incorrecta.». `-Estado` no cambia (sin `conservada-…`).
- [ ] Contraseña correcta: se abre con **solo la cuenta A**.
- [ ] `-Estado`: hay `vault.pdv`; `vault.pdv.bak` tiene la misma huella que antes; `recuperacion\conservada-…-bak.pdv`
      tiene esa misma huella.

### 3-sin-principal-tmp
- [ ] Pantalla de recuperación. Al recuperar: **cuentas A y B**.
- [ ] `-Estado`: `conservada-…-tmp.pdv` con la huella que tenía `vault.pdv.tmp`; ya no hay `.tmp`.

### 4-sin-principal-ambas
- [ ] Al recuperar: **cuentas A y B** (gana la copia más reciente).
- [ ] `-Estado`: `conservada-…-tmp.pdv` y `conservada-…-bak.pdv`; `vault.pdv.bak` sin cambios.

### 5-principal-danada
- [ ] Empieza como siempre («PassDesk está bloqueado»). Al desbloquear: «La bóveda principal no se puede abrir, pero queda
      una copia…», el título pasa a «Recuperar la bóveda» y aparece «Empezar con una bóveda nueva y restaurar una copia…».
- [ ] Vuelve a escribir la contraseña y pulsa **Recuperar bóveda**: **cuenta A**.
- [ ] `-Estado`: `conservada-…-principal.pdv` (la dañada) y `conservada-…-bak.pdv`.

### 6-principal-a-medias
- [ ] Igual que el 5 (aquí falla la capa de Windows). Resultado: **cuenta A**, con `conservada-…-principal.pdv`.

### 7-apartar
- [ ] Pantalla de recuperación → **Apartar las copias y crear una bóveda nueva…**. El aviso dice que **no** se borran y
      «No» es la opción por defecto. Pulsa **No**: no cambia nada (`-Estado`).
- [ ] Repite y pulsa **Sí**: aparece «Bienvenido a PassDesk» (crear bóveda).
- [ ] `-Estado`: ya no hay `.bak` en la carpeta; está en `recuperacion\conservada-…-bak.pdv` con la misma huella.
- [ ] Opcional: crea una bóveda nueva (otra contraseña inventada) y en **Ajustes › Puntos de recuperación…** elige
      `conservada-…-bak.pdv` con la contraseña de prueba: se ofrece restaurar la cuenta A.

### 8-archivar
- [ ] Al desbloquear se ofrece recuperar. Pulsa **Empezar con una bóveda nueva y restaurar una copia…** › Sí.
- [ ] **No** aparece «Crear bóveda»: sigue «Recuperar la bóveda» (no se encuentra el archivo principal) con «Apartar las
      copias…».
- [ ] `-Estado`: `vault-archivada-….pdv` (la dañada) y `vault.pdv.bak` intactos. Recupera: **cuenta A**.

## Para terminar

- [ ] En algún escenario, `logs\passdesk.log` solo tiene códigos (`VaultRecovered`, `VaultRecoveryCopiesSetAside`…), sin
      nombres de cuentas ni rutas.
- [ ] Anota los fallos. Las carpetas de `%TEMP%\PassDesk-prueba-recuperacion` se pueden borrar a mano al acabar (solo
      contienen datos ficticios).
