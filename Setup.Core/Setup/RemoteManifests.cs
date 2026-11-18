// ILION Setup — manifiestos firmados publicados en GitHub (rama «setup-manifiestos» de ENE4S/Optiscaler-ILION).
// Así una versión nueva de ILION o de un mod aprobada llega a los usuarios sin recompilar Setup. Solo se guarda lo que lleva
// una firma válida de ILION; si no hay red o la firma no cuadra, Setup sigue con lo que ya tenía. Sin la API de GitHub.
using System.Net;
using System.Text.RegularExpressions;

namespace Ilion.Setup;

public static class RemoteManifests
{
    public const string BaseUrl = "https://raw.githubusercontent.com/ENE4S/Optiscaler-ILION/setup-manifiestos/";
    public static readonly string[] Names = ["base-congelada.json", "catalogo.json"];

    /// <summary>Descarga los manifiestos y sus firmas; guarda en «carpeta» solo los que verifican. Devuelve qué pasó con cada uno.</summary>
    public static async Task<string[]> RefreshAsync(HttpClient http, string folder, CancellationToken token, string baseUrl = BaseUrl)
    {
        Directory.CreateDirectory(folder);
        var notes = new List<string>();
        foreach (var name in Names)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                var json = await http.GetByteArrayAsync(baseUrl + name, cts.Token);
                var signature = (await http.GetStringAsync(baseUrl + name + ".firma", cts.Token)).Trim();
                if (json.Length > 2_000_000 || !Manifests.Verify(json, signature)) { notes.Add($"{name}: la firma no es válida; se ignora."); continue; }
                var target = Path.Combine(folder, name);
                await File.WriteAllBytesAsync(target + ".nuevo", json, token);
                await File.WriteAllTextAsync(target + ".firma.nuevo", signature, token);
                File.Move(target + ".nuevo", target, true);
                File.Move(target + ".firma.nuevo", target + ".firma", true);
                notes.Add($"{name}: firmado y al día.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (token.IsCancellationRequested) throw;
                notes.Add($"{name}: sin conexión con GitHub ({(e as HttpRequestException)?.StatusCode?.ToString() ?? e.GetType().Name}); se usa el que ya había.");
            }
        }
        return [.. notes];
    }

    /// <summary>Última etiqueta publicada de un repositorio, leída de la redirección de github.com/…/releases/latest (sin la API ni su límite).</summary>
    public static async Task<string?> LatestTagAsync(HttpClient noRedirect, string repository, CancellationToken token)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{repository}/releases/latest");
            using var response = await noRedirect.SendAsync(request, cts.Token);
            if (response.StatusCode is not (HttpStatusCode.Found or HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect)) return null;
            var m = Regex.Match(response.Headers.Location?.ToString() ?? "", @"/releases/tag/([\w.\-]{1,40})$");
            return m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return null; }
    }
}
