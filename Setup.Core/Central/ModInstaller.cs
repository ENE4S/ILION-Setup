using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ilion.Client;

/// <summary>Una regla de la receta: qué se toma del paquete y dónde va, relativo a la carpeta del ejecutable.
/// «From» es una ruta exacta del paquete o una carpeta terminada en «/**» (todo su contenido, conservando subcarpetas);
/// si empieza por «*/», la primera carpeta del paquete puede llamarse de cualquier forma (paquetes con la versión en el nombre).
/// «To» admite {tag} (versión publicada). La primera regla que encaja con un archivo del paquete gana.</summary>
public sealed record RecipeFile(string From, string To);

/// <summary>Mod que CENTRAL sabe instalar con un clic: la receta (misma fórmula que RHI, código propio).
/// Descarga la última publicación oficial, comprueba su SHA-256, toma solo los archivos que hacen falta, los renombra si
/// toca y los coloca donde los espera ILION, como operación restaurable (respaldo exacto y «Restaurar»).</summary>
public sealed record InstallableMod(string Repository, string Name, string AssetPattern, string Describe, bool Dx12Only, RecipeFile[] Files)
{
    /// <summary>Archivos (destino) que deben salir del paquete; si falta alguno, la publicación no se instala.</summary>
    public string[] Required { get; init; } = [];
    /// <summary>Mods que funcionan como DLL proxy: nombres que admite, en el orden recomendado por su autor.</summary>
    public string[] ProxyNames { get; init; } = [];
    /// <summary>Ruta dentro del paquete de la compilación para cada nombre de proxy ({name} = el nombre elegido).</summary>
    public Func<string, string>? ProxySource { get; init; }
    /// <summary>Archivos de configuración que no se sobrescriben si ya existen (la configuración del usuario se conserva).</summary>
    public string[] KeepIfPresent { get; init; } = [];
    /// <summary>Mods con los que no se puede instalar: nombre y un archivo que delata que están en la carpeta del juego.</summary>
    public (string Name, string Marker)[] Conflicts { get; init; } = [];
    /// <summary>Archivo (destino) que indica que el mod ya está instalado; con {any} para una carpeta de versión.</summary>
    public string Marker { get; init; } = "";
    /// <summary>Necesita OptiScaler ILION en el juego (los que van dentro de sus carpetas). Los mods proxy independientes, no.</summary>
    public bool NeedsIlion { get; init; } = true;
    /// <summary>Publicación fija (etiqueta). Null = la última. Se fija cuando ILION espera los archivos de una versión concreta.</summary>
    public string? Tag { get; init; }
    /// <summary>Repositorio de la tarjeta del catálogo cuando los archivos salen de otro (p. ej. el Enabler de Artur sale del paquete de ILION).</summary>
    public string? Catalog { get; init; }
    /// <summary>Receta descrita por datos en el catálogo firmado (no integrada en Setup): no puede escribir encima de ILION ni de su base.</summary>
    public bool FromCatalogData { get; init; }
    /// <summary>Arquitecturas para las que sirve esta receta (ids de ILION, «rtx», marca o vacío = todas).</summary>
    public string[] Archs { get; init; } = [];
    /// <summary>Nombre corto para el botón cuando una tarjeta tiene varias recetas.</summary>
    public string? Short { get; init; }
    /// <summary>Aviso que se muestra antes de instalar.</summary>
    public string? Note { get; init; }
    /// <summary>Archivos oficiales (NVIDIA, Intel, AMD) que el mod necesita y su publicación no trae (regla de las recetas).</summary>
    public ModDependency[] Dependencies { get; init; } = [];
    /// <summary>Parte del mod se construye en el PC con el setup de su autor (HelixSR). Null = se instala tal cual.</summary>
    public AuthorBuild? Build { get; init; }
    /// <summary>Tamaño mínimo del archivo marcador cuando está construido (el del paquete sin preparar es mucho menor).</summary>
    public long BuiltMinSize { get; init; }
    /// <summary>ILION Setup: SHA-256 del paquete aprobado para ILION (manifiesto). Si GitHub publica otro, no se instala.</summary>
    public string? ApprovedSha256 { get; init; }
    /// <summary>ILION Setup: nombre exacto del archivo aprobado en la publicación (descarga directa, sin la API).</summary>
    public string? ApprovedAsset { get; init; }
    /// <summary>Versión mínima de ILION que sabe cargar el mod (null = cualquiera).</summary>
    public string? MinIlion { get; init; }
    /// <summary>Grupo de mods que hacen lo mismo y que ILION usa de uno en uno (se informa al instalar varios juntos; no bloquea).</summary>
    public string? Slot { get; init; }
    /// <summary>Clave estable de la receta (para selecciones guardadas y registros).</summary>
    public string Key => (Catalog ?? Repository) + "|" + Name;

    /// <summary>Destino de un archivo del paquete según la receta (null = no se instala: documentación, registros…).</summary>
    public string? Map(string entry, string tag)
    {
        var e = entry.Replace('\\', '/');
        foreach (var rule in Files)
        {
            var from = rule.From.Replace('\\', '/');
            var to = rule.To.Replace("{tag}", tag);
            if (from.StartsWith("*/")) // primera carpeta con cualquier nombre
            {
                var cut = e.IndexOf('/');
                if (cut < 0) continue;
                var inner = this with { Files = [rule with { From = from[2..] }] };
                if (inner.Map(e[(cut + 1)..], tag) is { } m) return m;
                continue;
            }
            if (from.EndsWith("/**") || from == "**")
            {
                var prefix = from == "**" ? "" : from[..^2];
                if (!e.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = e[prefix.Length..];
                if (Regex.IsMatch(rest, @"(^|/)(logs?/|[^/]+\.(md|html|txt|pdb)$)", RegexOptions.IgnoreCase)) return null; // documentación y registros fuera
                return Path.Combine(to.TrimEnd('\\', '/', '*'), rest.Replace('/', Path.DirectorySeparatorChar));
            }
            if (e.Equals(from, StringComparison.OrdinalIgnoreCase)) return to;
        }
        return null;
    }
}

public sealed record ModDownload(string Tag, string Folder, string[] Files);

/// <summary>Un archivo que se va a escribir: destino relativo a la carpeta del ejecutable y su origen en la caché.</summary>
public sealed record ModPlanItem(string Relative, string Source);

/// <summary>Instalación verificada y restaurable de mods del catálogo: publicación oficial de GitHub, huella SHA-256 publicada por GitHub,
/// extracción en caché y escritura con el motor de operaciones (respaldo exacto, «Restaurar»).</summary>
public sealed class ModInstaller(HttpClient http, string dataDirectory, OperationEngine operations)
{
    // ---- Archivos oficiales que necesitan los mods (regla de las recetas) ----
    /// <summary>XeFG de Intel para XeMFG XMX, en su propia carpeta. Prueba de laboratorio 2026-11-18 (RX 7900 XTX, XMX build-10, sin juego):
    /// su contexto XeFG arranca con la 1.3.1.78 oficial (compartida o propia) y con la 1.3.4.95. El fallo antiguo en AMD («Failed to create
    /// heap») venía de ILION 1.4.0, que cargaba a la vez una variante XeMFG que parcheaba la misma 1.3.1 compartida; ILION 1.4.1 ya no lo hace
    /// y, si la carpeta de XMX trae libxess_fg, la usa para XMX. Con copia propia nadie más la toca. Orden: una 1.3.4 firmada en los juegos del
    /// PC (la probada en juego), la 1.3.1 de OptiScaler del propio juego, o la 1.3.1 del SDK oficial de Intel (v3.0.2, fijada). Nunca 1.4 o más.</summary>
    public static readonly ModDependency XmxXeFg = new(@"OptiScaler\custom\XeMFG XMX\dlssg\libxess_fg.dll", "XeFG de Intel para XeMFG XMX", "Intel",
        "XeMFG XMX usa su propia libxess_fg.dll de Intel en su carpeta (1.3.1 o 1.3.4). ILION SETUP coloca una copia firmada: la 1.3.4 si está en tus juegos o, si no, la 1.3.1 oficial de Intel.")
    {
        MinVersion = "1.3.1", BelowVersion = "1.4", Signer = "Intel Corporation", SearchGames = true, PreferGames = true,
        CopyFrom = [@"OptiScaler\libxess_fg.dll"], Official = ComponentRecipe.For("libxess_fg.dll"), OfficialTag = "v3.0.2",
        Link = "https://github.com/intel/xess/releases",
    };

    /// <summary>Nombre anterior (las recetas por datos lo piden como «xefg-1.3.4-xmx»).</summary>
    public static ModDependency XeFg134 => XmxXeFg;

    /// <summary>Runtime oficial de Intel en la carpeta de OptiScaler (lo usan los plugins XeMFG). Sale del SDK de XeSS de Intel en GitHub.</summary>
    public static ModDependency IntelRuntime(string file, string what, string? min) => new($@"OptiScaler\{file}", what, "Intel",
        $"El mod usa la {file} de Intel de la carpeta de OptiScaler y no la incluye. ILION SETUP la descarga del SDK oficial de XeSS (github.com/intel/xess).")
    {
        MinVersion = min, Signer = "Intel Corporation", Official = ComponentRecipe.For(file), Link = "https://github.com/intel/xess/releases",
    };

    /// <summary>DLL de Neural Rendering de NVIDIA. Decisión del usuario (2026-11-12): CENTRAL no la consigue; solo avisa.
    /// En ILION, el puente AMD ejecuta el runtime elegido (dlssnr_amd_pass1..3 o LmxxfNrRuntime) y no la carga (AmdBridge.cpp);
    /// la pide solo la ruta de NVIDIA.</summary>
    public static readonly ModDependency DlssNr = new("nvngx_dlssnr.dll", "DLSS Neural Rendering de NVIDIA", "NVIDIA",
        "Solo hace falta si el registro de ILION la pide: entonces cópiala a mano junto al ejecutable. NVIDIA no la publica suelta en su GitHub; viene con los juegos que traen DLSS Neural Rendering. ILION SETUP no la descarga.")
    {
        Signer = "NVIDIA Corporation", WarnOnly = true, Link = "https://github.com/NVIDIA/DLSS",
    };

    /// <summary>Recetas. Solo entran mods cuyo paquete se ha estudiado con las carpetas de ILION.</summary>
    public static readonly InstallableMod[] Known =
    [
        new("realdody/dlssg-to-xefg-xmx", "XeMFG XMX", @"^dlssg-to-xefg-.*\.zip$", @"OptiScaler\custom\XeMFG XMX\dlssg\ (DLL + programas DXIL)", true,
            [new("**", @"OptiScaler\custom")]) // el ZIP trae «XeMFG XMX\dlssg\…»
        {
            Required = [@"OptiScaler\custom\XeMFG XMX\dlssg\dlssg_to_xefg_intel_is_better.dll"],
            Marker = @"OptiScaler\custom\XeMFG XMX\dlssg\dlssg_to_xefg_intel_is_better.dll",
            Note = "En ILION: Frame Generation › Implementación DLSS FG = XeMFG XMX (ruta NVNGX). Usa su propia libxess_fg de Intel (1.3.1 o 1.3.4) en su carpeta; ILION SETUP la coloca.",
            Dependencies = [XmxXeFg], Slot = "Implementación DLSS FG",
        },
        // DLSSG-XeFG de FakeMichau: ILION 1.4.1.2 lo carga de OptiScaler\custom\DLSSG-XeFG\dlssg (Nvngx_FakeMichau.cpp, ProviderPath).
        // El ZIP oficial trae la DLL en la raíz. Usa la libxess_fg de su carpeta o la de OptiScaler (README: XeFG 1.3.1 o 1.3.4)
        new("FakeMichau/dlssg-to-xefg", "DLSSG-XeFG (FakeMichau)", @"^dlssg-to-xefg-\d+\.zip$", @"OptiScaler\custom\DLSSG-XeFG\dlssg\ (DLL)", false,
            [new("dlssg_to_xefg_intel_is_better.dll", @"OptiScaler\custom\DLSSG-XeFG\dlssg\dlssg_to_xefg_intel_is_better.dll")])
        {
            Required = [@"OptiScaler\custom\DLSSG-XeFG\dlssg\dlssg_to_xefg_intel_is_better.dll"],
            Marker = @"OptiScaler\custom\DLSSG-XeFG\dlssg\dlssg_to_xefg_intel_is_better.dll",
            MinIlion = "1.4.1.2", Slot = "Implementación DLSS FG",
            Dependencies = [new(@"OptiScaler\libxess_fg.dll", "XeFG de Intel", "Intel",
                "DLSSG-XeFG usa la libxess_fg.dll de Intel (1.3.1 o 1.3.4) de su carpeta o de la de OptiScaler, y no la incluye. ILION SETUP la descarga del SDK oficial de XeSS (github.com/intel/xess).")
            {
                MinVersion = "1.3.1", Signer = "Intel Corporation", Official = ComponentRecipe.For("libxess_fg.dll"), Link = "https://github.com/intel/xess/releases",
                AlsoAccept = [@"OptiScaler\custom\DLSSG-XeFG\dlssg\libxess_fg.dll"],
            }],
            Note = "En ILION 1.4.1.2 o posterior: Frame Generation › Implementación DLSS FG = DLSSG-XeFG (ruta NVNGX). Es el original de FakeMichau, distinto de XeMFG XMX (realdody) y de MFG-Unlocker (dashdogy). DX12 y Vulkan.",
        },
        // Apex Neural Rendering FG (apex-compute/ANR): ILION 1.4.1.1 lo carga de OptiScaler\custom\Apex Neural Rendering FG\dlssg\nvngx_dlssg.dll
        // (Nvngx_ApexFg.cpp). El addon oficial trae esa carpeta completa (programas, núcleos, adaptadores y nr_fg.ini)
        new("apex-compute/ANR", "Apex Neural Rendering FG (ANR)", @"^anr-fg-V\.B\d+-addon\.zip$", @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\ (addon completo)", true,
            [new("Apex Neural Rendering FG/**", @"OptiScaler\custom\Apex Neural Rendering FG")])
        {
            Required = [@"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nvngx_dlssg.dll", @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\path_table.json",
                        @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\size_table.json", @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\kernels\pack.nrx"],
            Marker = @"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nvngx_dlssg.dll",
            KeepIfPresent = [@"OptiScaler\custom\Apex Neural Rendering FG\dlssg\nr_fg.ini"],
            MinIlion = "1.4.1.1", Slot = "Implementación DLSS FG",
            Note = "En ILION 1.4.1.1 o posterior: Frame Generation › Implementación DLSS FG = Apex Neural Rendering FG (ruta NVNGX), hasta ×6. Es generación de fotogramas neuronal; no tiene que ver con el Neural Rendering de escalado (runtimes de danielblnc y lmxxf). Beta de Apex Compute. Se conserva tu nr_fg.ini.",
        },
        new("dashdogy/MFG-Unlocker", "MFG-Unlocker", @"^MFGUnlocker-.*\.zip$", @"OptiScaler\plugins\XeMFG\dashdogy-<versión>\MFGUnlocker.asi", false,
            [new("MFGUnlocker.dll", @"OptiScaler\plugins\XeMFG\dashdogy-{tag}\MFGUnlocker.asi"), // el cargador de ILION busca un .asi en la carpeta de la variante
             new("**", @"OptiScaler\plugins\XeMFG\dashdogy-{tag}")])
        {
            Required = [@"OptiScaler\plugins\XeMFG\dashdogy-{tag}\MFGUnlocker.asi"],
            Marker = @"OptiScaler\plugins\XeMFG\dashdogy-{any}\MFGUnlocker.asi",
            Conflicts = [("MFG Unlock (mavismmg)", "MFGAdaUnlock.ini")],
            Note = "En ILION: Frame Generation › Modelo de Intel FG = la variante dashdogy. Necesita «Cargar plugins ASI».",
            Dependencies = [IntelRuntime("libxess_fg.dll", "XeFG de Intel", "1.3.1"), IntelRuntime("libxell.dll", "XeLL de Intel", null)],
        },
        new("sdli1995/dlssg_for_sm86", "DLSS for SM86", @"^dlssg-release-.*-x64\.zip$", "DLL proxy + dlssg_sm86.ini + carpeta dlssg_sm86", true,
            [new("dlssg_sm86.ini", "dlssg_sm86.ini"), new("dlssg_sm86/**", "dlssg_sm86")])
        {
            Required = [@"dlssg_sm86\runtime\nvngx_dlssg.dll", @"dlssg_sm86\backend\sm86_backend.dll", @"dlssg_sm86\kernels\fg_kernels.dll", "dlssg_sm86.ini"],
            // Orden del README del autor; version.dll va en la raíz del paquete y el resto en alternatives\ (cada nombre es su propia compilación: nunca se renombra)
            ProxyNames = ["version.dll", "winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll"],
            ProxySource = name => name.Equals("version.dll", StringComparison.OrdinalIgnoreCase) ? "version.dll" : "alternatives/" + name,
            KeepIfPresent = ["dlssg_sm86.ini"],
            Conflicts = [("MFG Unlock (mavismmg)", "MFGAdaUnlock.ini")],
            Marker = @"dlssg_sm86\runtime\nvngx_dlssg.dll",
            NeedsIlion = false,
            Note = "Solo NVIDIA RTX 20/30, DirectX 12 y driver R580 o superior. Con ILION: el FG de ILION apagado (el mod genera los fotogramas); ILION queda en su propio proxy.",
        },
        // Multi Frame Generation Enabler (Artur): ILION usa solo dlss-enabler-headless.dll en la carpeta OptiScaler. Artur publica
        // instaladores .exe, así que la DLL sale del paquete de ILION publicado en GitHub (ENE4S), que ya la incluye
        new("ENE4S/Optiscaler-ILION", "Multi Frame Generation Enabler (Artur)", @"\.(rar|7z|zip)$", @"OptiScaler\dlss-enabler-headless.dll", true,
            [new("*/OptiScaler/dlss-enabler-headless.dll", @"OptiScaler\dlss-enabler-headless.dll"), new("OptiScaler/dlss-enabler-headless.dll", @"OptiScaler\dlss-enabler-headless.dll")])
        {
            Catalog = "artur-graniszewski/DLSS-Enabler",
            Required = [@"OptiScaler\dlss-enabler-headless.dll"],
            Marker = @"OptiScaler\dlss-enabler-headless.dll",
            Slot = "Implementación DLSS FG",
            Note = "En ILION: Frame Generation › Implementación DLSS FG = DLSS Enabler de Artur (o Combinado). Se toma del paquete de ILION publicado en GitHub.",
        },
        // Nukem (dlssg-to-fsr3): se publica en Nexus, pero el paquete oficial de OptiScaler trae su DLL
        // ILION la busca en su carpeta OptiScaler (Nvngx_Nukems.cpp: MainDllPath), como la trae su paquete
        new("optiscaler/OptiScaler", "Nukem · dlssg-to-fsr3", @"^Optiscaler_[0-9A-Za-z._-]+\.7z$", @"OptiScaler\dlssg_to_fsr3_amd_is_better.dll", false,
            [new("dlssg_to_fsr3_amd_is_better.dll", @"OptiScaler\dlssg_to_fsr3_amd_is_better.dll"), new("*/dlssg_to_fsr3_amd_is_better.dll", @"OptiScaler\dlssg_to_fsr3_amd_is_better.dll")])
        {
            Catalog = "Nukem9/dlssg-to-fsr3",
            Required = [@"OptiScaler\dlssg_to_fsr3_amd_is_better.dll"],
            Marker = @"OptiScaler\dlssg_to_fsr3_amd_is_better.dll",
            Slot = "Implementación DLSS FG",
            Note = "En ILION: Frame Generation › Implementación DLSS FG = Nukem. Se toma del paquete oficial de OptiScaler.",
        },
        // HelixSR: ILION los busca en ILION\Escaladores\HelixSR\<carpeta> (helixsr_upscaler.dll o el HLX FSR 4 de la 3.x)
        new("lonewolf0622/HelixSR", "HelixSR 1.9 (GPU antiguas)", @"^HelixSR-1\.9\.\d+\.zip$", @"ILION\Escaladores\HelixSR\helix1.9", false,
            [new("*/amd_fidelityfx_dx12.dll", @"ILION\Escaladores\HelixSR\helix1.9\helixsr_upscaler.dll")]) // ILION pide el nombre helixsr_upscaler.dll
        {
            Tag = "v1.9.3", Short = "HelixSR 1.9",
            Required = [@"ILION\Escaladores\HelixSR\helix1.9\helixsr_upscaler.dll"],
            Marker = @"ILION\Escaladores\HelixSR\helix1.9\helixsr_upscaler.dll",
            Build = new(".", NoFg: true, NeedsFg: false), BuiltMinSize = 10_000_000,
            Note = "En ILION: Escalado › Variante = HelixSR/helix1.9. Pensado para GPU sin FSR 4 (RDNA 1/2, GTX, Arc). Sustituye a FSR 3.1 en DirectX 12. " +
                   "La red de NVIDIA se construye en tu PC con el setup oficial de HelixSR (descarga la DLL de DLSS 310.7.0 del GitHub de NVIDIA; unos minutos la primera vez). No toca ningún juego.",
        },
        new("lonewolf0622/HelixSR", "HLX FSR 4 (HelixSR 3.3)", @"^HelixSR-3\.3\.0\.zip$", @"ILION\Escaladores\HelixSR\HLX-FSR4", false,
            [new("*/HLX-FSR4/amd_fidelityfx_upscaler_dx12.dll", @"ILION\Escaladores\HelixSR\HLX-FSR4\amd_fidelityfx_upscaler_dx12.dll")])
        {
            Tag = "v3.3.0", Short = "HLX FSR 4",
            Required = [@"ILION\Escaladores\HelixSR\HLX-FSR4\amd_fidelityfx_upscaler_dx12.dll"],
            Marker = @"ILION\Escaladores\HelixSR\HLX-FSR4\amd_fidelityfx_upscaler_dx12.dll",
            Note = "En ILION: Escalado › Variante = HelixSR/HLX-FSR4. FSR 4 Lite de AMD sobre el motor de HelixSR, para GPU sin FSR 4. La descarga es grande (266 MB); solo se instala un archivo.",
        },
        new("lonewolf0622/HelixSR", "FG Lite (HelixSR 2.0.0)", @"^HelixSR-2\.0\.0\.zip$", "HelixSR-fsrfg", true,
            [new("*/HelixSR-fsrfg/**", "HelixSR-fsrfg")])
        {
            Tag = "v2.0.0", Short = "FG Lite", Archs = ["amd"],
            Required = [@"HelixSR-fsrfg\amd_fidelityfx_dx12.dll", @"HelixSR-fsrfg\helixsr-fg\nvexec64.dll", @"HelixSR-fsrfg\helixsr-fg\_nvngx.dll",
                        @"HelixSR-fsrfg\helixsr-fg\nvngx_dlssg.dll", @"HelixSR-fsrfg\helixsr-fg\hxfg_data.bin",
                        @"HelixSR-fsrfg\helixsr-fg\310.9.16\nvngx_dlssg.dll", @"HelixSR-fsrfg\helixsr-fg\310.9.16\hxfg_data.bin"],
            Marker = @"HelixSR-fsrfg\amd_fidelityfx_dx12.dll",
            Build = new("HelixSR-fsrfg", NoFg: false, NeedsFg: true), BuiltMinSize = 15_000_000, Slot = "Implementación DLSS FG",
            Note = "En ILION: Frame Generation › Implementación DLSS FG = NVIDIA FG Lite (HelixSR). Solo AMD. Es la 2.0.0: la 3.x ya no trae Frame Generation. " +
                   "La red y el Frame Generation se construyen en tu PC con el setup oficial de HelixSR a partir de las DLL de NVIDIA (DLSS 310.7.0, DLSS FG 310.3 y 310.9.16 y el núcleo NGX), descargadas de NVIDIA y comprobadas por SHA-256. Tarda varios minutos la primera vez. No toca ningún juego.",
        },
        // Neural Rendering: los dos runtimes que ILION ya integra, de publicaciones fijas de AMDNR, junto a la DLL de ILION
        new("3zwr1/AMD-NR---OptiScaler", "Runtime NR de danielblnc", @"^v0\.6\.0-Runtime\.zip$", "dlssnr_amd_pass1..3.dll + dlssnr_on_amd_weights.bin", true,
            [new("dlssnr_amd_pass1.dll", "dlssnr_amd_pass1.dll"), new("dlssnr_amd_pass2.dll", "dlssnr_amd_pass2.dll"), new("dlssnr_amd_pass3.dll", "dlssnr_amd_pass3.dll"),
             new("dlssnr_on_amd_weights.bin", "dlssnr_on_amd_weights.bin")])
        {
            Tag = "Alpha0.3.5.6", Catalog = "danielblnc/DLSS-NR-on-AMD",
            Required = ["dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll", "dlssnr_on_amd_weights.bin"],
            Marker = "dlssnr_amd_pass1.dll",
            Note = "Runtime 0.6.0 de danielblnc (el que usa ILION), del paquete de AMDNR. Va junto a la DLL de ILION. No instales su setup.exe ni su dxgi/version/winhttp.",
            Dependencies = [DlssNr], Slot = "Runtime de Neural Rendering",
        },
        new("3zwr1/AMD-NR---OptiScaler", "Runtime NR de lmxxf", @"^AMDNR-lmxxf-0\.3\.5\.8R2\.zip$", "LmxxfNrRuntime.dll + LmxxfNrRuntime.pak", true,
            [new("LmxxfNrRuntime.dll", "LmxxfNrRuntime.dll"), new("LmxxfNrRuntime.pak", "LmxxfNrRuntime.pak")])
        {
            Tag = "Alpha0.3.5.8R2", Catalog = "lmxxf/dlss5-on-amd-9070xt-porting",
            Required = ["LmxxfNrRuntime.dll", "LmxxfNrRuntime.pak"],
            Marker = "LmxxfNrRuntime.dll",
            Note = "Runtime de lmxxf del paquete de AMDNR. Va junto a la DLL de ILION. RX 9000 completo; en RX 7000 ILION usa su modo Fast.",
            Dependencies = [DlssNr], Slot = "Runtime de Neural Rendering",
        },
    ];

    /// <summary>Mods del catálogo que de momento solo se instalan a mano, y por qué (se muestra en la tarjeta).</summary>
    public static readonly Dictionary<string, string> ManualReason = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SilyNoMeta/rtx-encore"] = "Instalación compleja (proxy de Bink y runtimes de Intel): pendiente de receta.",
        ["mavismmg/MFGAdaUnlock-RenoDx"] = "Es un addon de ReShade: necesita ReShade instalado.",
    };

    /// <summary>Proyectos cuyo trabajo ya va dentro de ILION o de su paquete (se ven en el catálogo, pero no hay nada que instalar aparte).</summary>
    public static readonly Dictionary<string, string> Integrated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["3zwr1/AMD-NR---OptiScaler"] = "El puente AMD de Neural Rendering va dentro de ILION. Desde aquí solo se instalan los runtimes que usa (danielblnc y lmxxf).",
        ["countervolts/d4r"] = "d4r (DLSS en AMD RDNA 3 y RDNA 4) va en el paquete de ILION, en d4r\\ e ILION\\Escaladores\\DLSS4\\.",
        ["optiscaler/fakenvapi"] = "fakenvapi (Anti-Lag 2 y XeLL en GPU que no son NVIDIA) viene con OptiScaler y con el paquete de ILION.",
        ["dashdogy/OptiScaler"] = "El puente Vulkan → D3D12 de XeFG de este fork está integrado en ILION (XeMFG en Vulkan).",
    };

    /// <summary>Recetas de una tarjeta del catálogo (por su repositorio o el de origen).</summary>
    public static InstallableMod[] All(string repository) =>
        [.. Known.Where(m => (m.Catalog ?? m.Repository).Equals(repository, StringComparison.OrdinalIgnoreCase))];
    public static InstallableMod? For(string repository) => All(repository).FirstOrDefault();

    /// <summary>¿Ya está instalado en esta carpeta? (cualquier versión)</summary>
    public static bool Installed(InstallableMod mod, string exeDir)
    {
        if (mod.Marker.Length == 0) return false;
        var marker = mod.Marker.Split("{any}");
        if (marker.Length == 1) return File.Exists(Path.Combine(exeDir, mod.Marker));
        var parent = Path.GetDirectoryName(Path.Combine(exeDir, marker[0] + "x"))!;
        var leaf = Path.GetFileName(marker[0] + "x")[..^1];
        return Directory.Exists(parent) && Directory.EnumerateDirectories(parent, leaf + "*").Any(d => File.Exists(d + marker[1]));
    }

    /// <summary>Mods incompatibles presentes en la carpeta del juego.</summary>
    public static string[] ConflictsIn(InstallableMod mod, string exeDir) =>
        [.. mod.Conflicts.Where(c => File.Exists(Path.Combine(exeDir, c.Marker))).Select(c => c.Name)];

    /// <summary>Nombres de proxy y si están libres: un nombre ocupado por otro archivo (OptiScaler, ReShade, otro mod) no se ofrece.</summary>
    public static (string Name, bool Free)[] ProxyChoices(InstallableMod mod, string exeDir) =>
        [.. mod.ProxyNames.Select(n => (n, !File.Exists(Path.Combine(exeDir, n))))];

    /// <summary>Descarga la última publicación, comprueba su SHA-256 con el que publica GitHub y la extrae en la caché.</summary>
    public async Task<ModDownload> DownloadAsync(InstallableMod mod, CancellationToken token, IProgress<(long Done, long? Total)>? bytes = null)
    {
        // ILION Setup: el paquete aprobado ya está en caché (misma huella) → sin consultar GitHub (sin conexión y sin gastar la API)
        if (mod.ApprovedSha256 is { } approvedCache && mod.Tag is { } pinned)
        {
            var tagFolder = Path.Combine(dataDirectory, "Packages", "Mods", mod.Repository.Replace('/', '_'), pinned);
            if (Directory.Exists(tagFolder))
                foreach (var cachedPackage in Directory.EnumerateFiles(tagFolder, "paquete.*", SearchOption.AllDirectories))
                    if (!cachedPackage.EndsWith(".parcial") && Sha(cachedPackage).Equals(approvedCache, StringComparison.OrdinalIgnoreCase))
                        return Extract(cachedPackage, Path.GetDirectoryName(cachedPackage)!, pinned);
        }
        string tag, url, assetName; string digest; long? size;
        if (mod.ApprovedSha256 is { } fixedSha && mod.Tag is { } fixedTag && mod.ApprovedAsset is { } fixedAsset)
        {
            // ILION Setup: versión aprobada con su archivo exacto → enlace directo de la publicación (sin la API de GitHub ni su límite por hora)
            if (!Regex.IsMatch(fixedTag, @"^[\w.\-]{1,40}$") || !Regex.IsMatch(fixedAsset, @"^[\w.\-]{1,120}$")) throw new InvalidDataException("Versión aprobada no válida en el catálogo.");
            tag = fixedTag; assetName = fixedAsset; digest = "sha256:" + fixedSha.ToLowerInvariant(); size = null;
            url = $"https://github.com/{mod.Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";
        }
        else
        {
        var api = mod.Tag is null ? $"https://api.github.com/repos/{mod.Repository}/releases/latest" : $"https://api.github.com/repos/{mod.Repository}/releases/tags/{mod.Tag}";
        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.UserAgent.ParseAdd("ILION-Setup/" + Ilion.Setup.SetupEngine.SetupVersion);
        using var response = await http.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await response.Content.ReadAsStringAsync(token)) as JsonObject ?? throw new InvalidDataException("Respuesta no válida de GitHub.");
        tag = release["tag_name"]?.GetValue<string>() ?? throw new InvalidDataException("La publicación no tiene etiqueta.");
        if (!Regex.IsMatch(tag, @"^[\w.\-]{1,40}$")) throw new InvalidDataException("Etiqueta de publicación no válida.");
        var asset = (release["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => Regex.IsMatch(a["name"]?.GetValue<string>() ?? "", mod.AssetPattern, RegexOptions.IgnoreCase))
            ?? throw new InvalidDataException("La publicación no contiene el paquete esperado.");
        digest = asset["digest"]?.GetValue<string>()!;
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHub no publica la huella SHA-256 de este paquete: no se instala.");
        if (mod.ApprovedSha256 is { } approved && !digest[7..].Equals(approved, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"El paquete publicado ({tag}) no es la versión aprobada para ILION: no se instala.");
        url = asset["browser_download_url"]!.GetValue<string>();
        if (!url.StartsWith($"https://github.com/{mod.Repository}/releases/download/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Enlace de descarga inesperado.");
        assetName = asset["name"]!.GetValue<string>();
        size = asset["size"]?.GetValue<long>();
        }
        var extension = Path.GetExtension(assetName).ToLowerInvariant();
        if (extension is not (".zip" or ".7z" or ".rar")) throw new InvalidDataException("Formato de paquete no admitido.");

        // Caché por publicación y paquete: varias recetas pueden salir de la misma publicación
        var folder = Path.Combine(dataDirectory, "Packages", "Mods", mod.Repository.Replace('/', '_'), tag, Path.GetFileNameWithoutExtension(assetName));
        var zip = Path.Combine(folder, "paquete" + extension);
        Directory.CreateDirectory(folder);
        if (!File.Exists(zip) || !Sha(zip).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
        {
            var partial = zip + ".parcial";
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using var response1 = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    response1.EnsureSuccessStatusCode();
                    var total = response1.Content.Headers.ContentLength ?? size;
                    await using var net = await response1.Content.ReadAsStreamAsync(token);
                    await using var file = File.Create(partial);
                    var buffer = new byte[1 << 16]; long done = 0; int read; var last = Environment.TickCount64;
                    while ((read = await net.ReadAsync(buffer, token)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), token);
                        done += read;
                        if (Environment.TickCount64 - last > 150) { bytes?.Report((done, total)); last = Environment.TickCount64; }
                    }
                    bytes?.Report((done, total));
                    break;
                }
                catch (Exception error) when (attempt < 3 && !token.IsCancellationRequested && error is HttpRequestException or IOException)
                { await Task.Delay(1500 * attempt, token); } // reintento: red inestable
            }
            if (!Sha(partial).Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) { File.Delete(partial); throw new InvalidDataException(mod.ApprovedAsset is not null ? "El paquete descargado no es la versión aprobada para ILION (su SHA-256 no coincide): descartado, no se instala." : "La huella SHA-256 no coincide con la publicada: descarga descartada."); }
            File.Move(partial, zip, overwrite: true);
        }
        return Extract(zip, folder, tag);
    }

    /// <summary>Recetas con compilación (HelixSR): prepara una copia del paquete en la caché y ejecuta allí el paso de compilación del setup
    /// del autor (nunca su paso de juegos). Se hace una vez por versión; las siguientes instalaciones reutilizan lo construido.</summary>
    public async Task<ModDownload> PrepareAsync(InstallableMod mod, ModDownload download, IProgress<string>? progress, CancellationToken token)
    {
        if (mod.Build is not { } build) return download;
        var built = Path.Combine(Path.GetDirectoryName(download.Folder)!, "compilado");
        var ready = Path.Combine(built, ".listo");
        if (!File.Exists(ready))
        {
            if (Directory.Exists(built)) Directory.Delete(built, true);
            foreach (var f in download.Files)
            {
                var to = Path.Combine(built, f);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(Path.Combine(download.Folder, f), to);
            }
        }
        // Carpeta del setup: la raíz del paquete o su primera carpeta (los ZIP de HelixSR traen «HelixSR-x.y.z\»)
        var root = File.Exists(Path.Combine(built, "helixsr-setup.ps1")) ? built
            : Directory.EnumerateDirectories(built).FirstOrDefault(d => File.Exists(Path.Combine(d, "helixsr-setup.ps1")))
              ?? throw new InvalidDataException("El paquete no trae el setup de su autor.");
        var log = await AuthorBuilder.BuildAsync(http, root, build, progress, token);
        File.WriteAllText(ready, log);
        var files = Directory.EnumerateFiles(built, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(built, f)).Where(f => f != ".listo").ToArray();
        return new(download.Tag, built, files);
    }

    /// <summary>Extrae el paquete en la caché (rutas seguras) y lo devuelve con sus archivos tal como vienen. ZIP, 7z o RAR.</summary>
    public static ModDownload Extract(string zip, string folder, string tag)
    {
        var extracted = Path.Combine(folder, "contenido");
        if (Directory.Exists(extracted)) Directory.Delete(extracted, true);
        var root = Path.GetFullPath(extracted) + Path.DirectorySeparatorChar;
        string Safe(string key)
        {
            var target = Path.GetFullPath(Path.Combine(extracted, key));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Ruta no válida dentro del paquete.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            return target;
        }
        if (zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            using (var archive = ZipFile.OpenRead(zip))
                foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0))
                    entry.ExtractToFile(Safe(entry.FullName), overwrite: true);
        else
            using (var archive = ArchiveFactory.OpenArchive(zip))
                foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    entry.WriteToFile(Safe(entry.Key!), new ExtractionOptions { Overwrite = true });
        var files = Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(extracted, f)).ToArray();
        if (files.Length == 0) throw new InvalidDataException("El paquete está vacío.");
        return new(tag, extracted, files);
    }

    /// <summary>Archivos de ILION y de su base congelada (los fija ILION Setup al cargar el manifiesto): una receta por datos no los toca.</summary>
    public static HashSet<string> ProtectedFiles { get; } = new(StringComparer.OrdinalIgnoreCase) { "OptiScaler.ini" };

    /// <summary>Lo que se va a escribir según la receta. Valida que estén los archivos obligatorios y que el proxy elegido sea válido y esté libre.</summary>
    public static ModPlanItem[] Plan(InstallableMod mod, ModDownload download, string exeDir, string? proxy, bool updating = false)
    {
        var items = new List<ModPlanItem>();
        foreach (var f in download.Files)
            if (mod.Map(f, download.Tag) is { } to) items.Add(new(to, Path.Combine(download.Folder, f)));
        if (mod.ProxyNames.Length > 0)
        {
            if (proxy is null || !mod.ProxyNames.Contains(proxy, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Elige un nombre de proxy de la lista.");
            if (!updating && File.Exists(Path.Combine(exeDir, proxy))) throw new InvalidDataException($"{proxy} ya existe en la carpeta del juego: elige otro nombre.");
            var source = Path.Combine(download.Folder, mod.ProxySource!(proxy).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source)) throw new InvalidDataException($"El paquete no trae la compilación para {proxy}.");
            items.Add(new(proxy, source));
        }
        if (mod.FromCatalogData && items.FirstOrDefault(i => ProtectedFiles.Contains(i.Relative)) is { } clash)
            throw new InvalidDataException($"La receta escribiría {clash.Relative}, que es de ILION o de su base: no se instala.");
        var missing = mod.Required.Select(r => r.Replace("{tag}", download.Tag)).Where(r => !items.Any(i => i.Relative.Equals(r, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (missing.Length > 0) throw new InvalidDataException("La publicación no trae lo que espera la receta: " + string.Join(", ", missing) + ". No se instala.");
        if (mod.BuiltMinSize > 0 && items.FirstOrDefault(i => i.Relative.Equals(mod.Marker, StringComparison.OrdinalIgnoreCase)) is { } marker && new FileInfo(marker.Source).Length < mod.BuiltMinSize)
            throw new InvalidDataException($"{Path.GetFileName(mod.Marker)} está sin construir (falta el paso del setup de su autor). No se instala.");
        // La configuración que ya tiene el usuario se conserva
        items.RemoveAll(i => mod.KeepIfPresent.Contains(i.Relative, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(exeDir, i.Relative)));
        return [.. items.DistinctBy(i => i.Relative, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Copia los archivos en la carpeta del ejecutable como una operación restaurable.</summary>
    public OperationManifest Install(string gameId, string exeDir, InstallableMod mod, ModDownload download, string? proxy = null, IEnumerable<ModPlanItem>? official = null, string? batch = null, bool updating = false)
    {
        var writes = Plan(mod, download, exeDir, proxy, updating).Concat(official ?? []).DistinctBy(i => i.Relative, StringComparer.OrdinalIgnoreCase).Select(i =>
        {
            var current = Path.Combine(exeDir, i.Relative);
            return new PlannedWrite(i.Relative, i.Source, File.Exists(current) ? ComponentUpdateService.Sha256(current) : null);
        }).ToList();
        return operations.Apply(gameId, exeDir, ModBatch.Kind, $"{mod.Name} {download.Tag}" + (proxy is null ? "" : $" ({proxy})"), writes, $"github.com/{mod.Repository}", download.Tag, batch);
    }

    private static string Sha(string path) { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
}
