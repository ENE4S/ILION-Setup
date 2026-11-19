// ILION Setup 0.4 — recetas descritas por datos en el catálogo firmado (formato 2). Permiten añadir o adaptar mods sin publicar
// otro Setup, con los mismos pasos que las recetas integradas: descarga directa de la publicación aprobada, huella SHA-256,
// extracción en caché, comprobación de que el paquete trae todo lo que la receta espera y escritura restaurable.
// Límites deliberados: solo copian archivos (nunca ejecutan nada), solo dentro de la carpeta del juego, sin scripts ni ejecutables,
// y solo si el catálogo lleva la firma de ILION. Lo que necesite código (compilar como HelixSR) sigue siendo una receta integrada.
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ilion.Client;

namespace Ilion.Setup;

/// <summary>Regla de copia: «de» es una ruta del paquete (o carpeta terminada en «/**», o con «*/» delante si la primera carpeta
/// lleva la versión en el nombre) y «a» la ruta junto al ejecutable (admite {tag}).</summary>
public sealed record RecipeFileData([property: JsonPropertyName("de")] string From, [property: JsonPropertyName("a")] string To);

public sealed record RecipeConflict([property: JsonPropertyName("nombre")] string Name, [property: JsonPropertyName("marcador")] string Marker);

public sealed record RecipeData(
    [property: JsonPropertyName("repositorio")] string Repository,
    [property: JsonPropertyName("archivos")] RecipeFileData[] Files,
    [property: JsonPropertyName("requeridos")] string[] Required,
    [property: JsonPropertyName("marcador")] string Marker,
    [property: JsonPropertyName("destino")] string? Describe = null,
    [property: JsonPropertyName("soloDx12")] bool Dx12Only = false,
    [property: JsonPropertyName("conservar")] string[]? KeepIfPresent = null,
    [property: JsonPropertyName("incompatibles")] RecipeConflict[]? Conflicts = null,
    [property: JsonPropertyName("necesitaIlion")] bool NeedsIlion = true,
    [property: JsonPropertyName("ilionMinimo")] string? MinIlion = null,
    [property: JsonPropertyName("ranura")] string? Slot = null,
    [property: JsonPropertyName("nota")] string? Note = null,
    [property: JsonPropertyName("archivosOficiales")] string[]? Dependencies = null,
    [property: JsonPropertyName("nombreCorto")] string? Short = null,
    [property: JsonPropertyName("arquitecturas")] string[]? Archs = null,
    [property: JsonPropertyName("proxies")] string[]? ProxyNames = null,
    [property: JsonPropertyName("proxyOrigen")] Dictionary<string, string>? ProxySource = null,
    [property: JsonPropertyName("tarjeta")] string? CatalogCard = null);

public static class Recipes
{
    /// <summary>Archivos oficiales que una receta por datos puede pedir: los que Setup ya sabe conseguir o comprobar.</summary>
    public static readonly Dictionary<string, Func<ModDependency>> OfficialFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xefg-xmx"] = () => ModInstaller.XmxXeFg,
        ["xefg-1.3.4-xmx"] = () => ModInstaller.XmxXeFg, // nombre anterior: también acepta la 1.3.1 oficial
        ["intel-xefg"] = () => ModInstaller.IntelRuntime("libxess_fg.dll", "XeFG de Intel", "1.3.1"),
        ["intel-xell"] = () => ModInstaller.IntelRuntime("libxell.dll", "XeLL de Intel", null),
        ["nvidia-dlssnr"] = () => ModInstaller.DlssNr,
    };

    private static readonly Regex SafeTag = new(@"^[\w.\-]{1,40}$");
    private static readonly Regex SafeAsset = new(@"^[\w.\-]{1,120}\.(zip|7z|rar)$", RegexOptions.IgnoreCase);
    private static readonly Regex Sha = new("^[0-9a-fA-F]{64}$");
    private static readonly Regex Repo = new(@"^[\w.\-]{1,60}/[\w.\-]{1,100}$");
    private static readonly string[] Blocked = [".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".scr", ".com", ".lnk", ".reg"];
    /// <summary>Lo que es de ILION u OptiScaler: una receta de un mod nunca lo sustituye.</summary>
    private static readonly string[] Protected = ["OptiScaler.ini", "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll"];

    /// <summary>¿Es una ruta segura junto al ejecutable? Relativa, sin «..», sin unidad, sin caracteres raros y sin extensiones ejecutables.</summary>
    public static string? CheckPath(string path, string what)
    {
        if (string.IsNullOrWhiteSpace(path)) return $"{what}: ruta vacía.";
        var p = path.Replace('/', '\\');
        if (Path.IsPathRooted(p) || p.Contains(':') || p.StartsWith('\\')) return $"{what}: «{path}» no es relativa a la carpeta del juego.";
        if (p.Split('\\').Any(s => s is ".." or ".")) return $"{what}: «{path}» sale de la carpeta del juego.";
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || p.Contains('?') || p.Contains('"') || p.Contains('<') || p.Contains('>') || p.Contains('|'))
            return $"{what}: «{path}» tiene caracteres no válidos.";
        if (Blocked.Any(b => p.EndsWith(b, StringComparison.OrdinalIgnoreCase))) return $"{what}: «{path}» es un ejecutable o un script; las recetas por datos no los instalan.";
        return null;
    }

    /// <summary>Comprueba una receta por datos antes de usarla. Null = válida.</summary>
    public static string? Validate(CatalogEntry e, RecipeData d)
    {
        if (!Repo.IsMatch(d.Repository)) return "repositorio no válido";
        if (e.Version is not { } v || !SafeTag.IsMatch(v)) return "versión aprobada no válida";
        if (e.Sha256 is not { } s || !Sha.IsMatch(s)) return "falta la huella SHA-256 aprobada";
        if (e.Asset is not { } a || !SafeAsset.IsMatch(a)) return "falta el nombre exacto del paquete aprobado (.zip, .7z o .rar)";
        if (d.Files.Length == 0) return "no dice qué archivos copiar";
        if (d.Required.Length == 0) return "no dice qué archivos son imprescindibles";
        foreach (var f in d.Files)
        {
            if (string.IsNullOrWhiteSpace(f.From) || f.From.Contains("..")) return $"origen no válido «{f.From}»";
            if (CheckPath(f.To.Replace("{tag}", "x"), "Destino") is { } bad) return bad;
            if (Protected.Contains(f.To, StringComparer.OrdinalIgnoreCase)) return $"«{f.To}» es de ILION u OptiScaler: un mod no lo sustituye";
        }
        foreach (var r in d.Required.Append(d.Marker).Concat(d.KeepIfPresent ?? []))
            if (CheckPath(r.Replace("{tag}", "x").Replace("{any}", "x"), "Archivo") is { } bad) return bad;
        if (!d.Required.Any(r => r.Equals(d.Marker, StringComparison.OrdinalIgnoreCase)) && !d.Marker.Contains("{any}"))
            return "el marcador debe ser uno de los archivos imprescindibles";
        foreach (var c in d.Conflicts ?? []) if (CheckPath(c.Marker, "Incompatible") is { } bad) return bad;
        foreach (var dep in d.Dependencies ?? []) if (!OfficialFiles.ContainsKey(dep)) return $"archivo oficial desconocido «{dep}»";
        if (d.ProxyNames is { Length: > 0 } proxies)
        {
            if (d.ProxySource is null || d.ProxySource.Count == 0) return "un mod proxy necesita «proxyOrigen»";
            foreach (var n in proxies) if (!Regex.IsMatch(n, @"^[\w.\-]{1,40}\.dll$", RegexOptions.IgnoreCase)) return $"nombre de carga no válido «{n}»";
        }
        return null;
    }

    /// <summary>Receta lista para instalar a partir de los datos del catálogo (ya validados).</summary>
    public static InstallableMod Build(CatalogEntry e, RecipeData d) =>
        new(d.Repository, e.Name, "^" + Regex.Escape(e.Asset!) + "$", d.Describe ?? d.Marker, d.Dx12Only,
            [.. d.Files.Select(f => new RecipeFile(f.From, f.To.Replace('/', '\\')))])
        {
            Required = [.. d.Required.Select(r => r.Replace('/', '\\'))],
            Marker = d.Marker.Replace('/', '\\'),
            KeepIfPresent = [.. (d.KeepIfPresent ?? []).Select(k => k.Replace('/', '\\'))],
            Conflicts = [.. (d.Conflicts ?? []).Select(c => (c.Name, c.Marker.Replace('/', '\\')))],
            NeedsIlion = d.NeedsIlion, MinIlion = d.MinIlion, Slot = d.Slot, Note = d.Note, Short = d.Short, Archs = d.Archs ?? [],
            Dependencies = [.. (d.Dependencies ?? []).Select(id => OfficialFiles[id]())],
            ProxyNames = d.ProxyNames ?? [],
            ProxySource = d.ProxySource is { Count: > 0 } map ? name => (map.TryGetValue(name, out var s) ? s : map.GetValueOrDefault("*") ?? name).Replace("{name}", name) : null,
            Catalog = d.CatalogCard, FromCatalogData = true,
            Tag = e.Version, ApprovedSha256 = e.Sha256, ApprovedAsset = e.Asset,
        };
}
