# ILION SETUP

Instalador de **OptiScaler ILION** para Windows x64: instala ILION, su base exacta de OptiScaler, sus variantes y los mods compatibles con tu GPU, y lo deja todo verificado y restaurable.

## ⬇ Descargar

**[Descargar ILION Setup 0.3.1 (ILION-Setup-0.3.1.exe, 63 MB)](https://github.com/ENE4S/ILION-Setup/releases/download/setup-v0.3.1/ILION-Setup-0.3.1.exe)**

Todas las versiones: [Releases](https://github.com/ENE4S/ILION-Setup/releases) · Motor ILION: [ENE4S/Optiscaler-ILION](https://github.com/ENE4S/Optiscaler-ILION)

## Qué hace

- **Instalación nueva**
  - Elige el juego (Steam, Epic, GOG, Xbox, EA, Ubisoft, Battle.net, Rockstar o un ejecutable a mano).
  - Descarga ILION de su publicación oficial y la base congelada de OptiScaler desde sus fuentes oficiales.
  - Añade las variantes de ILION y los mods que marques.
- **Actualizar ILION:** sustituye solo la DLL del motor. «Buscar versión nueva de ILION» consulta la versión aprobada en GitHub.
- **Verificar y reparar:** compara cada archivo con su huella SHA-256 y repone lo que falta. Lo que cambiaste tú solo se sustituye si lo confirmas. También permite añadir mods y restaurar o desinstalar.
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

- `Setup.Tools`: genera las claves y firma los manifiestos. La clave privada nunca forma parte del repositorio.

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
