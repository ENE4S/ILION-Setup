// ILION CENTRAL-01 0.6 — Clasificación de componentes, guardia de rutas y motor de operaciones restaurables.
// La política de seguridad vive aquí (núcleo), no en la interfaz: ninguna llamada puede escribir sin pasar por OperationEngine.
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Ilion.Client;

public enum Ownership { Game, OptiScaler, Mod, Unknown }
public enum UpdateMode { Standard, Advanced }

/// <summary>Ubicación + propiedad + función de un archivo detectado.</summary>
public sealed record ComponentInfo(DetectedLibrary File, Ownership Owner, string Package, string PackageRoot, string Group, string? Signer, string Reason)
{
    public ComponentRecipe? Recipe => ComponentRecipe.For(File.Name);
}

public static class ComponentClassifier
{
    private static readonly Dictionary<string, (long, DateTime, string?)> signers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Vendors = ["NVIDIA Corporation", "Advanced Micro Devices", "Intel Corporation"];

    /// <summary>Firmante Authenticode válido (o null). Cacheado por ruta, tamaño y fecha.</summary>
    public static string? SignerOf(string path)
    {
        var info = new FileInfo(path);
        lock (signers)
            if (signers.TryGetValue(path, out var hit) && hit.Item1 == info.Length && hit.Item2 == info.LastWriteTimeUtc) return hit.Item3;
        var signer = Authenticode.IsTrusted(path) ? Authenticode.Subject(path) : null;
        lock (signers) signers[path] = (info.Length, info.LastWriteTimeUtc, signer);
        return signer;
    }

    public static ComponentInfo Classify(string root, DetectedLibrary file, IReadOnlySet<string>? managedFullPaths = null, string? optiBinary = null)
    {
        var parts = file.RelativePath.Split('\\', '/');
        var dirs = parts[..^1];
        var lower = dirs.Select(p => p.ToLowerInvariant()).ToArray();
        var full = Path.GetFullPath(Path.Combine(root, file.RelativePath));
        string Join(int count) => string.Join('\\', dirs.Take(count));
        static string Pretty(string s) => s.Replace('_', ' ').Replace("DLSS4", "DLSS 4", StringComparison.OrdinalIgnoreCase).Replace("FSR4", "FSR 4", StringComparison.OrdinalIgnoreCase);

        // 1. HelixSR, en cualquier ubicación
        var helix = Array.FindIndex(lower, p => p.Contains("helixsr") || p.StartsWith("helix") || p.StartsWith("hlx-"));
        if (helix >= 0)
        {
            var end = Math.Min(dirs.Length, helix + 2);
            var name = helix + 1 < dirs.Length ? $"{Pretty(dirs[helix])} · {Pretty(dirs[helix + 1])}" : Pretty(dirs[helix]);
            if (lower.Take(helix).Contains("escaladores") && helix >= 1 && !lower[helix].Contains("helixsr")) name = $"HelixSR · {Pretty(dirs[helix])}";
            else if (helix + 1 < dirs.Length && lower[helix] == "helixsr") name = $"HelixSR · {Pretty(dirs[helix + 1])}";
            return new(file, Ownership.Mod, name, Join(end), name, null, "Pertenece a un paquete HelixSR.");
        }
        // 2. Escaladores de ILION: <Tecnología>\<Variante>
        var esc = Array.IndexOf(lower, "escaladores");
        if (esc >= 0 && esc + 2 < dirs.Length + 1)
        {
            var tech = esc + 1 < dirs.Length ? Pretty(dirs[esc + 1]) : "Escaladores";
            var variant = esc + 2 < dirs.Length ? Pretty(dirs[esc + 2]) : null;
            var name = variant is null ? tech : $"{tech} · {variant}";
            return new(file, Ownership.Mod, name, Join(Math.Min(dirs.Length, esc + 3)), name, null, "Variante de escalador distribuida con ILION, no original del juego.");
        }
        // 3. Carpetas de variantes FSR 4 (también dentro de OptiScaler)
        var fsr4 = Array.FindIndex(lower, p => p.StartsWith("fsr4"));
        if (fsr4 >= 0 && fsr4 + 1 < dirs.Length)
        {
            var name = $"FSR 4 · {Pretty(dirs[fsr4 + 1])}";
            return new(file, Ownership.Mod, name, Join(fsr4 + 2), name, null, "Variante de FSR 4 (comunitaria o alternativa).");
        }
        // 4. Directorios de mods genéricos
        var mods = Array.FindIndex(lower, p => p is "mods" or "mod" or "plugins_mods");
        if (mods >= 0)
        {
            var name = mods + 1 < dirs.Length ? Pretty(dirs[mods + 1]) : "Mods";
            return new(file, Ownership.Mod, name, Join(Math.Min(dirs.Length, mods + 2)), name, null, "Situado en un directorio de mods.");
        }
        var lowerName = file.Name.ToLowerInvariant();
        var insideOpti = lower.Contains("optiscaler") || (managedFullPaths?.Contains(full) ?? false) ||
                         (optiBinary is not null && Path.GetFullPath(optiBinary).Equals(full, StringComparison.OrdinalIgnoreCase));
        // 5. DLSS Enabler / ReShade fuera de OptiScaler → mod
        if (!insideOpti && (lowerName.StartsWith("dlss-enabler") || (lowerName == "version.dll" && (FileVersionInfo.GetVersionInfo(full).ProductName ?? "").Contains("Enabler", StringComparison.OrdinalIgnoreCase))))
            return new(file, Ownership.Mod, "DLSS Enabler", Join(dirs.Length), "DLSS Enabler", null, "Componente de DLSS Enabler.");
        if (file.Family == "ReShade") return new(file, Ownership.Mod, "ReShade", Join(dirs.Length), "ReShade", null, "Inyector de efectos ReShade.");
        // 6. OptiScaler
        if (insideOpti || lowerName is "optiscaler.asi" or "optipatcher.asi" or "dlssg_to_fsr3_amd_is_better.dll")
        {
            var group = (optiBinary is not null && Path.GetFullPath(optiBinary).Equals(full, StringComparison.OrdinalIgnoreCase)) || lowerName is "optiscaler.dll" or "optiscaler.asi" ? "Núcleo"
                : lowerName.Contains("dlssg") || lowerName.Contains("framegeneration") || lowerName is "libxess_fg.dll" or "libxell.dll" or "sl.dlss_g.dll" || lowerName.StartsWith("dlss-enabler") ? "Frame Generation"
                : lowerName.StartsWith("nvngx_dlss") || lowerName.StartsWith("libxess") || lowerName.Contains("upscaler") || lowerName is "amd_fidelityfx_dx12.dll" or "amd_fidelityfx_vk.dll" || lowerName.Contains("loader") ? "Escalado"
                : "Dependencias y plugins";
            return new(file, Ownership.OptiScaler, "OptiScaler", lower.Contains("optiscaler") ? Join(Array.IndexOf(lower, "optiscaler") + 1) : Join(dirs.Length), group, null, "Gestionado por OptiScaler.");
        }
        // 7. Zona del juego: solo es "original" si está firmado por el fabricante esperado
        var recipe = ComponentRecipe.For(file.Name);
        var vendorFile = recipe is not null || lowerName.StartsWith("nvngx") || lowerName.StartsWith("sl.") || lowerName.StartsWith("amd_") || lowerName.StartsWith("libxe");
        if (!vendorFile) return new(file, Ownership.Game, "Juego", "", "Otros componentes", null, "Componente del juego.");
        var signer = File.Exists(full) ? SignerOf(full) : null;
        var expected = recipe?.Signer;
        var trusted = signer is not null && (expected is null ? Vendors.Any(v => signer.Contains(v, StringComparison.OrdinalIgnoreCase)) : signer.Contains(expected, StringComparison.OrdinalIgnoreCase));
        if (trusted) return new(file, Ownership.Game, "Juego", "", recipe?.Vendor ?? VendorOf(lowerName), signer, $"Firmado por {signer}.");
        return new(file, Ownership.Unknown, "Reemplazos sin firma oficial", Join(dirs.Length), "Reemplazos sin firma oficial", signer,
            signer is null ? "Sin firma digital válida del fabricante: posible reemplazo, mod o archivo modificado." : $"Firmado por {signer}, no por el fabricante esperado.");
    }

    private static string VendorOf(string name) => name.StartsWith("nvngx") || name.StartsWith("sl.") ? "NVIDIA" : name.StartsWith("amd_") ? "AMD" : name.StartsWith("libxe") ? "Intel" : "Otros componentes";
}

/// <summary>Resuelve rutas de forma canónica y rechaza enlaces simbólicos, uniones y salidas de la carpeta del juego.</summary>
public static class PathGuard
{
    public static string Resolve(string root, string relative)
    {
        var rootFull = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(rootFull, relative));
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("La ruta sale de la carpeta del juego.");
        var current = rootFull.TrimEnd('\\');
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("La carpeta del juego es un enlace simbólico o unión.");
        foreach (var part in Path.GetRelativePath(rootFull, full).Split('\\'))
        {
            current = Path.Combine(current, part);
            if (!Path.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException($"«{part}» es un enlace simbólico o unión: escritura bloqueada.");
        }
        // Ruta canónica real (nombres cortos 8.3, mayúsculas, unidades montadas)
        var existing = File.Exists(full) ? full : Path.GetDirectoryName(full)!;
        if (Path.Exists(existing) && Canonical(existing) is { } canon && Canonical(rootFull.TrimEnd('\\')) is { } canonRoot &&
            !canon.StartsWith(canonRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !canon.Equals(canonRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("La ruta canónica sale de la carpeta del juego.");
        return full;
    }

    private static string? Canonical(string path)
    {
        try
        {
            using var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero); // FILE_FLAG_BACKUP_SEMANTICS (carpetas)
            if (handle.IsInvalid) return null;
            var buffer = new char[1024];
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) return null;
            var text = new string(buffer, 0, (int)length);
            return text.StartsWith(@"\\?\UNC\") ? @"\\" + text[8..] : text.StartsWith(@"\\?\") ? text[4..] : text;
        }
        catch (Exception) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, [Out] char[] buffer, uint length, uint flags);
}

// ------------------------------------------------------------------ Operaciones restaurables
public sealed record OpEntry(string RelativePath, string? BackupFile, string? OriginalSha256, string OriginalVersion, string? InstalledSha256, string InstalledVersion);
public sealed record OperationManifest(string Id, string GameId, string GameRoot, string Kind, string Label, OpEntry[] Entries,
    string Source, string Tag, DateTimeOffset When, string State, string? Batch = null)
{
    public bool Completed => State == "Completada";
    public bool Restored => State == "Restaurada";
    public bool Touches(string relative) => Entries.Any(e => e.RelativePath.Equals(relative, StringComparison.OrdinalIgnoreCase));
}
/// <summary>Una escritura planificada. SourceFile null = retirar el archivo. ExpectedCurrentSha null = el archivo no debe existir.</summary>
public sealed record PlannedWrite(string RelativePath, string? SourceFile, string? ExpectedCurrentSha, string NewVersion = "");

public sealed class OperationEngine(string dataDirectory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string GameFolder(string gameId) => Path.Combine(dataDirectory, "Backups", IniConfigStore.Safe(gameId));
    public static string ReadableName(ComponentInfo c, string fallback)
    {
        var name = c.File.Name.ToLowerInvariant();
        if (name == "_nvngx.dll") return c.Signer is { } s && s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "Núcleo NGX (NVIDIA)" : "Cargador NGX del paquete";
        return fallback;
    }

    /// <summary>Respaldo verificado de todo el conjunto → diario «Pendiente» → escrituras verificadas → «Completada». Si algo falla, se revierte.</summary>
    public OperationManifest Apply(string gameId, string root, string kind, string label, IReadOnlyList<PlannedWrite> writes, string source, string tag, string? batch = null)
    {
        if (writes.Count == 0) throw new InvalidOperationException("No hay nada que modificar.");
        if (writes.Select(w => w.RelativePath.ToLowerInvariant()).Distinct().Count() != writes.Count) throw new InvalidOperationException("Escrituras duplicadas sobre el mismo archivo.");
        var id = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        var folder = Path.Combine(GameFolder(gameId), id);
        var entries = new List<OpEntry>();
        foreach (var write in writes)
        {
            var target = PathGuard.Resolve(root, write.RelativePath);
            var current = File.Exists(target) ? ComponentUpdateService.Sha256(target) : null;
            if (current != write.ExpectedCurrentSha)
                throw new InvalidOperationException($"{write.RelativePath} cambió desde el análisis (otro programa lo modificó). Vuelve a analizar el juego.");
            string? backup = null;
            if (current is not null)
            {
                backup = Path.Combine(folder, "archivos", write.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup);
                if (ComponentUpdateService.Sha256(backup) != current) throw new IOException("El respaldo no es idéntico al original; no se modifica nada.");
            }
            var installed = write.SourceFile is null ? null : ComponentUpdateService.Sha256(write.SourceFile);
            entries.Add(new(write.RelativePath, backup, current, current is null ? "" : ComponentUpdateService.VersionOf(target), installed, write.NewVersion));
        }
        var manifest = new OperationManifest(id, gameId, Path.GetFullPath(root), kind, label, [.. entries], source, tag, DateTimeOffset.Now, "Pendiente", batch);
        Save(manifest);
        try
        {
            for (var i = 0; i < writes.Count; i++)
            {
                var target = PathGuard.Resolve(root, writes[i].RelativePath);
                if (writes[i].SourceFile is { } source1) Write(source1, target, entries[i].InstalledSha256!);
                else if (File.Exists(target)) { MakeWritable(target); File.Delete(target); }
            }
        }
        catch (Exception error)
        {
            Rollback(manifest);
            Save(manifest with { State = "Revertida" });
            throw new IOException("La operación falló y se revirtió al estado anterior: " + error.Message, error);
        }
        var done = manifest with { State = "Completada" };
        Save(done);
        return done;
    }

    /// <summary>Recupera el estado anterior exacto de todo el conjunto, o no toca nada.</summary>
    public OperationManifest Restore(OperationManifest requested)
    {
        var op = Load(requested.GameId, requested.Id) ?? throw new InvalidOperationException("No se encontró el registro de la operación.");
        if (!op.Completed) throw new InvalidOperationException(op.Restored ? "Esta operación ya se restauró." : "Solo se restauran operaciones completadas.");
        var later = All(op.GameId).Where(o => o.Completed && o.When > op.When && o.Entries.Any(e => op.Touches(e.RelativePath))).ToArray();
        if (later.Length > 0) throw new InvalidOperationException($"Hay una operación posterior sobre los mismos archivos ({later[0].Label}, {later[0].When:dd/MM HH:mm}). Restaura primero esa.");
        foreach (var entry in op.Entries)
        {
            var target = PathGuard.Resolve(op.GameRoot, entry.RelativePath);
            var current = File.Exists(target) ? ComponentUpdateService.Sha256(target) : null;
            if (current != entry.InstalledSha256 && current != entry.OriginalSha256)
                throw new InvalidOperationException($"{entry.RelativePath} cambió después de la operación (otro programa o una actualización del juego). Restauración bloqueada para no perder ese cambio.");
            if (entry.BackupFile is not null && (!File.Exists(entry.BackupFile) || ComponentUpdateService.Sha256(entry.BackupFile) != entry.OriginalSha256))
                throw new IOException($"El respaldo de {entry.RelativePath} falta o está dañado; no se toca nada.");
        }
        Rollback(op);
        var done = op with { State = "Restaurada" };
        Save(done);
        return done;
    }

    /// <summary>Operaciones interrumpidas (corte de luz, cierre forzado): se revierten al estado previo.</summary>
    public string[] RecoverPending(string gameId)
    {
        var notes = new List<string>();
        foreach (var op in All(gameId).Where(o => o.State == "Pendiente"))
        {
            try { Rollback(op); Save(op with { State = "Revertida" }); notes.Add($"Operación interrumpida revertida: {op.Label}"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { notes.Add($"No se pudo revertir {op.Label}: {error.Message}"); }
        }
        return [.. notes];
    }

    /// <summary>Al arrancar CENTRAL: revierte todas las operaciones que quedaron «Pendiente» en cualquier juego.</summary>
    public string[] RecoverAll()
    {
        var root = Path.Combine(dataDirectory, "Backups");
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root).SelectMany(dir => RecoverPending(Path.GetFileName(dir))).ToArray();
    }

    // Historial por juego en memoria: se lee del disco una vez y se invalida en cada escritura (compartido entre instancias)
    private static readonly Dictionary<string, OperationManifest[]> history = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<OperationManifest> All(string gameId)
    {
        var folder = Path.GetFullPath(GameFolder(gameId));
        lock (history) if (history.TryGetValue(folder, out var cached)) return cached;
        if (!Directory.Exists(folder)) return [];
        var list = Directory.EnumerateFiles(folder, "operacion.json", SearchOption.AllDirectories)
            .Select(f => { try { return JsonSerializer.Deserialize<OperationManifest>(File.ReadAllText(f)); } catch (JsonException) { return null; } })
            .OfType<OperationManifest>().OrderByDescending(o => o.When).ToArray();
        lock (history) history[folder] = list;
        return list;
    }

    /// <summary>Olvida el historial en memoria (otra copia de CENTRAL o una limpieza de respaldos cambió el disco).</summary>
    public static void ForgetHistory() { lock (history) history.Clear(); }

    /// <summary>null si la operación puede restaurarse ahora; si no, el motivo (respaldo dañado, cambio posterior, operación posterior).</summary>
    public string? CanRestore(OperationManifest op)
    {
        if (!op.Completed) return "La operación ya no está activa.";
        if (All(op.GameId).Any(o => o.Completed && o.When > op.When && o.Entries.Any(e => op.Touches(e.RelativePath)))) return "Hay una operación posterior sobre estos archivos: restaura primero esa.";
        foreach (var entry in op.Entries)
        {
            string target;
            try { target = PathGuard.Resolve(op.GameRoot, entry.RelativePath); } catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return e.Message; }
            var current = File.Exists(target) ? ComponentUpdateService.Sha256(target) : null;
            if (current != entry.InstalledSha256 && current != entry.OriginalSha256) return $"{Path.GetFileName(entry.RelativePath)} cambió después de la operación: restaurar lo sobrescribiría.";
            if (entry.BackupFile is not null && !File.Exists(entry.BackupFile)) return "Falta el respaldo de " + Path.GetFileName(entry.RelativePath) + ".";
        }
        return null;
    }

    /// <summary>Última operación completada que puede restaurar este archivo ahora mismo.</summary>
    public OperationManifest? RestorableFor(string gameId, string relative) =>
        All(gameId).Where(o => o.Completed && o.Touches(relative)).OrderByDescending(o => o.When).FirstOrDefault();

    private void Rollback(OperationManifest op)
    {
        foreach (var entry in op.Entries)
        {
            var target = PathGuard.Resolve(op.GameRoot, entry.RelativePath);
            var current = File.Exists(target) ? ComponentUpdateService.Sha256(target) : null;
            if (current == entry.OriginalSha256) continue;
            if (current is not null && current != entry.InstalledSha256) continue; // cambio ajeno: no se pisa
            if (entry.OriginalSha256 is null) { MakeWritable(target); File.Delete(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Write(entry.BackupFile!, target, entry.OriginalSha256);
        }
    }

    private static void Write(string source, string target, string expectedSha)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var staging = target + ".ilion-new";
        MakeWritable(staging);
        File.Copy(source, staging, overwrite: true);
        MakeWritable(staging); // una copia de un archivo de solo lectura también lo sería
        if (ComponentUpdateService.Sha256(staging) != expectedSha) { File.Delete(staging); throw new IOException("Copia intermedia corrupta."); }
        MakeWritable(target);  // archivos de solo lectura (copiados de .rar/.7z antiguos): se pueden sustituir igual, con su respaldo
        try { File.Move(staging, target, overwrite: true); }
        catch { File.Delete(staging); throw; }
        if (ComponentUpdateService.Sha256(target) != expectedSha) throw new IOException("Verificación posterior fallida.");
    }

    /// <summary>Quita el atributo de solo lectura (si lo tiene) para poder sustituir o retirar el archivo.</summary>
    private static void MakeWritable(string path)
    {
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private void Save(OperationManifest op)
    {
        lock (history) history.Remove(Path.GetFullPath(GameFolder(op.GameId)));
        var folder = Path.Combine(GameFolder(op.GameId), op.Id);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "operacion.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(op, Json));
        File.Move(file + ".tmp", file, overwrite: true);
    }

    private OperationManifest? Load(string gameId, string id)
    {
        var file = Path.Combine(GameFolder(gameId), id, "operacion.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<OperationManifest>(File.ReadAllText(file)) : null;
    }
}
