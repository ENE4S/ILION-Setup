# ILION SETUP

Instalador de **OptiScaler ILION** para Windows x64: instala ILION, su base exacta de OptiScaler, sus variantes y los mods compatibles con tu GPU, y lo deja todo verificado y restaurable.

## ⬇ Descargar

**[Descargar ILION Setup (ILION-Setup.exe)](https://github.com/ENE4S/ILION-Setup/releases/latest/download/ILION-Setup.exe)** · siempre la última versión

Notas de cada versión: [Releases](https://github.com/ENE4S/ILION-Setup/releases) · Motor ILION: [ENE4S/Optiscaler-ILION](https://github.com/ENE4S/Optiscaler-ILION)

## Qué hace

- **Instalación nueva**
  - Elige el juego (Steam, Epic, GOG, Xbox, EA, Ubisoft, Battle.net, Rockstar o un ejecutable a mano).
  - Descarga ILION de su publicación oficial y la base congelada de OptiScaler desde sus fuentes oficiales.
  - Añade las variantes de ILION y los mods que marques.
- **Actualizar ILION:** sustituye solo la DLL del motor. «Buscar versión nueva de ILION» consulta la versión aprobada en GitHub.
- **Verificar y reparar:** compara cada archivo con su huella SHA-256 y repone lo que falta. Lo que cambiaste tú solo se sustituye si lo confirmas. También permite añadir mods, **actualizar los mods instalados** cuando se aprueba una versión nueva, deshacer cualquier cambio de Setup (quitar mods, deshacer una reparación o una actualización) y desinstalar ILION.
- **Idiomas:** español (predeterminado), portugués, ruso e inglés.
- **Seguridad**
  - Cada archivo se comprueba por SHA-256 antes de copiarlo.
  - Antes de escribir se guarda una copia de lo que se sustituye: todo es restaurable.
  - Ningún archivo del juego se sobrescribe sin respaldo.

## Al abrirlo por primera vez

El ejecutable no lleva firma digital de editor, así que Windows puede mostrar «Windows protegió su PC». Pulsa **Más información → Ejecutar de todas formas**. Comprueba antes que el SHA-256 del archivo coincide con el de la publicación.

## Qué se instala y de dónde

| Componente | Origen |
|---|---|
| ILION (dxgi.dll y OptiScaler.ini) | Publicación oficial de [ENE4S/Optiscaler-ILION](https://github.com/ENE4S/Optiscaler-ILION/releases) |
| Base de OptiScaler (commit f6f75dd) | optiscaler/OptiScaler-nightly (nightly-20261009), FidelityFX SDK v2.4.0, Streamline SDK 2.14.1 |
| FSR 4.1.1 oficial, Exact, Lossy, Lossy2 | FidelityFX SDK v2.3.0 (AMD) y zgauthier2000/fsr4-shader-performance-overrides |
| FSR 4.1.1B, XeMFG Clásica/WMMA, runtime NR de lmxxf, DLSS Enabler, asistente de ReShade | Publicación `ilion-componentes-1` de ENE4S/Optiscaler-ILION |
| Mods (XeMFG XMX, FakeMichau, ANR, MFG-Unlocker, HelixSR, DLSS NR de danielblnc, DLSS for SM86…) | GitHub de cada autor, en su versión aprobada |
| DLSS 4 Modelo K (d4r) | Solo instalación local, desde tu propia copia (lleva DLL de NVIDIA) |

**DLSS Neural Rendering:** el archivo oficial de NVIDIA no se descarga ni se incluye. Debes tenerlo previamente.

## Versiones aprobadas y manifiestos firmados

Setup no instala «la última» versión de cada mod, sino la **aprobada para ILION**: la versión exacta, el nombre del archivo y su SHA-256.

- La lista vive en la rama [`setup-manifiestos`](https://github.com/ENE4S/Optiscaler-ILION/tree/setup-manifiestos) de ENE4S/Optiscaler-ILION.
- Va firmada con la clave de ILION (ECDSA P-256), y Setup ignora cualquier lista sin una firma válida.
- Así, una versión nueva aprobada llega a los usuarios sin recompilar Setup.
- Si el autor de un mod publica algo más nuevo, Setup lo avisa, pero sigue instalando la versión aprobada.
- Cuando se aprueba una versión nueva, «Verificar y reparar» la ofrece en **Actualizaciones de mods**. Antes de tocar el juego se descarga el paquete y se comprueba que trae todo lo que espera su receta. Lo que se sustituye o retira queda guardado. Si el mod no queda completo, vuelve solo la versión anterior.

### Dos catálogos (desde Setup 0.4)

| Archivo | Lo lee | Contenido |
|---|---|---|
| `catalogo.json` (formato 1) | Setup 0.3.x y 0.4+ | Versiones aprobadas de los mods con receta integrada en Setup |
| `catalogo-2.json` (formato 2) | Setup 0.4+ | Lo mismo y, además, **recetas descritas por datos**: mods nuevos o paquetes que cambian de estructura, sin publicar otro Setup |

- Setup usa el más nuevo de los dos, así que `catalogo-2.json` debe tener siempre una fecha igual o posterior a la de `catalogo.json`.
- El catálogo de formato 1 nunca debe nombrar una receta que la 0.3.x no traiga: le impediría arrancar. `Setup.Tools validar` lo comprueba y `firmar` se niega a firmar un catálogo con errores.
- Las recetas por datos solo copian archivos dentro de la carpeta del juego: no ejecutan nada, no instalan ejecutables ni scripts y no sustituyen archivos de ILION u OptiScaler.
- Una entrada puede pedir `setupMinimo`: los Setup más antiguos la muestran como no instalable en vez de fallar.

### Aprobar una versión nueva de un mod

```
Setup.Tools comparar Setup/Assets/catalogo-2.json <id> <versión> <paquete> [carpeta]
```

Descarga la versión aprobada y la nueva y comprueba que la nueva encaja con su receta: archivos imprescindibles, rutas, DLL de 64 bits, mismo firmante y mismas funciones exportadas. También da la huella SHA-256 para el catálogo. Después: prueba en un juego de laboratorio, `validar`, `firmar` y publicar los dos catálogos.

## Compilar

Requiere el SDK de .NET 10.

```
dotnet build Setup/Setup.csproj -c Release
dotnet publish Setup/Setup.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
```

- `Setup.Core`: instalación, base congelada, catálogo, manifiestos, verificación y restauración.
- `Setup`: interfaz WPF con la estética SPUTNIK de ILION.
- `Setup.Tests`: pruebas en un laboratorio aislado; nunca escribe en juegos reales.

  ```
  Setup.Tests <carpeta-laboratorio> <paquete-ILION> [--sin-red]
  ```

- `Setup.Tools`: genera las claves, valida, compara versiones de mods y firma los manifiestos. La clave privada nunca forma parte del repositorio.

## Créditos y licencias

- OptiScaler y su comunidad (GPL-3.0).
- AMD FidelityFX SDK (MIT).
- NVIDIA Streamline.
- Intel XeSS.
- Mods y variantes: de sus autores, cada uno con su licencia:

  | Autor | Proyecto |
  |---|---|
  | realdody | XeMFG XMX |
  | FakeMichau | DLSSG-XeFG |
  | apex-compute | ANR |
  | dashdogy | MFG-Unlocker |
  | lonewolf0622 | HelixSR |
  | danielblnc | AMD-NR |
  | sdli1995 | DLSS for SM86 |
  | zgauthier2000 | FSR 4 Exact/Lossy |
  | lmxxf | runtime NR (MIT) |
  | Artur Graniszewski | DLSS Enabler (MIT) |

- Fuente Russo One (SIL Open Font License).

ILION SETUP se distribuye bajo **GPL-3.0**.
