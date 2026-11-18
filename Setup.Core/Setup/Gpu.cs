// ILION Setup — GPU del equipo y su arquitectura (mismos identificadores que ILION y Central: IlionCommunity kArchs).
// Clasificación tomada de ILION CENTRAL-01 (Desktop/Architecture.cs), sin dependencias de su interfaz.
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Ilion.Setup;

public sealed record GpuArch(string Id, string Marca, string Nombre, string Ejemplos);
public sealed record ArchTable(GpuArch[] Arquitecturas);
public sealed record GpuDetection(string Name, GpuArch? Arch, string[] All);

public static class Gpu
{
    public static ArchTable Load(Stream json) =>
        JsonSerializer.Deserialize<ArchTable>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new([]);

    public static string BrandName(string marca) => marca switch { "amd" => "AMD", "nvidia" => "NVIDIA", "intel" => "Intel", _ => marca };

    /// <summary>Arquitectura a partir del nombre comercial (null si no se reconoce).</summary>
    public static string? Classify(string name)
    {
        var n = name.ToUpperInvariant();
        if (Regex.Match(n, @"RTX\s*A?(\d)0\d0") is { Success: true } rtx) return rtx.Groups[1].Value switch { "2" => "rtx20", "3" => "rtx30", "4" => "rtx40", "5" => "rtx50", _ => null };
        if (Regex.IsMatch(n, @"GTX\s*(9\d0|10\d0|16\d0)|TITAN X")) return "nv_gtx";
        if (Regex.Match(n, @"\bRX\s*(\d)\d{3}") is { Success: true } rx) return rx.Groups[1].Value switch { "5" => "amd_old", "6" => "rdna2", "7" => "rdna3", "9" => "rdna4", _ => null };
        if (Regex.IsMatch(n, @"\bRX\s*[45]\d0\b|VEGA|RADEON VII")) return "amd_old";
        if (Regex.IsMatch(n, @"RADEON\D*\d{3,4}M\b|RADEON GRAPHICS|Z1|AMD CUSTOM GPU")) return "amd_apu";
        if (Regex.IsMatch(n, @"ARC.*\bA\d{3}")) return "arc_a";
        if (Regex.IsMatch(n, @"ARC.*\bB\d{3}")) return "arc_b";
        if (Regex.IsMatch(n, @"ARC.*\b1[34]0[VT]")) return "intel_xe2";
        if (Regex.IsMatch(n, @"INTEL.*(UHD|IRIS|HD GRAPHICS)")) return "intel_other";
        return null;
    }

    /// <summary>Gráficas instaladas según el registro de Windows (clase Display).</summary>
    [SupportedOSPlatform("windows")]
    public static string[] Installed()
    {
        var names = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? [])
            {
                if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string d && d.Length > 0 && !names.Contains(d)) names.Add(d);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return [.. names];
    }

    /// <summary>La principal: una dedicada reconocida antes que una integrada.</summary>
    public static GpuDetection Detect(ArchTable table, IEnumerable<string> gpus)
    {
        var all = gpus.ToArray();
        var known = all.Select(g => (g, a: table.Arquitecturas.FirstOrDefault(x => x.Id == Classify(g)))).Where(x => x.a is not null).ToArray();
        var pick = known.OrderBy(x => x.a!.Id is "amd_apu" or "intel_other" or "intel_xe2" ? 1 : 0).FirstOrDefault();
        return pick.g is not null ? new(pick.g, pick.a, all) : new(all.FirstOrDefault() ?? "No detectada", null, all);
    }

    /// <summary>Grupo dentro de la marca: «rtx» para RTX 20–50.</summary>
    public static string? Group(GpuArch arch) => arch.Id.StartsWith("rtx") ? "rtx" : null;

    /// <summary>¿La arquitectura está en la lista (ids, «rtx», marca o «*»)?</summary>
    public static bool Fits(IEnumerable<string> list, GpuArch arch) => list.Any(p => p == "*" || p == arch.Id || p == arch.Marca || p == Group(arch));
}
