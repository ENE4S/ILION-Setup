// ILION CENTRAL-01 0.7 — Cambio de DLL base, desinstalación completa restaurable y catálogo de variantes de escalado de ILION.
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Ilion.Client;

public enum UninstallKind { Safe, Full }
public sealed record InventoryItem(string RelativePath, string Category, string? Sha256);

public sealed partial class OptiScalerInstaller
{
    private static readonly string[] InstallFolders = ["OptiScaler", "ILION", "D3D12_Optiscaler"];

    public static bool IsOptiscalerBinary(string path) => IsOptiBinary(path);

    // ---------------------------------------------------------------- DLL base
    /// <summary>Comprueba una DLL principal candidata. Devuelve el motivo de bloqueo o null.</summary>
    public static string? CheckBase(string currentBinary, string newDll)
    {
        if (!File.Exists(newDll)) return "El archivo elegido no existe.";
        if (Path.GetFullPath(newDll).Equals(Path.GetFullPath(currentBinary), StringComparison.OrdinalIgnoreCase)) return "Has elegido la DLL que ya está instalada.";
        if (!IsOptiBinary(newDll)) return "La DLL elegida no es un binario x64 de OptiScaler (ni oficial ni ILION).";
        if (ComponentUpdateService.Sha256(newDll) == ComponentUpdateService.Sha256(currentBinary)) return "La DLL elegida es idéntica a la instalada.";
        return null;
    }

    /// <summary>Sustituye solo la DLL principal que usa el método de carga actual. No toca carpetas, dependencias ni el INI.</summary>
    public OperationManifest ReplaceBase(string gameId, string directory, string currentBinary, string newDll)
    {
        if (CheckBase(currentBinary, newDll) is { } block) throw new InvalidOperationException(block);
        var relative = Path.GetRelativePath(directory, currentBinary);
        if (relative.Contains(Path.DirectorySeparatorChar) || relative.StartsWith("..")) throw new InvalidOperationException("La DLL principal no está junto al ejecutable.");
        var copy = Path.Combine(DataDir, "Cache", "base-" + Guid.NewGuid().ToString("N") + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(newDll, copy);
        var edition = EditionOf(copy);
        try
        {
            var op = operations.Apply(gameId, directory, "DLL base", $"DLL base {relative} → {Title(copy, edition)}",
                [new PlannedWrite(relative, copy, ComponentUpdateService.Sha256(currentBinary), FileVersionInfo.GetVersionInfo(copy).FileVersion ?? "")],
                "Archivo local: " + newDll, DisplayVersion(copy, edition));
            // El registro sigue reconociendo el binario como propio, pero solo si es de ESTA carpeta (puede haber una instalación
            // antigua registrada en otra carpeta con un binario del mismo nombre)
            if (InstalledRaw(gameId) is { } manifest && SameFolder(manifest.Directory, directory))
                SaveManifest(manifest with { Edition = edition, Version = DisplayVersion(copy, edition),
                    Files = [.. manifest.Files.Select(f => f.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase) ? f with { Sha256 = ComponentUpdateService.Sha256(copy) } : f)] });
            return op;
        }
        finally { File.Delete(copy); }
    }

    // ---------------------------------------------------------------- Desinstalación completa
    /// <summary>Inventario de la instalación: registrados, modificados y contenido de sus carpetas sin registrar.</summary>
    public IReadOnlyList<InventoryItem> Inventory(string gameId, string directory, string? binary)
    {
        var dir = Path.GetFullPath(directory).TrimEnd('\\');
        var manifest = InstalledRaw(gameId);
        var registered = manifest?.Files.ToDictionary(f => f.RelativePath, f => f.Sha256, StringComparer.OrdinalIgnoreCase) ?? new(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (binary is not null && File.Exists(binary)) paths.UnionWith(AdoptionCandidates(dir, binary));
        foreach (var folder in InstallFolders)
        {
            var full = Path.Combine(dir, folder);
            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                paths.UnionWith(Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dir, f)));
        }
        paths.UnionWith(registered.Keys.Where(k => File.Exists(Path.Combine(dir, k))));
        return paths.Order(StringComparer.OrdinalIgnoreCase).Select(rel =>
        {
            var sha = ComponentUpdateService.Sha256(Path.Combine(dir, rel));
            var category = registered.TryGetValue(rel, out var known) ? (known == sha || IsBinaryOf(dir, rel) ? "Registrado" : "Modificado") : "De la instalación, sin registrar";
            return new InventoryItem(rel, category, sha);
        }).ToArray();
    }

    private static bool IsBinaryOf(string dir, string rel) => !rel.Contains('\\') && Proxies.Contains(rel, StringComparer.OrdinalIgnoreCase) && IsOptiBinary(Path.Combine(dir, rel));

    /// <summary>Desinstalación con respaldo de todo lo retirado (restaurable). Segura: solo registrado y sin cambios. Completa: todo el inventario.</summary>
    public (OperationManifest? Operation, string[] Kept) UninstallWith(string gameId, string directory, string? binary, UninstallKind kind)
    {
        var dir = Path.GetFullPath(directory).TrimEnd('\\');
        var items = Inventory(gameId, dir, binary);
        var remove = kind == UninstallKind.Full ? items : items.Where(i => i.Category == "Registrado").ToArray();
        var kept = items.Except(remove).Select(i => i.RelativePath).ToArray();
        OperationManifest? op = null;
        if (remove.Count > 0)
            op = operations.Apply(gameId, dir, kind == UninstallKind.Full ? "Desinstalación completa" : "Desinstalación",
                $"Desinstalar OptiScaler ({(kind == UninstallKind.Full ? "completa" : "segura")}, {remove.Count} archivos)",
                remove.Select(i => new PlannedWrite(i.RelativePath, null, i.Sha256)).ToList(), "Instalación local", "");
        // Carpetas: solo se eliminan si quedaron vacías
        foreach (var folder in InstallFolders.Select(f => Path.Combine(dir, f)).Where(Directory.Exists))
            foreach (var sub in Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories).Append(folder).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub);
        if (InstalledRaw(gameId) is not null && (kind == UninstallKind.Full || !File.Exists(Path.Combine(dir, InstalledRaw(gameId)!.Proxy))))
            File.Move(ManifestPath(gameId), ManifestPath(gameId) + "." + DateTimeOffset.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N")[..6] + ".retirado");
        return (op, kept);
    }
}

// ---------------------------------------------------------------- Variantes de escalado de ILION
public enum VariantFamily { Fsr4, HelixSR, OtherFfx, Dlss4 }
public sealed record UpscalerVariant(string Key, string Display, VariantFamily Family, string Path, bool Valid, string? Problem, bool Legacy);

/// <summary>Réplica en lectura del catálogo de ILION (misc/IlionUpscalers.h): solo archivos, no carga nada.</summary>
public static class IlionVariants
{
    public const string FfxDll = "amd_fidelityfx_upscaler_dx12.dll";

    public static IReadOnlyList<UpscalerVariant> Scan(string exeDir, string? optiDllPath, bool includeDriver = true)
    {
        var result = new List<UpscalerVariant>();
        bool Known(string key) => result.Any(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var root = Path.Combine(exeDir, "ILION", "Escaladores");
        if (Directory.Exists(root))
            foreach (var family in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                var familyName = Path.GetFileName(family);
                if (familyName.Equals("HelixSR", StringComparison.OrdinalIgnoreCase))
                {
                    AddHelix(family, "HelixSR", result);
                    foreach (var sub in Directory.EnumerateDirectories(family).Order(StringComparer.OrdinalIgnoreCase)) AddHelix(sub, "HelixSR/" + Path.GetFileName(sub), result);
                    continue;
                }
                foreach (var sub in Directory.EnumerateDirectories(family).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var key = familyName + "/" + Path.GetFileName(sub);
                    if (familyName.Equals("DLSS4", StringComparison.OrdinalIgnoreCase)) { AddD4r(sub, key, false, result); continue; }
                    var dll = System.IO.Path.Combine(sub, FfxDll);
                    if (!File.Exists(dll) && familyName.Equals("FSR4", StringComparison.OrdinalIgnoreCase)) dll = System.IO.Path.Combine(sub, "amdxcffx64.dll");
                    if (!File.Exists(dll)) continue;
                    var fsr4 = familyName.Equals("FSR4", StringComparison.OrdinalIgnoreCase);
                    result.Add(new(key, $"{(fsr4 ? "FSR 4" : familyName)} · {Path.GetFileName(sub).Replace('_', ' ')}", fsr4 ? VariantFamily.Fsr4 : VariantFamily.OtherFfx, dll, true, null, false));
                }
            }
        // Instalaciones anteriores: ILION\FSR_Versions\<n> y <OptiDllPath>\FSR4\<n>
        var optiRoot = optiDllPath is { Length: > 0 } p && !p.Equals("auto", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFullPath(System.IO.Path.Combine(exeDir, p)) : System.IO.Path.Combine(exeDir, "OptiScaler"); // por defecto .\OptiScaler (OptiScaler.ini)
        foreach (var legacy in new[] { System.IO.Path.Combine(exeDir, "ILION", "FSR_Versions"), System.IO.Path.Combine(optiRoot, "FSR4") }.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
            foreach (var sub in Directory.EnumerateDirectories(legacy).Order(StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(sub);
                if (!Known(name) && File.Exists(System.IO.Path.Combine(sub, FfxDll)))
                    result.Add(new(name, $"FSR 4 · {name} (instalación anterior)", VariantFamily.Fsr4, System.IO.Path.Combine(sub, FfxDll), true, null, true));
            }
        var d4r = System.IO.Path.Combine(exeDir, "d4r"); // ILION: carpeta d4r junto a la DLL principal
        if (!Known("d4r")) AddD4r(d4r, "d4r", true, result);
        if (includeDriver && !Known("FSR4/AMD driver") && DriverDll() is { } driver)
            result.Add(new("FSR4/AMD driver", "FSR 4 · Driver", VariantFamily.Fsr4, driver, true, null, false));
        return result;
    }

    private static string? driverCache; private static bool driverScanned;
    private static string? DriverDll()
    {
        if (driverScanned) return driverCache;
        driverScanned = true;
        try
        {
            var repo = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "DriverStore", "FileRepository");
            // El controlador lo instala en FileRepository\<inf>\<subcarpeta>\amdxcffx64.dll
            driverCache = Directory.EnumerateDirectories(repo, "u0*").SelectMany(d => Directory.EnumerateFiles(d, "amdxcffx64.dll", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return driverCache;
    }

    /// <summary>Igual que ILION (IlionUpscalers.h): la familia sale del archivo. HelixSR: "HXSRNET1" a 32 bytes del final;
    /// HLX FSR 4: "HLXFSR4" en el bloque final de 32 bytes. Sin marca no es seleccionable, se llame como se llame.</summary>
    private static void AddHelix(string folder, string key, List<UpscalerVariant> result)
    {
        string? firstProblem = null; string? firstPath = null;
        foreach (var name in new[] { "helixsr_upscaler.dll", "helixsr.dll", "amd_fidelityfx_dx12.dll", FfxDll })
        {
            var dll = System.IO.Path.Combine(folder, name);
            if (!File.Exists(dll)) continue;
            var (helix, hlx) = Markers(dll);
            if (helix || hlx)
            {
                var label = hlx ? $"HLX FSR 4 · {System.IO.Path.GetFileName(folder)}" : key == "HelixSR" ? "HelixSR" : $"HelixSR · {System.IO.Path.GetFileName(folder)}";
                result.Add(new(key, label, VariantFamily.HelixSR, dll, true, null, false));
                return;
            }
            firstPath ??= dll;
            firstProblem ??= name == FfxDll ? "Es el FSR de AMD, no HelixSR (falta la marca HXSRNET1/HLXFSR4)." : "El archivo no contiene la marca de HelixSR (HXSRNET1/HLXFSR4).";
        }
        if (firstPath is not null)
            result.Add(new(key, key == "HelixSR" ? "HelixSR" : $"HelixSR · {System.IO.Path.GetFileName(folder)}", VariantFamily.HelixSR, firstPath, false, firstProblem, false));
    }

    private static (bool Helix, bool Hlx) Markers(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            if (stream.Length < 32) return (false, false);
            stream.Seek(-32, SeekOrigin.End);
            var trailer = new byte[32];
            stream.ReadExactly(trailer);
            return (trailer.AsSpan(0, 8).SequenceEqual("HXSRNET1"u8), trailer.AsSpan().IndexOf("HLXFSR4"u8) >= 0);
        }
        catch (IOException) { return (false, false); }
    }

    private static void AddD4r(string folder, string key, bool legacy, List<UpscalerVariant> result)
    {
        if (!File.Exists(System.IO.Path.Combine(folder, "_nvngx.dll")) || !File.Exists(System.IO.Path.Combine(folder, "d4r.ini"))) return;
        var model = File.ReadLines(System.IO.Path.Combine(folder, "d4r.ini")).Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("Model", StringComparison.OrdinalIgnoreCase))?.Split('=').ElementAtOrDefault(1)?.Trim();
        var valid = model is null || model.StartsWith('K') || model.StartsWith('k');
        result.Add(new(key, legacy ? "DLSS 4 · modelo K (d4r, instalación anterior)" : $"DLSS 4 · {System.IO.Path.GetFileName(folder).Replace('_', ' ')} (d4r)", VariantFamily.Dlss4, folder, valid,
            valid ? null : $"d4r.ini pide el modelo {model}; esta versión solo admite K.", legacy));
    }
}
