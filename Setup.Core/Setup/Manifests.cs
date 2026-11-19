// ILION Setup — manifiestos versionados: la base congelada de OptiScaler y el catálogo de mods aprobados.
// Son datos, nunca código: solo pueden elegir entre capacidades que Setup ya trae (recetas, compilación de HelixSR…).
// Los que vienen dentro del ejecutable son la referencia. Uno externo (actualización de recetas sin recompilar) solo se acepta
// si lleva una firma ECDSA P-256 válida de la clave de ILION y su formato es uno que esta versión de Setup entiende.
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ilion.Setup;

public sealed record BaseFile(
    [property: JsonPropertyName("destino")] string Destination,
    [property: JsonPropertyName("origen")] string Origin,
    [property: JsonPropertyName("entrada")] string? Entry,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("tamano")] long Size,
    [property: JsonPropertyName("firmante")] string? Signer,
    [property: JsonPropertyName("opcional")] bool Optional = false,
    [property: JsonPropertyName("grupo")] string? GroupName = null);

public sealed record BasePackage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("tipo")] string Kind,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("nota")] string? Note);

public sealed record EngineInfo(
    [property: JsonPropertyName("nombre")] string Name,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("destino")] string Destination,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("codigo")] string? Code,
    [property: JsonPropertyName("base")] string? Base,
    [property: JsonPropertyName("nota")] string? Note);

public sealed record BaseManifest(
    [property: JsonPropertyName("formato")] int Format,
    [property: JsonPropertyName("nombre")] string Name,
    [property: JsonPropertyName("fecha")] string Date,
    [property: JsonPropertyName("motor")] EngineInfo Engine,
    [property: JsonPropertyName("paquetes")] BasePackage[] Packages,
    [property: JsonPropertyName("archivos")] BaseFile[] Files,
    [property: JsonPropertyName("justificacion")] string[] Evidence);

/// <summary>Un mod del catálogo de Setup: qué receta usa, la versión aprobada para ILION y lo que se le enseña al usuario.</summary>
public sealed record CatalogEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("nombre")] string Name,
    [property: JsonPropertyName("autor")] string Author,
    [property: JsonPropertyName("categoria")] string Category,
    [property: JsonPropertyName("estado")] string State,
    [property: JsonPropertyName("receta")] string? Recipe,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("paquete")] string? Asset,
    [property: JsonPropertyName("descripcion")] string Description,
    [property: JsonPropertyName("para")] string[] Archs,
    [property: JsonPropertyName("api")] string Apis,
    [property: JsonPropertyName("dependencias")] string? Dependencies,
    [property: JsonPropertyName("conflictos")] string? Conflicts,
    [property: JsonPropertyName("fuente")] string Source,
    [property: JsonPropertyName("recomendado")] string[]? Recommended = null,
    // Formato 2 (Setup 0.4+): receta descrita por datos (mods nuevos sin recompilar) y versión mínima de Setup que la entiende
    [property: JsonPropertyName("definicion")] RecipeData? Definition = null,
    [property: JsonPropertyName("setupMinimo")] string? MinSetup = null);

public sealed record Catalog(
    [property: JsonPropertyName("formato")] int Format,
    [property: JsonPropertyName("fecha")] string Date,
    [property: JsonPropertyName("mods")] CatalogEntry[] Mods,
    // Formato 2: traducciones de los textos del catálogo («texto en español» → [portugués, ruso, inglés]), para que un mod nuevo
    // llegue traducido sin publicar otro Setup
    [property: JsonPropertyName("textos")] Dictionary<string, string[]>? Texts = null);

public static class Manifests
{
    /// <summary>Formato más alto que entiende esta versión de Setup (1 = catálogo de la 0.3.x; 2 = recetas por datos, Setup 0.4+).</summary>
    public const int SupportedFormat = 2;

    /// <summary>Clave pública de ILION para los manifiestos externos (SubjectPublicKeyInfo, Base64). La privada no viaja con Setup.</summary>
    public static string PublicKey { get; set; } = "";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static T Parse<T>(string json) where T : class =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Manifiesto vacío.");

    /// <summary>¿La firma (Base64, ECDSA P-256 + SHA-256 sobre los bytes exactos del JSON) es de la clave de ILION?</summary>
    public static bool Verify(byte[] json, string signature)
    {
        if (string.IsNullOrWhiteSpace(PublicKey)) return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            return key.VerifyData(json, Convert.FromBase64String(signature.Trim()), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException) { return false; }
    }

    /// <summary>Firma un manifiesto (herramienta de quien publica; la clave privada nunca va en Setup).</summary>
    public static string Sign(byte[] json, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(key.SignData(json, HashAlgorithmName.SHA256));
    }

    /// <summary>Lee un manifiesto externo firmado; null (y el motivo) si falta, no tiene una firma válida, no se lee o su formato es más nuevo.</summary>
    public static T? External<T>(string? file, Func<T, int> format, out string why) where T : class
    {
        why = "";
        if (file is null || !File.Exists(file) || !File.Exists(file + ".firma")) { why = "no descargado"; return null; }
        var bytes = File.ReadAllBytes(file);
        if (!Verify(bytes, File.ReadAllText(file + ".firma"))) { why = "sin una firma válida de ILION"; return null; }
        T external;
        try { external = Parse<T>(System.Text.Encoding.UTF8.GetString(bytes)); }
        catch (JsonException) { why = "no se puede leer"; return null; }
        if (format(external) > SupportedFormat) { why = "necesita una versión más nueva de Setup"; return null; }
        return external;
    }

    /// <summary>Catálogo de mods: el más nuevo (por fecha) entre el integrado y los externos firmados. Setup 0.4 lee el catálogo 2
    /// (catalogo-2.json, con recetas por datos) y también el de la 0.3.x (catalogo.json), que se sigue publicando para quien no actualiza.
    /// A igual fecha gana el de formato más alto; entre iguales, el externo.</summary>
    public static (Catalog Catalog, string Origin) ChooseCatalog(string embedded, params (string File, string Label)[] externals)
    {
        var best = (Catalog: Parse<Catalog>(embedded), Origin: "integrado");
        foreach (var (file, label) in externals)
        {
            if (External<Catalog>(file, c => c.Format, out _) is not { } c) continue;
            var newer = string.CompareOrdinal(c.Date, best.Catalog.Date);
            if (newer > 0 || newer == 0 && c.Format >= best.Catalog.Format) best = (c, $"{label} firmado {c.Date}");
        }
        return best;
    }

    /// <summary>Elige el manifiesto: el de dentro de Setup, salvo que haya uno externo firmado, de un formato admitido y más nuevo.</summary>
    public static (T Manifest, string Origin) Choose<T>(string embedded, string? externalFile, Func<T, string> date, Func<T, int> format) where T : class
    {
        var builtIn = Parse<T>(embedded);
        if (externalFile is null || !File.Exists(externalFile) || !File.Exists(externalFile + ".firma")) return (builtIn, "integrado");
        var bytes = File.ReadAllBytes(externalFile);
        if (!Verify(bytes, File.ReadAllText(externalFile + ".firma"))) return (builtIn, "integrado (el externo no tiene una firma válida de ILION)");
        T external;
        try { external = Parse<T>(System.Text.Encoding.UTF8.GetString(bytes)); }
        catch (JsonException) { return (builtIn, "integrado (el externo no se puede leer)"); }
        if (format(external) > SupportedFormat) return (builtIn, "integrado (el externo necesita una versión más nueva de Setup)");
        return string.CompareOrdinal(date(external), date(builtIn)) > 0 ? (external, "externo firmado " + date(external)) : (builtIn, "integrado");
    }
}
