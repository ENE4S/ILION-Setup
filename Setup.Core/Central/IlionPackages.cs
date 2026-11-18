// ILION CENTRAL-01 — Paquetes locales de OptiScaler ILION: la persona elige el que quiera (varias versiones o compilaciones de prueba).
// CENTRAL solo lee la carpeta elegida: nunca la mueve, la modifica ni mezcla archivos de dos paquetes.
using System.Text.Json;

namespace Ilion.Client;

/// <summary>Lo que se sabe de una carpeta candidata. Problem = no se puede instalar desde ella (y por qué).</summary>
public sealed record IlionPackageInfo(string Folder, string? Binary, string Version, bool Ilion, bool Complete, int Files, string[] Missing, string? Problem)
{
    /// <summary>Texto corto del contenido para la lista.</summary>
    public string Summary => Problem ?? (Complete ? $"Paquete completo · {Files} archivos" : "Solo la DLL principal (sin ILION\\, OptiScaler\\ ni configuración)");
}

public sealed class IlionPackageStore(string dataDirectory)
{
    private string HistoryFile => Path.Combine(dataDirectory, "Ajustes", "paquetes-ilion.json");
    private string LegacyFile => Path.Combine(dataDirectory, "Ajustes", "paquete-ilion.txt");
    private const int Keep = 10;

    /// <summary>Qué hay en la carpeta: binario de OptiScaler, edición ILION, versión y si es un paquete completo.</summary>
    public static IlionPackageInfo Inspect(string folder)
    {
        if (!Directory.Exists(folder)) return new(folder, null, "", false, false, 0, [], "La carpeta ya no existe.");
        OptiPackage package;
        try { package = OptiScalerInstaller.InspectFolder(folder, "Paquete local: " + folder); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        { return new(folder, null, "", false, false, 0, [], "No contiene la DLL de OptiScaler (dxgi.dll u OptiScaler.dll) de 64 bits."); }
        var ilion = package.Edition == OptiEdition.Ilion;
        int files;
        try { files = Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 8 }).Count(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { files = 0; }
        // Lo esencial de un paquete completo de ILION: la DLL, su configuración y sus carpetas
        var missing = new List<string>();
        if (!File.Exists(Path.Combine(folder, "OptiScaler.ini"))) missing.Add("OptiScaler.ini");
        if (!Directory.Exists(Path.Combine(folder, "OptiScaler"))) missing.Add("OptiScaler\\");
        if (!Directory.Exists(Path.Combine(folder, "ILION"))) missing.Add("ILION\\");
        var complete = missing.Count == 0;
        return new(folder, package.MainBinary, package.Version, ilion, complete, files, [.. missing],
            ilion ? null : "La DLL de esta carpeta no es la edición ILION.");
    }

    /// <summary>Carpetas usadas antes (la última primero). Incluye la del ajuste antiguo (paquete-ilion.txt).</summary>
    public string[] History()
    {
        var list = new List<string>();
        try
        {
            if (File.Exists(HistoryFile) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(HistoryFile)) is { } saved) list.AddRange(saved);
            if (File.Exists(LegacyFile) && File.ReadAllText(LegacyFile).Trim() is { Length: > 0 } legacy && !list.Contains(legacy, StringComparer.OrdinalIgnoreCase)) list.Add(legacy);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return [.. list.Where(f => f.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase).Take(Keep)];
    }

    /// <summary>La última carpeta válida usada (la que se propone primero).</summary>
    public string? Last => History().FirstOrDefault(Directory.Exists);

    /// <summary>Guarda la carpeta como la última usada (sin tocar la carpeta).</summary>
    public void Remember(string folder)
    {
        var list = History().Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)).Prepend(Path.GetFullPath(folder)).Take(Keep).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
        File.WriteAllText(HistoryFile, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(LegacyFile, list[0]); // compatibilidad con versiones anteriores de CENTRAL
    }

    /// <summary>Quita una carpeta de la lista (la carpeta no se toca).</summary>
    public void Forget(string folder)
    {
        var list = History().Where(f => !f.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryFile)!);
        File.WriteAllText(HistoryFile, JsonSerializer.Serialize(list));
        if (File.Exists(LegacyFile) && File.ReadAllText(LegacyFile).Trim().Equals(folder, StringComparison.OrdinalIgnoreCase)) File.Delete(LegacyFile);
    }

    /// <summary>Busca paquetes de ILION en las carpetas habituales (Escritorio, Descargas, Documentos): carpetas con «ILION» en el nombre,
    /// hasta 3 niveles, que contienen la DLL. Solo lectura.</summary>
    public static string[] Discover(IEnumerable<string>? roots = null, CancellationToken token = default)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        roots ??= new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(profile, "OneDrive", "Escritorio"), Path.Combine(profile, "OneDrive", "Desktop") };
        var found = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in roots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root, "*", options).Prepend(root).Where(d => Path.GetFileName(d).Contains("ILION", StringComparison.OrdinalIgnoreCase)).ToArray(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs)
            {
                token.ThrowIfCancellationRequested();
                if (found.Count >= 12) return [.. found];
                if (!new[] { "dxgi.dll", "OptiScaler.dll" }.Any(n => File.Exists(Path.Combine(dir, n)))) continue;
                if (found.Any(f => f.Equals(dir, StringComparison.OrdinalIgnoreCase))) continue;
                try { if (OptiScalerInstaller.EditionOf(OptiScalerInstaller.InspectFolder(dir, "").MainBinary) == OptiEdition.Ilion) found.Add(dir); }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException) { }
            }
        }
        return [.. found];
    }
}
