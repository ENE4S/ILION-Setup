// ILION Setup — GPU del equipo y su arquitectura (mismos identificadores que ILION y Central: IlionCommunity kArchs).
// Clasificación tomada de ILION CENTRAL-01 (Desktop/Architecture.cs), sin dependencias de su interfaz.
using System.Runtime.InteropServices;
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

    /// <summary>Gráficas del equipo: las que Windows tiene activas ahora. El registro (clase Display) guarda también las tarjetas
    /// retiradas (un RX 9070 que sustituyó a un 6700 XT dejaba el 6700 XT el primero de la lista), así que de él solo se toman
    /// los nombres de las que D3DKMT da como activas. Si Windows no contesta (máquina virtual, Escritorio remoto, Windows
    /// antiguo), se usa la lista del registro como hasta la 0.4.2.</summary>
    [SupportedOSPlatform("windows")]
    public static string[] Installed()
    {
        var registry = RegistryAdapters();
        var active = ActiveDeviceIds();
        var names = OnlyActive(registry, active);
        return names.Length > 0 ? names : [.. registry.Select(r => r.Name).Distinct()];
    }

    /// <summary>De las entradas del registro (nombre, MatchingDeviceId), las que corresponden a un adaptador activo
    /// (fabricante, dispositivo). Vacío si no hay adaptadores activos que casen.</summary>
    public static string[] OnlyActive(IEnumerable<(string Name, string MatchingId)> registry, IEnumerable<(uint Vendor, uint Device)> active)
    {
        var ids = active.Select(a => $"VEN_{a.Vendor:X4}&DEV_{a.Device:X4}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. registry.Where(r => ids.Any(id => r.MatchingId.Contains(id, StringComparison.OrdinalIgnoreCase))).Select(r => r.Name).Distinct()];
    }

    [SupportedOSPlatform("windows")]
    private static List<(string Name, string MatchingId)> RegistryAdapters()
    {
        var list = new List<(string, string)>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var sub in cls?.GetSubKeyNames() ?? [])
            {
                if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is string d && d.Length > 0) list.Add((d, k.GetValue("MatchingDeviceId") as string ?? ""));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return list;
    }

    // ---- Adaptadores activos (gdi32, D3DKMT: lo mismo que usa ILION 1.4.1.3)
    [StructLayout(LayoutKind.Sequential)] private struct EnumAdapters2 { public uint NumAdapters; public IntPtr Adapters; }
    [StructLayout(LayoutKind.Sequential)] private struct AdapterInfo { public uint Handle; public uint LuidLow; public int LuidHigh; public uint Sources; public int Precise; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint Handle; public int Type; public IntPtr Data; public uint Size; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryDeviceIds { public uint PhysicalIndex, Vendor, Device, SubVendor, SubSystem, Revision, BusType; }
    [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint Handle; }
    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo data);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter data);
    private const int PhysicalAdapterDeviceIds = 31; // KMTQAITYPE_PHYSICALADAPTERDEVICEIDS

    [SupportedOSPlatform("windows")]
    public static List<(uint Vendor, uint Device)> ActiveDeviceIds()
    {
        var found = new List<(uint, uint)>();
        try
        {
            var e = new EnumAdapters2();
            if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0 || e.NumAdapters > 64) return found;
            var size = Marshal.SizeOf<AdapterInfo>();
            var buffer = Marshal.AllocHGlobal(size * (int)e.NumAdapters);
            var ids = Marshal.AllocHGlobal(Marshal.SizeOf<QueryDeviceIds>());
            try
            {
                e.Adapters = buffer;
                if (D3DKMTEnumAdapters2(ref e) != 0) return found;
                for (var i = 0; i < e.NumAdapters; i++)
                {
                    var info = Marshal.PtrToStructure<AdapterInfo>(buffer + i * size);
                    Marshal.StructureToPtr(new QueryDeviceIds(), ids, false);
                    var q = new QueryAdapterInfo { Handle = info.Handle, Type = PhysicalAdapterDeviceIds, Data = ids, Size = (uint)Marshal.SizeOf<QueryDeviceIds>() };
                    if (D3DKMTQueryAdapterInfo(ref q) == 0 && Marshal.PtrToStructure<QueryDeviceIds>(ids) is { Vendor: not 0 } d) found.Add((d.Vendor, d.Device));
                    var c = new CloseAdapter { Handle = info.Handle };
                    D3DKMTCloseAdapter(ref c);
                }
            }
            finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(ids); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
        return found;
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
