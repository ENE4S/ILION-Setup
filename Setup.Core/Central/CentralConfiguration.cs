using System.Diagnostics;
using System.Text;

namespace Ilion.Client;

public sealed record DetectedLibrary(string Name, string Family, string RelativePath, string Version, string Evidence);

public sealed class TechnologyInspector
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nvngx_dlss.dll"] = "DLSS Super Resolution", ["nvngx_dlssg.dll"] = "DLSS Frame Generation",
        ["nvngx_dlssd.dll"] = "DLSS Ray Reconstruction", ["sl.interposer.dll"] = "Streamline",
        ["sl.common.dll"] = "Streamline", ["sl.dlss.dll"] = "Streamline DLSS", ["sl.dlss_g.dll"] = "Streamline FG",
        ["libxess.dll"] = "XeSS", ["libxess_dx11.dll"] = "XeSS", ["libxess_fg.dll"] = "XeFG", ["libxell.dll"] = "XeLL",
        ["amd_fidelityfx_dx12.dll"] = "FidelityFX DX12", ["amd_fidelityfx_vk.dll"] = "FidelityFX Vulkan",
        ["amd_fidelityfx_upscaler_dx12.dll"] = "FSR DX12", ["amd_fidelityfx_framegeneration_dx12.dll"] = "FSR FG",
        ["amd_fidelityfx_loader_dx12.dll"] = "FidelityFX Loader", ["OptiScaler.dll"] = "OptiScaler",
        ["ReShade64.dll"] = "ReShade", ["ReShade32.dll"] = "ReShade", ["OptiPatcher.asi"] = "OptiPatcher",
        ["dxgi.dll"] = "Proxy posible", ["d3d12.dll"] = "Proxy posible", ["winmm.dll"] = "Proxy posible",
        ["version.dll"] = "Proxy posible", ["dinput8.dll"] = "Proxy posible", ["ilion-demo.dll"] = "ILION ficticio",
        ["renderer-demo.dll"] = "DLL ficticia",
        ["sl.dlss_d.dll"] = "Streamline", ["sl.reflex.dll"] = "Streamline", ["sl.pcl.dll"] = "Streamline", ["sl.nis.dll"] = "Streamline",
        ["sl.deepdvc.dll"] = "Streamline", ["sl.directsr.dll"] = "Streamline", ["amd_fidelityfx_loader_vk.dll"] = "FidelityFX Loader",
        ["amd_fidelityfx_upscaler_vk.dll"] = "FSR Vulkan", ["amd_fidelityfx_framegeneration_vk.dll"] = "FSR FG Vulkan", ["nvapi64.dll"] = "NVAPI / fakenvapi", ["dlss-enabler-headless.dll"] = "DLSS Enabler (Artur)", ["dlss-enabler.dll"] = "DLSS Enabler (Artur)",
        ["dlssg_to_fsr3_amd_is_better.dll"] = "Nukem dlssg-to-fsr3", ["helixsr_upscaler.dll"] = "HelixSR", ["_nvngx.dll"] = "NGX (cargador)",
        ["amd_fidelityfx_denoiser_dx12.dll"] = "FidelityFX Denoiser", ["NvLowLatencyVk.dll"] = "Reflex Vulkan", ["nvngx_deepdvc.dll"] = "RTX Dynamic Vibrance", ["sl.nvperf.dll"] = "Streamline"
    };
    private readonly Dictionary<string, (long Length, DateTime Stamp, string Version)> versions = new(StringComparer.OrdinalIgnoreCase);
    // Carpetas de datos donde nunca hay DLL de escalado: no se recorren. En juegos Unreal grandes agotaban el presupuesto antes de llegar a
    // Engine\Plugins\Runtime\Nvidia\...\Win64, que además está a 8 niveles (informe de ARC Raiders sin tecnologías detectadas)
    private static readonly HashSet<string> DataFolders = new(StringComparer.OrdinalIgnoreCase)
        { "Paks", "Movies", "Localization", "Splash", "Saved", "Logs", "Videos", "Music", "Audio", "Sounds", "Textures", "ShaderCache", "__MACOSX" };
    /// <summary>La última búsqueda se cortó por el presupuesto (puede faltar alguna tecnología).</summary>
    public bool LastIncomplete { get; private set; }

    public DetectedLibrary[] Inspect(string root)
    {
        if (!Directory.Exists(root)) return [];
        var files = new List<DetectedLibrary>();
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var budget = 40000;
        while (pending.TryDequeue(out var directory) && budget > 0)
        {
            budget--;
            try
            {
                if ((File.GetAttributes(directory.Path) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    if (--budget < 0) break;
                    var attributes = File.GetAttributes(path);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (directory.Depth < 10 && !DataFolders.Contains(Path.GetFileName(path))) pending.Enqueue((path, directory.Depth + 1));
                        continue;
                    }
                    var name = Path.GetFileName(path);
                    if (!Known.TryGetValue(name, out var family)) continue;
                    var file = new FileInfo(path);
                    if (!versions.TryGetValue(path, out var cached) || cached.Length != file.Length || cached.Stamp != file.LastWriteTimeUtc)
                    {
                        var metadata = FileVersionInfo.GetVersionInfo(path);
                        cached = (file.Length, file.LastWriteTimeUtc, metadata.FileVersion ?? metadata.ProductVersion ?? "No declarada");
                        if (versions.Count >= 8192) versions.Clear();
                        versions[path] = cached;
                    }
                    files.Add(new DetectedLibrary(name, family, Path.GetRelativePath(root, path), cached.Version,
                        family == "Proxy posible" ? "Nombre ambiguo; requiere identificación" : "Archivo presente; compatibilidad sin verificar"));
                }
            }
            catch (Exception error) when (LibraryDiscovery.IsReadError(error) || error is System.ComponentModel.Win32Exception) { }
        }
        LastIncomplete = budget <= 0 && pending.Count > 0;
        return files.OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public sealed record IniSetting(string Section, string Key, string Value);
public sealed class IniDocument
{
    private readonly List<string> lines;
    private readonly string separator;
    private IniDocument(string text)
    {
        separator = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        lines = [.. text.Replace("\r\n", "\n").Split('\n')];
    }

    public static IniDocument Parse(string text) => new(text);
    public string Text => string.Join(separator, lines);
    public IniSetting[] Settings => Scan().Select(item => item.Setting).ToArray();

    public void Set(string section, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(section) || string.IsNullOrWhiteSpace(key) ||
            section.IndexOfAny(['[', ']', '\r', '\n']) >= 0 ||
            key.IndexOfAny(['=', ';', '#', '[', ']', '\r', '\n']) >= 0 ||
            value.IndexOfAny(['\r', '\n', '\0', ';', '#']) >= 0)
            throw new ArgumentException("Usa un nombre de sección, una clave y un valor de una sola línea sin delimitadores.");
        var matches = Scan().Where(item => Same(item.Setting.Section, section) && Same(item.Setting.Key, key)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException($"Clave ambigua: [{section}] {key}.");
        if (matches.Length == 1)
        {
            var item = matches[0];
            var line = lines[item.Index];
            lines[item.Index] = line[..(line.IndexOf('=') + 1)] + value + item.Comment;
            return;
        }

        var headers = lines.Select((line, index) => (Name: Header(line), Index: index)).Where(item => item.Name is not null).ToArray();
        var target = headers.Where(item => Same(item.Name!, section)).ToArray();
        if (target.Length > 1) throw new InvalidOperationException($"Sección duplicada: [{section}].");
        if (target.Length == 0)
        {
            if (lines.Count == 1 && lines[0].Length == 0) lines.Clear();
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }
        var next = headers.FirstOrDefault(item => item.Index > target[0].Index);
        lines.Insert(next.Name is null ? lines.Count : next.Index, $"{key}={value}");
    }

    private IEnumerable<(int Index, IniSetting Setting, string Comment)> Scan()
    {
        string section = "";
        for (var index = 0; index < lines.Count; index++)
        {
            var raw = lines[index];
            var line = raw.Trim();
            if (Header(raw) is { } header) { section = header; continue; }
            var equal = raw.IndexOf('=');
            if (section.Length == 0 || equal < 1 || line.StartsWith(';') || line.StartsWith('#')) continue;
            var value = raw[(equal + 1)..];
            var comment = -1;
            for (var offset = 1; offset < value.Length; offset++)
                if (char.IsWhiteSpace(value[offset - 1]) && value[offset] is ';' or '#') { comment = offset; break; }
            var suffix = comment < 0 ? "" : " " + value[comment..];
            yield return (index, new IniSetting(section, raw[..equal].Trim(),
                (comment < 0 ? value : value[..comment]).Trim()), suffix);
        }
    }
    private static string? Header(string line) =>
        line.Trim() is { Length: > 2 } trimmed && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1].Trim() : null;
    private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);
}

public sealed record ConfigField(string Label, string Section, string Key);
public interface IConfigurationAdapter
{
    string Label { get; }
    IReadOnlyList<ConfigField> Fields { get; }
    string[] ProxyCandidates { get; }
}

public sealed class OptiScalerConfiguration : IConfigurationAdapter
{
    public string Label => "OptiScaler oficial";
    public IReadOnlyList<ConfigField> Fields { get; } =
    [
        new("Escalador DX11", "Upscalers", "Dx11Upscaler"), new("Escalador DX12", "Upscalers", "Dx12Upscaler"),
        new("Escalador Vulkan", "Upscalers", "VulkanUpscaler"), new("Frame Generation", "FrameGen", "Enabled"),
        new("Entrada de FG", "FrameGen", "FGInput"), new("Salida de FG", "FrameGen", "FGOutput"),
        new("Reemplazo Nvngx FG", "FrameGen", "FGNvngxReplacement"), new("Modelo DLSS / calidad", "DLSS", "RenderPresetQuality"),
        new("Modelo FSR4", "FSR", "Fsr4Preset"), new("Relación del modo calidad", "QualityOverrides", "QualityRatioQuality"),
        new("Directorio OptiScaler", "Libraries", "OptiDllPath"), new("Ruta DLSS", "Libraries", "NvngxDlssPath"),
        new("Ruta XeSS", "Libraries", "XeSSPath"), new("Ruta XeFG", "Libraries", "XeFGPath"),
        new("Ruta FidelityFX DX12", "Libraries", "FfxDx12Path"), new("Ruta de plugins", "Plugins", "Path"),
        new("Multiplicador XeFG", "XeFG", "InterpolationCount"), new("Multiplicador DLSS FG", "DLSSG", "InterpolationCount"),
        new("Entrada DLSS", "Inputs", "EnableDlssInputs"), new("Entrada XeSS", "Inputs", "EnableXeSSInputs"),
        new("Entrada FSR 2", "Inputs", "EnableFsr2Inputs"), new("Entrada FSR 3", "Inputs", "EnableFsr3Inputs"),
        new("Entrada FidelityFX", "Inputs", "EnableFfxInputs"),
        new("Cargar plugins ASI (OptiPatcher)", "Plugins", "LoadAsiPlugins"), new("Fakenvapi", "fakenvapi", "UseFakenvapi")
    ];
    public string[] ProxyCandidates => ["dxgi.dll", "d3d12.dll", "winmm.dll", "version.dll", "dinput8.dll"];
}

public sealed class IlionConfiguration : IConfigurationAdapter
{
    public string Label => "OptiScaler ILION";
    public IReadOnlyList<ConfigField> Fields => [];
    public string[] ProxyCandidates => [];
}


