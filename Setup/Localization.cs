using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Ilion.Setup.App;

/// <summary>Idiomas de ILION SETUP (el mismo traductor que ILION CENTRAL): español (original y predeterminado), portugués, ruso e inglés. El texto se escribe en español en el código y
/// se traduce al mostrarse: cada elemento recuerda su original, así que cambiar de idioma (o volver al español) es exacto.
/// Diccionario en Assets/idiomas.json: «texto en español» → [pt, ru, en]. Frases compuestas: se traducen por partes (« · », números, prefijos).</summary>
internal static class Lang
{
    public static readonly string[] Codes = ["ES", "PT", "RU", "EN"];
    public static int Current { get; private set; }
    private static Dictionary<string, string[]> map = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<object, Holder> texts = new(), tips = new(), tags = new();
    private sealed class Holder { public string Original = ""; public string Shown = ""; }
    private static string settingsFile = "";

    public static void Init(string dataDirectory)
    {
        settingsFile = Path.Combine(dataDirectory, "Ajustes", "idioma.txt");
        try
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/idiomas.json"))!.Stream;
            map = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream) ?? [];
            BuildTemplates();
        }
        catch (Exception e) when (e is IOException or JsonException or NullReferenceException) { map = []; }
        try { Current = File.Exists(settingsFile) ? Math.Max(0, Array.IndexOf(Codes, File.ReadAllText(settingsFile).Trim())) : 0; } catch (IOException) { Current = 0; }
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) => { if (Current != 0) Apply((DependencyObject)s); }), true); // WPF solo lanza Loaded en elementos con manejador propio (ventanas, controles): se traduce su subárbol
        // Textos que el código cambia después de cargarse (barra de estado, avisos): repaso ligero de lo que se ve (las pestañas ocultas no se recorren)
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) => { if (Current != 0) foreach (Window w in Application.Current.Windows) if (w.IsVisible && w.WindowState != WindowState.Minimized) Apply(w, visibleOnly: true); };
        timer.Start();
    }

    public static void Set(int index)
    {
        Current = Math.Clamp(index, 0, Codes.Length - 1);
        lock (memo) memo.Clear();
        try { Directory.CreateDirectory(Path.GetDirectoryName(settingsFile)!); File.WriteAllText(settingsFile, Codes[Current]); } catch (IOException) { }
        foreach (Window w in Application.Current.Windows) Apply(w, force: true);
        Changed?.Invoke();
    }

    /// <summary>Añade traducciones que llegan con el catálogo firmado (mods nuevos). Las del propio Setup no se pisan.</summary>
    public static void Merge(IReadOnlyDictionary<string, string[]>? extra)
    {
        if (extra is null || extra.Count == 0) return;
        var added = false;
        foreach (var (es, values) in extra)
            if (values.Length == 3 && !map.ContainsKey(es)) { map[es] = values; added = true; }
        if (!added) return;
        BuildTemplates();
        lock (memo) memo.Clear();
    }

    /// <summary>Plantillas: «Instalado en {0}.» → los datos ({0}, {1}…) se conservan y se traduce el resto.</summary>
    private static void BuildTemplates() =>
        templates = [.. map.Where(e => e.Key.Contains("{0}")).Select(e => (new Regex("^" + Regex.Replace(Regex.Escape(e.Key), @"\\\{(\d+)}", "(?<p$1>.+?)") + "$", RegexOptions.Singleline), e.Value))];

    /// <summary>Al cambiar de idioma: las vistas que generan textos (opciones de los desplegables) se vuelven a dibujar.</summary>
    public static event Action? Changed;

    /// <summary>Traducción de un texto en español (o el mismo si no hay).</summary>
    private static readonly Dictionary<string, string> memo = new(StringComparer.Ordinal);

    public static string T(string es)
    {
        if (Current == 0 || string.IsNullOrWhiteSpace(es)) return es;
        lock (memo)
        {
            if (memo.TryGetValue(es, out var known)) return known;
            if (memo.Count > 20000) memo.Clear();
        }
        var shown = Translate(es);
        lock (memo) memo[es] = shown;
        return shown;
    }

    private static string Translate(string es)
    {
        var trimmed = es.Trim();
        if (trimmed.Length != es.Length) { var start = es.IndexOf(trimmed, StringComparison.Ordinal); return es[..start] + T(trimmed) + es[(start + trimmed.Length)..]; }
        if (Word(es) is { } whole) return whole;
        // Por partes: « · », saltos de línea y «  ·  »
        foreach (var sep in new[] { "\n", "  ·  ", " · ", " — " })
            if (es.Contains(sep)) return string.Join(sep, es.Split(sep).Select(T));
        return Pattern(es) ?? es;
    }

    private static string? Word(string es) =>
        map.TryGetValue(es, out var t) && t.Length >= Current && !string.IsNullOrEmpty(t[Current - 1]) ? t[Current - 1]
        : map.TryGetValue(es.Trim(), out t) && t.Length >= Current && !string.IsNullOrEmpty(t[Current - 1]) ? es.Replace(es.Trim(), t[Current - 1]) : null;

    private static readonly Regex Number = new(@"^(\d+(?:[.,]\d+)?)( ?)(.+)$");
    private static readonly Regex Prefix = new(@"^([^:]{2,40}): (.+)$");
    private static (Regex Key, string[] Values)[] templates = [];

    private static string? Template(string es)
    {
        foreach (var (key, values) in templates)
        {
            if (values.Length < Current || string.IsNullOrEmpty(values[Current - 1])) continue;
            var m = key.Match(es);
            if (!m.Success) continue;
            // Cada dato se traduce también si es una palabra conocida («1 mod seleccionado», «Cancelada»…)
            return Regex.Replace(values[Current - 1], @"\{(\d+)}", x => m.Groups["p" + x.Groups[1].Value] is { Success: true } g ? (Word(g.Value) ?? g.Value) : x.Value);
        }
        return null;
    }

    private static string? Pattern(string es)
    {
        if (Template(es) is { } fromTemplate) return fromTemplate;
        var n = Number.Match(es);
        if (n.Success && Word(n.Groups[3].Value) is { } rest) return n.Groups[1].Value + n.Groups[2].Value + rest;
        var c = Regex.Match(es, @"^(\S+) ([A-Z])$"); // «Copia A»
        if (c.Success && Word(c.Groups[1].Value) is { } copy) return copy + " " + c.Groups[2].Value;
        var size = Regex.Match(es, @"^(.+?) (\d[\d.,]* ?[KMG]B)$"); // «Respaldos 4.3 GB»
        if (size.Success && Word(size.Groups[1].Value) is { } label) return label + " " + size.Groups[2].Value;
        var p = Prefix.Match(es);
        if (p.Success && Word(p.Groups[1].Value) is { } head) return head + ": " + (Word(p.Groups[2].Value) ?? p.Groups[2].Value);
        return null;
    }

    private static string Localize(ConditionalWeakTable<object, Holder> table, object owner, string current)
    {
        var h = table.GetOrCreateValue(owner);
        if (current != h.Shown) h.Original = current; // primera vez, o el código cambió el texto: nuevo original
        h.Shown = Current == 0 ? h.Original : T(h.Original);
        return h.Shown;
    }

    public static void Apply(DependencyObject root, bool force = false, bool visibleOnly = false)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (visibleOnly && d is UIElement { Visibility: not Visibility.Visible }) continue;
            ApplyOne(d);
            if (d is Visual or System.Windows.Media.Media3D.Visual3D)
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    /// <summary>Traduce un elemento (sin recorrer sus hijos).</summary>
    private static void ApplyOne(DependencyObject d)
    {
        {
            switch (d)
            {
                case TextBlock tb when tb.Inlines.Count > 1 || tb.Inlines.FirstInline is Hyperlink:
                    foreach (var run in Runs(tb.Inlines).ToList()) { var v = Localize(texts, run, run.Text); if (run.Text != v) run.Text = v; }
                    break;
                case TextBlock tb when tb.Text.Length > 0 && tb.TemplatedParent is not TextBox && !System.Windows.Data.BindingOperations.IsDataBound(tb, TextBlock.TextProperty):
                    { var v = Localize(texts, tb, tb.Text); if (tb.Text != v) tb.Text = v; break; }
                case ContentControl cc when cc.Content is string text && !System.Windows.Data.BindingOperations.IsDataBound(cc, ContentControl.ContentProperty):
                    { var v = Localize(texts, cc, text); if (text != v) cc.Content = v; break; }
            }
            if (d is FrameworkElement fe)
            {
                if (fe.ToolTip is string tip) { var v = Localize(tips, fe, tip); if (tip != v) fe.ToolTip = v; }
                if (fe is TextBox { Tag: string placeholder } box) { var v = Localize(tags, box, placeholder); if (placeholder != v) box.Tag = v; }
            }
        }
    }

    private static IEnumerable<Run> Runs(InlineCollection inlines)
    {
        foreach (var inline in inlines)
            if (inline is Run r) yield return r;
            else if (inline is Span s) foreach (var x in Runs(s.Inlines)) yield return x;
    }

}
