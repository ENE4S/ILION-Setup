using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ilion.Client;
using Microsoft.Win32;
using Level = Ilion.Setup.Level;

namespace Ilion.Setup.App;

/// <summary>ILION SETUP: instalación nueva, actualización del motor y verificación/reparación, con la estética de SPUTNIK.
/// Funciona solo: no necesita ILION CENTRAL. Sus datos (caché, respaldos, registro) van en %LOCALAPPDATA%\ILION.Setup.</summary>
public partial class MainWindow : Window
{
    private readonly string data;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private SetupEngine engine;
    private string baseOrigin = "integrado", catalogOrigin = "integrado";
    private FrameworkElement emblem = null!, statusSpin = null!;
    private int working;                                     // cargas o instalaciones en curso (el Sputnik gira mientras > 0)
    private readonly Dictionary<string, string> newerTags = new(); // id del catálogo → versión más nueva publicada por su autor (sin aprobar)
    private string? lastGameId;
    private bool showMoreInfo;
    private readonly ArchTable table;
    private readonly ManualSource manual;
    private readonly IlionPackageStore packages;
    private readonly string[] args = Environment.GetCommandLineArgs();
    private GpuDetection? gpu;
    private LibraryGame[] games = [];
    private readonly Dictionary<string, (string? Exe, string ExeDir, string? Api, OptiScalerInstallation Opti)> gameInfo = new();
    private List<string> engineCandidates = [];

    // Instalación nueva
    private LibraryGame? game; private string? exeDir; private EngineSource? engineSource;
    private bool withStreamline = true, withAgility, allowOtherArch, withVariants = true; private string proxy = "dxgi.dll"; private string? dlss4Folder;
    private const string Official = "github:";
    private readonly List<(CheckBox Box, CatalogItem Item)> modChecks = [];
    // Operación en curso
    private bool busyFlag;
    private bool busy { get => busyFlag; set { if (busyFlag == value) return; busyFlag = value; Work(value); } }
    private volatile bool writing; private InstallProgress? progressWindow; private CancellationTokenSource? cts;

    public MainWindow()
    {
        InitializeComponent();
        var d = Array.IndexOf(args, "--datos");
        data = Path.GetFullPath(d > 0 && d + 1 < args.Length ? args[d + 1] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ILION.Setup"));
        Directory.CreateDirectory(data);
        App.LogFile = Path.Combine(data, "ilion-setup.log");
        // Idioma: español por defecto; portugués, ruso e inglés (los mismos que ILION y ILION Central)
        Lang.Init(data);
        LanguagePick.ItemsSource = Lang.Codes;
        LanguagePick.SelectedIndex = Lang.Current;
        Lang.Changed += () => { if (IsLoaded && !busy) ShowMode(); };
        SetupEngine.SetupVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "0.4.0";
        Title = "ILION SETUP " + SetupEngine.SetupVersion;
        SetupVersionLabel.Text = "v" + SetupEngine.SetupVersion;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ILION-Setup/" + SetupEngine.SetupVersion);
        Manifests.PublicKey = Resource("clave-publica.txt").Trim();
        engine = LoadManifests();
        using (var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/arquitecturas.json"))!.Stream) table = Gpu.Load(s);
        manual = new ManualSource(Path.Combine(data, "Ajustes", "juegos-manuales.json"));
        packages = new IlionPackageStore(data);
        ShowBaseInfo();
        emblem = Sputnik.Build(56);
        Emblem.Content = emblem;
        statusSpin = Sputnik.Build(26);
        StatusSpin.Content = statusSpin;
        try { lastGameId = File.ReadAllText(Path.Combine(data, "Ajustes", "ultimo-juego.txt")).Trim(); } catch (IOException) { }
        var scale = Array.IndexOf(args, "--escala");
        if (scale > 0 && scale + 1 < args.Length && double.TryParse(args[scale + 1], System.Globalization.CultureInfo.InvariantCulture, out var pct))
        { Ui.Scale = pct / 100.0; Root.LayoutTransform = new ScaleTransform(Ui.Scale, Ui.Scale); Width *= Ui.Scale; Height *= Ui.Scale; }
        Closing += GuardClose;
        Status.MouseLeftButtonUp += (_, _) => { if (progressWindow is { IsRunning: true } p) p.Reveal(); };
        Loaded += async (_, _) => await StartAsync();
    }

    /// <summary>Manifiesto de la base y catálogo: el integrado o, si es más nuevo y lleva la firma de ILION, el descargado de GitHub.</summary>
    private SetupEngine LoadManifests()
    {
        var folder = Path.Combine(data, "Manifiestos");
        var (b, bo) = Manifests.Choose<BaseManifest>(Resource("base-congelada.json"), Path.Combine(folder, "base-congelada.json"), m => m.Date, m => m.Format);
        var (c, co) = Manifests.ChooseCatalog(Resource("catalogo-2.json"), (Path.Combine(folder, "catalogo-2.json"), "externo"), (Path.Combine(folder, "catalogo.json"), "externo"));
        baseOrigin = bo.StartsWith("externo") ? "GitHub, firmado" : bo; catalogOrigin = co.StartsWith("externo") ? "GitHub, firmado" : co;
        Lang.Merge(c.Texts);
        return new SetupEngine(data, http, b, c);
    }

    /// <summary>Barra inferior: base, versión de ILION (en amarillo) y fecha de los manifiestos, corta para que no se recorte.</summary>
    private void ShowBaseInfo()
    {
        static string Day(string date) => date.Length >= 10 ? $"{date[8..10]}/{date[5..7]}" : date;
        BaseInfo.Inlines.Clear();
        BaseInfo.Inlines.Add(new System.Windows.Documents.Run("Base f6f75dd  ·  "));
        BaseInfo.Inlines.Add(new System.Windows.Documents.Run($"ILION {engine.Base.Engine.Version}") { Foreground = Ui.B("B.Yellow"), FontFamily = (FontFamily)FindResource("F.Strong") });
        // Cada dato en su propio texto (así se traduce cada uno)
        foreach (var part in new[] { $"manifiesto {Day(engine.Base.Date)}", $"catálogo {Day(engine.Catalog.Date)}" })
        {
            BaseInfo.Inlines.Add(new System.Windows.Documents.Run("  ·  "));
            BaseInfo.Inlines.Add(new System.Windows.Documents.Run(part));
        }
        BaseInfo.ToolTip = $"Manifiesto de la base {engine.Base.Date} ({baseOrigin}) · catálogo {engine.Catalog.Date} ({catalogOrigin})";
        if (newerSetup is not null) BaseInfo.Inlines.Add(NewerSetupLink());
    }

    /// <summary>Descarga los manifiestos firmados de GitHub y, si traen algo más nuevo, los aplica. Devuelve si cambió la versión aprobada de ILION.</summary>
    private async Task<(bool EngineChanged, string[] Notes)> RefreshManifests()
    {
        var before = engine.Base.Engine.Sha256;
        string[] notes;
        try { notes = await RemoteManifests.RefreshAsync(http, Path.Combine(data, "Manifiestos"), CancellationToken.None); }
        catch (Exception e) when (e is not OutOfMemoryException) { notes = [e.Message]; }
        var next = LoadManifests();
        if (next.Base.Date != engine.Base.Date || next.Catalog.Date != engine.Catalog.Date)
        {
            engine = next;
            if (engineSource is null or { Remote: true }) engineSource = engine.RemoteEngine;
        }
        ShowBaseInfo();
        Log("Manifiestos de GitHub: " + string.Join(" | ", notes));
        return (engine.Base.Engine.Sha256 != before, notes);
    }

    /// <summary>Versión más nueva publicada por el autor de cada mod (redirección de releases/latest, sin la API): solo se avisa, nunca se instala sola.</summary>
    private async Task CheckNewerMods()
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        noRedirect.DefaultRequestHeaders.UserAgent.ParseAdd("ILION-Setup/" + SetupEngine.SetupVersion);
        await CheckNewerSetup(noRedirect);
        foreach (var repo in engine.Catalog.Mods.Where(m => m.Version is not null && SetupEngine.RecipeFor(m) is not null).GroupBy(m => SetupEngine.RecipeFor(m)!.Repository))
        {
            if (repo.Key.StartsWith("ENE4S/", StringComparison.OrdinalIgnoreCase)) continue;
            var latest = await RemoteManifests.LatestTagAsync(noRedirect, repo.Key, CancellationToken.None);
            if (latest is null || repo.Any(m => m.Version == latest)) continue;
            var newest = repo.OrderByDescending(m => m.Version, Comparer<string?>.Create((a, b) => VersionUtil.Compare(a, b))).First();
            newerTags[newest.Id] = latest;
        }
    }

    /// <summary>Una carga o instalación empieza (true) o termina (false): el Sputnik gira mientras haya alguna.</summary>
    private void Work(bool on)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Work(on)); return; }
        working = Math.Max(0, working + (on ? 1 : -1));
        var spin = working > 0;
        Sputnik.Spin(emblem, spin); Sputnik.Spin(statusSpin, spin);
        StatusSpin.Visibility = spin ? Visibility.Visible : Visibility.Collapsed;
        StatusLamp.Visibility = spin ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RememberGame(LibraryGame g)
    {
        lastGameId = SetupEngine.GameId(g);
        try { Directory.CreateDirectory(Path.Combine(data, "Ajustes")); File.WriteAllText(Path.Combine(data, "Ajustes", "ultimo-juego.txt"), lastGameId); } catch (IOException) { }
    }

    private LibraryGame? LastGame(IEnumerable<LibraryGame> among) => lastGameId is null ? null : among.FirstOrDefault(g => SetupEngine.GameId(g) == lastGameId);

    /// <summary>Pantalla sin juego elegido: el Sputnik tenue en el centro y qué hacer.</summary>
    private void Watermark(string text)
    {
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 70, 0, 0) };
        box.Children.Add(Sputnik.Watermark(200, 0.18));
        var t = Note(text, null, 14); t.HorizontalAlignment = HorizontalAlignment.Center; t.Margin = new Thickness(0, 18, 0, 0); t.Opacity = 0.8;
        box.Children.Add(t);
        Page.Children.Add(box);
    }

    private static string Resource(string name)
    {
        using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/" + name))!.Stream;
        return new StreamReader(s).ReadToEnd();
    }

    private async Task StartAsync()
    {
        Work(true);
        Say("Detectando la GPU, los juegos y los paquetes de ILION…", Level.Info);
        var manifests = RefreshManifests();
        gpu = await Task.Run(() => OperatingSystem.IsWindows() ? Gpu.Detect(table, Gpu.Installed()) : new GpuDetection("No detectada", null, []));
        GpuName.Text = gpu.Name;
        GpuArch.Text = gpu.Arch is { } a ? $"{Gpu.BrandName(a.Marca)} · {a.Nombre}" : "Arquitectura no reconocida";
        await ScanGames();
        engineCandidates = [.. packages.History(), .. await Task.Run(() => { try { return IlionPackageStore.Discover(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException) { return []; } })];
        engineCandidates = [.. engineCandidates.Distinct(StringComparer.OrdinalIgnoreCase)];
        engineSource = engine.RemoteEngine; // la publicación oficial de GitHub; un paquete o DLL local es opción avanzada
        await manifests;
        if (LastGame(games) is { } last) SelectGame(last);
        Work(false);
        _ = CheckNewerMods().ContinueWith(_ => Dispatcher.Invoke(() => { if (newerTags.Count > 0 && ModeNew.IsChecked == true && !busy) ShowMode(); }));
        Say($"{games.Length} juegos detectados · GPU {GpuArch.Text}.");
        ApplyLaunchRequest();
        ShowMode();
        try { await RunCentralOrders(); }
        catch (Exception e) when (e is not OutOfMemoryException) { LogError("Órdenes de ILION Central", e); Say("No se pudo completar lo que pidió ILION Central: " + ModBatch.Friendly(e), Level.Warn); }
        await CaptureIfRequested();
    }

    private async Task ScanGames()
    {
        var found = await Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows()) return Array.Empty<LibraryGame>();
            var scan = LibraryDiscovery.Scan([.. WindowsLibraryLocations.DefaultSources(), manual]);
            return scan.Games.Where(g => !System.Text.RegularExpressions.Regex.IsMatch(g.Name, @"Redistributable|Steam Linux Runtime|^Proton\b|SteamVR|Steamworks Common", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToArray();
        });
        games = found;
        await Task.Run(() => { foreach (var g in games) Info(g); });
    }

    /// <summary>Ejecutable real, su carpeta, la API y lo que haya de OptiScaler (cacheado por juego).</summary>
    private (string? Exe, string ExeDir, string? Api, OptiScalerInstallation Opti) Info(LibraryGame g)
    {
        lock (gameInfo)
            if (gameInfo.TryGetValue(SetupEngine.GameId(g), out var known)) return known;
        var evidence = ExecutableDiscovery.Inspect(g);
        var dir = evidence.Path is null ? g.Root : Path.GetDirectoryName(evidence.Path)!;
        var api = evidence.Apis.Select(x => x.Name).OrderByDescending(n => n == "DirectX 12").FirstOrDefault();
        OptiScalerInstallation opti;
        try { opti = OptiScalerDetection.Inspect(g.Root, evidence.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { opti = OptiScalerDetection.Inspect(dir, null); }
        var info = (evidence.Path, dir, api, opti);
        lock (gameInfo) gameInfo[SetupEngine.GameId(g)] = info;
        return info;
    }

    // ---------------------------------------------------------------- Ventana, estado y cierre
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Mode_Checked(object sender, RoutedEventArgs e) { if (IsLoaded) ShowMode(); }
    private void Language_Changed(object sender, SelectionChangedEventArgs e) { if (LanguagePick.SelectedIndex >= 0 && LanguagePick.SelectedIndex != Lang.Current) Lang.Set(LanguagePick.SelectedIndex); }

    private void Say(string text, Level level = Level.Ok)
    {
        Status.Text = text;
        if (Lang.Current != 0) Lang.Apply(Status);
        StatusLamp.Fill = Ui.B(level switch { Level.Error => "B.Error", Level.Warn => "B.Warn", Level.Info => "B.TextDim", _ => "B.Ok" });
    }

    /// <summary>Mientras se escriben archivos no se puede cerrar; mientras se prepara, se pregunta (no se ha escrito nada).</summary>
    private void GuardClose(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (writing) { e.Cancel = true; Confirm.Info(this, "Instalación en curso", "Se están copiando archivos al juego. Espera a que termine para no dejarlo a medias."); return; }
        if (progressWindow is { IsRunning: true })
        {
            if (!Confirm.Ask(this, "Instalación en curso", [], "Se está preparando la instalación. Si cierras ahora se cancela sin escribir nada en el juego.", "Cancelar y cerrar", danger: true)) { e.Cancel = true; return; }
            cts?.Cancel();
        }
    }

    private void ShowMode()
    {
        Page.Children.Clear();
        if (ModeNew.IsChecked == true) RenderNew();
        else if (ModeUpdate.IsChecked == true) RenderUpdate();
        else RenderVerify();
        // En otro idioma, la página se traduce al dibujarla (sin esperar al repaso periódico: nada de destellos en español)
        if (Lang.Current != 0) { Lang.Apply(Page); Lang.Apply(Status); Lang.Apply(BaseInfo); }
        Dispatcher.BeginInvoke(WriteStatus, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---------------------------------------------------------------- Piezas de la página
    private static Border Section(string title, UIElement content, string? right = null)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new Border { Style = Ui.S("AccentBar"), Height = 22 });
        if (right is not null) { var r = Ui.Text(right, "T.Note"); r.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(r, Dock.Right); head.Children.Add(r); }
        head.Children.Add(Ui.Text(title, "T.Section"));
        var stack = new StackPanel(); stack.Children.Add(head); stack.Children.Add(content);
        return new Border { Style = Ui.S("Panel"), Margin = new Thickness(0, 0, 0, 10), Child = stack };
    }

    private static TextBlock Note(string text, Level? level = null, double size = 13)
    {
        var t = Ui.Text(text, "T.Note"); t.TextWrapping = TextWrapping.Wrap; t.FontSize = size;
        if (level is { } l) t.Foreground = Ui.B(l switch { Level.Ok => "B.Ok", Level.Warn => "B.Warn", Level.Error => "B.Error", _ => "B.TextDim" });
        return t;
    }

    private static Button Key(string text, Func<Task> click, double width = 150, bool primary = false, string? tip = null)
    {
        var b = Ui.Button(text, primary ? "Btn.Primary" : "Btn", async (_, _) => await click(), width, tip);
        b.Margin = new Thickness(0, 0, 8, 0);
        return b;
    }

    private static ComboBox Combo<T>(IEnumerable<T> items, Func<T, string> label, T? selected, Action<T> changed, double width = 520)
    {
        var list = items.ToList();
        // Las opciones se traducen al crearlas: WPF las dibuja desde los datos y el traductor de la página no llega a ellas
        var combo = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = list.Select(label).Select(Lang.T).ToList() };
        var index = selected is null ? -1 : list.IndexOf(selected);
        combo.SelectedIndex = index;
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) changed(list[combo.SelectedIndex]); };
        return combo;
    }

    private static UIElement Lamp(Level level) => new System.Windows.Shapes.Ellipse
    {
        Width = 10, Height = 10, Margin = new Thickness(0, 4, 10, 0), VerticalAlignment = VerticalAlignment.Top,
        Fill = Ui.B(level switch { Level.Ok => "B.Ok", Level.Warn => "B.Warn", Level.Error => "B.Error", _ => "B.TextDim" })
    };

    private static string GameLabel(LibraryGame g) => $"{g.Name}  ·  {g.Platform}";

    // ================================================================ INSTALACIÓN NUEVA
    private void RenderNew()
    {
        // 1 · Juego
        var gameBox = new StackPanel();
        var row = new WrapPanel();
        row.Children.Add(Combo(games, GameLabel, game, g => { SelectGame(g); ShowMode(); }));
        row.Children.Add(new Border { Width = 10 });
        row.Children.Add(Key("Otro ejecutable…", () => { PickExecutable(); return Task.CompletedTask; }, 190));
        gameBox.Children.Add(row);
        if (game is not null)
        {
            var info = Info(game);
            var platform = Note($"Plataforma {game.Platform} · API {info.Api ?? "no determinada"} · ejecutable {(info.Exe is null ? "no identificado" : Path.GetFileName(info.Exe))}", null, 12.5);
            platform.Margin = new Thickness(0, 10, 0, 2); gameBox.Children.Add(platform);
            if (info.Opti.Binary is not null) gameBox.Children.Add(Note($"Este juego ya tiene OptiScaler ({Path.GetFileName(info.Opti.Binary)}): para cambiar solo el motor usa «Actualizar ILION».", Level.Warn));
        }
        else { var pick = Note(games.Length == 0 ? "No se encontraron juegos: usa «Otro ejecutable…»." : "Elige el juego.", null, 12.5); pick.Margin = new Thickness(0, 10, 0, 0); gameBox.Children.Add(pick); }
        Page.Children.Add(Section("1 · JUEGO", gameBox, $"{games.Length} juegos detectados"));

        // 2 · Carpeta
        var folderBox = new StackPanel();
        var frow = new DockPanel();
        var change = Key("Cambiar…", () => { PickFolder(); return Task.CompletedTask; }, 130); DockPanel.SetDock(change, Dock.Right); frow.Children.Add(change);
        var path = Ui.Text(exeDir ?? "—", "T.Value"); path.VerticalAlignment = VerticalAlignment.Center; path.ToolTip = exeDir; frow.Children.Add(path);
        folderBox.Children.Add(frow);
        var where = Note("Junto al ejecutable real del juego (en Unreal, <Juego>\\Binaries\\Win64). Ningún archivo del juego se sobrescribe.", null, 12.5); where.Margin = new Thickness(0, 10, 0, 0);
        folderBox.Children.Add(where);
        Page.Children.Add(Section("2 · CARPETA DE INSTALACIÓN", folderBox));

        // 3 · Core OptiScaler
        var b = engine.Base;
        var coreBox = new StackPanel();
        coreBox.Children.Add(Note($"OptiScaler en el commit f6f75ddf (compilación oficial nightly-20261009) con FidelityFX SDK v2.4.0 y Streamline 2.14.1 de NVIDIA: la base exacta con la que funciona ILION {b.Engine.Version}. Cada archivo se comprueba por SHA-256; si tu paquete local ya los tiene idénticos, no se descarga nada.", null, 13));
        var opts = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var sl = new CheckBox { Style = Ui.S("Check"), Content = "Streamline 2.14.1 de NVIDIA (entradas DLSS; 276 MB la primera vez)", IsChecked = withStreamline, Margin = new Thickness(0, 0, 20, 0) };
        sl.Checked += (_, _) => withStreamline = true; sl.Unchecked += (_, _) => withStreamline = false;
        opts.Children.Add(sl);
        opts.Children.Add(Key("Ver manifiesto", () => { ShowManifest(); return Task.CompletedTask; }, 150));
        coreBox.Children.Add(opts);
        Page.Children.Add(Section("3 · CORE OPTISCALER COMPATIBLE", coreBox, $"{b.Files.Length} archivos verificados"));

        // 4 · Motor ILION
        Page.Children.Add(Section("4 · MOTOR ILION", EngineBox(() => ShowMode())));

        // 5 · Mods
        Page.Children.Add(Section("5 · MODS ADICIONALES", ModsBox(), gpu?.Arch is { } ga ? $"Recomendaciones para {Gpu.BrandName(ga.Marca)} {ga.Nombre}" : "GPU no reconocida"));

        // 6 · Opciones avanzadas
        var adv = new StackPanel();
        var prow = new WrapPanel();
        var pl = Ui.Text("Nombre de carga de ILION", "T.Label"); pl.VerticalAlignment = VerticalAlignment.Center; pl.Margin = new Thickness(0, 0, 12, 0);
        prow.Children.Add(pl);
        prow.Children.Add(Combo(OptiScalerInstaller.Proxies, p => p, proxy, p => proxy = p, 200));
        adv.Children.Add(prow);
        var ag = new CheckBox { Style = Ui.S("Check"), Content = "D3D12Core (Agility SDK) para [FSR] FsrAgilitySDKUpgrade", IsChecked = withAgility, Margin = new Thickness(0, 10, 0, 0) };
        ag.Checked += (_, _) => withAgility = true; ag.Unchecked += (_, _) => withAgility = false; adv.Children.Add(ag);
        var oa = new CheckBox { Style = Ui.S("Check"), Content = "Permitir mods que su autor no indica para mi arquitectura (bajo mi responsabilidad)", IsChecked = allowOtherArch, Margin = new Thickness(0, 8, 0, 0) };
        oa.Checked += (_, _) => { allowOtherArch = true; ShowMode(); }; oa.Unchecked += (_, _) => { allowOtherArch = false; ShowMode(); }; adv.Children.Add(oa);
        var va = new CheckBox { Style = Ui.S("Check"), Content = "Variantes de ILION: FSR 4 (oficial, 4.1.1B, Exact, Lossy, Lossy2), XeMFG Clásica/WMMA, OptiPatcher y runtime NR de lmxxf (RDNA 3/4, 760 MB)", IsChecked = withVariants, Margin = new Thickness(0, 8, 0, 0) };
        va.Checked += (_, _) => withVariants = true; va.Unchecked += (_, _) => withVariants = false; adv.Children.Add(va);
        adv.Children.Add(Dlss4Box(() => ShowMode()));
        Page.Children.Add(Section("6 · OPCIONES AVANZADAS", adv));

        var go = Key("Continuar", ContinueNew, 220, primary: true);
        go.Height = 44; go.FontSize = 17; go.HorizontalAlignment = HorizontalAlignment.Right; go.Margin = new Thickness(0, 4, 0, 8);
        go.IsEnabled = game is not null && exeDir is not null && engineSource is { Problem: null } && !busy;
        Page.Children.Add(go);
    }

    /// <summary>DLSS 4 Modelo K: lleva DLL de NVIDIA que no se pueden redistribuir, así que es la única instalación local.</summary>
    private StackPanel Dlss4Box(Action changed)
    {
        var box = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        var row = new WrapPanel();
        var l = Ui.Text("DLSS 4 Modelo K (d4r) · instalación local", "T.Label"); l.VerticalAlignment = VerticalAlignment.Center; l.Margin = new Thickness(0, 0, 12, 0);
        row.Children.Add(l);
        row.Children.Add(Key(dlss4Folder is null ? "Elegir carpeta…" : "Cambiar…", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Carpeta Modelo_K de tu copia de ILION (ILION › Escaladores › DLSS4 › Modelo_K)" };
            if (dialog.ShowDialog(this) == true)
            {
                if (SetupEngine.FindD4r(dialog.FolderName) is { } found) dlss4Folder = found;
                else Confirm.Info(this, "DLSS 4 Modelo K", "En esa carpeta no está DLSS 4 Modelo K (falta d4r.ini).", error: true);
            }
            changed(); return Task.CompletedTask;
        }, 160));
        if (dlss4Folder is not null) row.Children.Add(Key("Quitar", () => { dlss4Folder = null; changed(); return Task.CompletedTask; }, 100));
        box.Children.Add(row);
        var rdna = gpu?.Arch?.Id is "rdna3" or "rdna4";
        box.Children.Add(Note(dlss4Folder is { } f ? $"Se copiará desde: {f}" + (rdna ? "" : "  ·  Pensado para AMD RDNA 3 / RDNA 4: en tu GPU no está verificado.")
            : "Lleva DLL propietarias de NVIDIA, que no se pueden redistribuir: Setup no las descarga. Si tienes tu copia (carpeta Modelo_K), elígela y se instala con ILION. Solo AMD RDNA 3 / RDNA 4.",
            dlss4Folder is not null && !rdna ? Level.Warn : null, 12.5));
        ((FrameworkElement)box.Children[^1]).Margin = new Thickness(0, 10, 0, 0);
        return box;
    }

    private void SelectGame(LibraryGame g)
    {
        if (centralGameId is not null && g.Id != centralGameId) centralMods.Clear(); // otro juego que el pedido por Central: lo marcado ya no aplica
        game = g;
        exeDir = Info(g).ExeDir;
        RememberGame(g);
    }

    private void PickExecutable()
    {
        var dialog = new OpenFileDialog { Title = "Ejecutable del juego", Filter = "Ejecutable (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) != true) return;
        try { manual.Add(dialog.FileName); } catch (IOException e) { Confirm.Info(this, "Ejecutable", e.Message, error: true); return; }
        var added = manual.Scan().Games.First(x => x.Executable!.Equals(Path.GetFullPath(dialog.FileName), StringComparison.OrdinalIgnoreCase));
        games = [.. games.Where(x => x.Id != added.Id), added];
        SelectGame(added); ShowMode();
    }

    private void PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Carpeta del ejecutable del juego", InitialDirectory = exeDir ?? "" };
        if (dialog.ShowDialog(this) != true) return;
        if (!Directory.EnumerateFiles(dialog.FolderName, "*.exe").Any()) { Confirm.Info(this, "Carpeta", "En esa carpeta no hay ningún ejecutable. OptiScaler va junto al .exe del juego.", error: true); return; }
        exeDir = dialog.FolderName; ShowMode();
    }

    private void ShowManifest()
    {
        var list = new StackPanel();
        list.Children.Add(Note("Cada archivo, con su origen oficial y su SHA-256. Justificación de la base:", null, 13));
        foreach (var j in engine.Base.Evidence) list.Children.Add(Note("· " + j, null, 12.5));
        list.Children.Add(new Border { Height = 10 });
        foreach (var f in engine.Base.Files)
        {
            var t = new TextBlock { FontFamily = (FontFamily)FindResource("F.Mono"), FontSize = 11.5, Foreground = Ui.B("B.Text"), Margin = new Thickness(0, 0, 0, 3), TextWrapping = TextWrapping.Wrap,
                Text = $"{f.Destination}  [{FrozenBase.Group(f)}]\n   {f.Sha256}  ·  {(f.Url ?? f.Origin + " › " + f.Entry)}" };
            list.Children.Add(t);
        }
        Confirm.View(this, "Base congelada de OptiScaler", new ScrollViewer { Content = list, MaxHeight = 520 }, 980);
    }

    /// <summary>Elegir el motor: paquetes usados o detectados, otra carpeta o una DLL suelta. Muestra versión y si es la aprobada.</summary>
    private StackPanel EngineBox(Action changed)
    {
        var box = new StackPanel();
        var row = new WrapPanel();
        row.Children.Add(Combo([Official, .. engineCandidates], p => p == Official ? $"ILION {engine.Base.Engine.Version} · publicación oficial en GitHub (recomendado)" : "Local: " + Path.GetFileName(p.TrimEnd('\\')) + "   ·   " + p,
            engineSource is { Remote: true } ? Official : engineSource?.PackageFolder ?? engineSource?.Dll,
            p => { engineSource = p == Official ? engine.RemoteEngine : engine.InspectEngine(p); if (engineSource.PackageFolder is { } f && engineSource.Problem is null) packages.Remember(f); changed(); }, 560));
        row.Children.Add(new Border { Width = 10 });
        row.Children.Add(Key("Paquete…", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Carpeta del paquete de ILION (la que tiene dxgi.dll)" };
            if (dialog.ShowDialog(this) == true) { engineSource = engine.InspectEngine(dialog.FolderName); if (engineSource.Problem is null) { packages.Remember(dialog.FolderName); engineCandidates = [dialog.FolderName, .. engineCandidates.Where(c => c != dialog.FolderName)]; } changed(); }
            return Task.CompletedTask;
        }, 120));
        row.Children.Add(Key("DLL…", () =>
        {
            var dialog = new OpenFileDialog { Title = "DLL de ILION (dxgi.dll)", Filter = "Biblioteca (*.dll)|*.dll" };
            if (dialog.ShowDialog(this) == true) { engineSource = engine.InspectEngine(dialog.FileName); changed(); }
            return Task.CompletedTask;
        }, 100));
        box.Children.Add(row);
        if (engineSource is null) box.Children.Add(Note("Elige el motor de ILION.", null, 12.5));
        else if (engineSource is { Remote: true, Problem: null })
        {
            var line = new DockPanel { Margin = new Thickness(0, 12, 0, 2) };
            line.Children.Add(Lamp(Level.Ok));
            line.Children.Add(Note($"OptiScaler ILION {engineSource.Version} · se descarga de github.com/ENE4S/Optiscaler-ILION y se comprueba por SHA-256 ({engine.Base.Engine.Sha256[..12]}…).", Level.Ok));
            box.Children.Add(line);
            box.Children.Add(Note("Con ILION se instalan también sus variantes (FSR 4 oficial, 4.1.1B, Exact, Lossy, Lossy2; XeMFG Clásica y WMMA; OptiPatcher) y, en RDNA 3/4, el runtime NR de lmxxf. Paquete o DLL local: «Paquete…» o «DLL…».", null, 12.5));
        }
        else if (engineSource.Problem is { } p) box.Children.Add(Note(p, Level.Error));
        else
        {
            var line = new DockPanel { Margin = new Thickness(0, 12, 0, 2) };
            line.Children.Add(Lamp(engineSource.Approved ? Level.Ok : Level.Warn));
            line.Children.Add(Note(engineSource.Approved
                ? $"OptiScaler ILION {engineSource.Version} · la DLL aprobada para esta base (SHA-256 {engine.Base.Engine.Sha256[..12]}…)."
                : $"OptiScaler ILION {engineSource.Version} · no es la DLL aprobada para esta base ({engine.Base.Engine.Version}): compatibilidad no verificada.",
                engineSource.Approved ? Level.Ok : Level.Warn));
            box.Children.Add(line);
            box.Children.Add(Note(engineSource.PackageFolder is null
                ? "DLL suelta: se usa la configuración oficial de OptiScaler (ILION completa sus claves al guardar) y no se añaden las carpetas propias del paquete."
                : "Paquete completo: del paquete se toman la DLL, su OptiScaler.ini, ILION\\ (escaladores, d4r), sus plugins (variantes XeMFG, OptiPatcher) y el runtime NR de lmxxf. Los mods se eligen abajo.", null, 12.5));
        }
        return box;
    }

    /// <summary>Mods por casillas: compatibilidad fundada, estado, versión aprobada, dependencias y conflictos.</summary>
    private StackPanel ModsBox()
    {
        modChecks.Clear();
        var box = new StackPanel();
        var api = game is null ? null : Info(game).Api;
        var items = engine.Resolve(gpu?.Arch, api, engineSource?.Problem is null ? engineSource?.Version : null);
        var counter = Note("", null, 13);
        void Recount() => counter.Text = modChecks.Count(c => c.Box.IsChecked == true) switch { 0 => "Ningún mod seleccionado", 1 => "1 mod seleccionado", var n => $"{n} mods seleccionados" };
        var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        tools.Children.Add(Key("Marcar todos los compatibles", () => { foreach (var c in modChecks.Where(c => c.Box.IsEnabled && c.Item.Compat == Compat.Compatible)) c.Box.IsChecked = true; return Task.CompletedTask; }, 250));
        tools.Children.Add(Key("Ninguno", () => { foreach (var c in modChecks) c.Box.IsChecked = false; return Task.CompletedTask; }, 120));
        counter.VerticalAlignment = VerticalAlignment.Center; counter.Margin = new Thickness(8, 0, 0, 0); tools.Children.Add(counter);
        box.Children.Add(tools);
        foreach (var item in items.Where(i => i.Recipe is not null))
        {
            var e = item.Entry;
            var archOnly = item.Compat == Compat.Incompatible && item.Why?.StartsWith("No está pensado") == true;
            var check = new CheckBox { Style = Ui.S("Check"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0),
                IsEnabled = item.Compat != Compat.Incompatible || archOnly && allowOtherArch };
            check.IsChecked = check.IsEnabled && (item.Preselect || centralMods.Contains(e.Id)); // recomendado para la GPU o marcado desde ILION Central
            check.Checked += (_, _) => Recount(); check.Unchecked += (_, _) => Recount();
            System.Windows.Automation.AutomationProperties.SetName(check, "Seleccionar " + e.Name);
            modChecks.Add((check, item));
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            grid.Children.Add(check);
            var text = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            var title = new TextBlock { FontSize = 15, Foreground = Ui.B("B.Text"), FontFamily = (FontFamily)FindResource("F.Brand"), TextWrapping = TextWrapping.Wrap };
            title.Inlines.Add(new System.Windows.Documents.Run(e.Name));
            title.Inlines.Add(new System.Windows.Documents.Run($"   {e.Author} · {e.Version} · {e.Apis}") { FontFamily = (FontFamily)FindResource("F.Ui"), FontSize = 12.5, Foreground = Ui.B("B.TextDim") });
            text.Children.Add(title);
            text.Children.Add(Note(e.Description, null, 12.5));
            if (e.Dependencies is { } dep) text.Children.Add(Note("Necesita: " + dep, null, 12));
            if (e.Conflicts is { } con) text.Children.Add(Note("Conflictos: " + con, null, 12));
            if (item.Why is { } why) text.Children.Add(Note(why, item.Compat == Compat.Incompatible ? Level.Error : Level.Warn, 12));
            if (newerTags.TryGetValue(e.Id, out var newer)) text.Children.Add(Note($"Su autor ya publicó {newer}: aún sin aprobar para ILION (se instala la {e.Version}).", Level.Warn, 12));
            Grid.SetColumn(text, 1); grid.Children.Add(text);
            var state = Ui.Pill(e.State, e.State == "estable" ? Tone.Ok : Tone.Warn); state.Margin = new Thickness(0, 2, 8, 0); state.VerticalAlignment = VerticalAlignment.Top;
            state.Width = 124; ((TextBlock)state.Child).HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(state, 2); grid.Children.Add(state);
            var compat = Ui.Pill(item.Compat switch { Compat.Compatible => item.Preselect ? "Recomendado" : "Compatible", Compat.Warning => "Con aviso", _ => "No compatible" },
                item.Compat switch { Compat.Compatible => Tone.Ok, Compat.Warning => Tone.Warn, _ => Tone.Error });
            compat.VerticalAlignment = VerticalAlignment.Top; compat.Margin = new Thickness(0, 2, 0, 0);
            compat.Width = 136; ((TextBlock)compat.Child).HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(compat, 3); grid.Children.Add(compat);
            box.Children.Add(new Border { BorderBrush = Ui.B("B.Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 10), Child = grid });
        }
        Recount();
        // Lo que no se instala aparte: integrado, dependencias, variantes del paquete y referencias
        var other = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var group in items.Where(i => i.Recipe is null).GroupBy(i => i.Entry.Category))
        {
            var head = Ui.Text(group.Key switch { "integrado" => "INTEGRADO O INSTALADO CON ILION", "dependencia" => "DEPENDENCIAS", "variante" => "INSTALACIÓN LOCAL", _ => "REFERENCIAS (INSTALACIÓN MANUAL)" }, "T.Kicker");
            head.Margin = new Thickness(0, 8, 0, 4); other.Children.Add(head);
            foreach (var i in group) other.Children.Add(Note($"{i.Entry.Name} — {i.Entry.Description}", null, 12));
        }
        other.Visibility = showMoreInfo ? Visibility.Visible : Visibility.Collapsed;
        var toggle = new TextBlock { Text = (showMoreInfo ? "▾  " : "▸  ") + "Más información: integrados, instalación local, dependencias y referencias", Style = Ui.S("T.Label"), FontSize = 13, Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 4, 0, 0) };
        toggle.MouseLeftButtonUp += (_, _) => { showMoreInfo = !showMoreInfo; other.Visibility = showMoreInfo ? Visibility.Visible : Visibility.Collapsed; toggle.Text = (showMoreInfo ? "▾  " : "▸  ") + toggle.Text[3..]; };
        var more = new StackPanel(); more.Children.Add(toggle); more.Children.Add(other);
        box.Children.Add(new Border { BorderBrush = Ui.B("B.Line"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Child = more });
        return box;
    }

    private async Task ContinueNew()
    {
        if (busy || game is null || exeDir is null || engineSource is not { Problem: null } src) return;
        var chosen = modChecks.Where(c => c.Box.IsChecked == true).Select(c => c.Item).ToList();
        centralMods.Clear(); // lo marcado desde ILION Central vale para esta instalación
        if (Requests(chosen, exeDir, proxy) is not { } requests) return;
        var notes = new List<string> { "La instalación puede tardar varios minutos. Se descargarán, verificarán y prepararán los componentes seleccionados. Mantén la aplicación abierta hasta finalizar." };
        if (!src.Approved) notes.Add($"La DLL de ILION elegida ({src.Version}) no es la aprobada para esta base: compatibilidad no verificada.");
        foreach (var i in ModBatch.Analyze(requests, exeDir)) notes.Add((i.Blocking ? "No se instala " : "") + i.Mod + ": " + i.Message);
        foreach (var r in requests.Where(r => r.Proxy is not null)) notes.Add($"{r.Mod.Name} se carga como {r.Proxy}.");
        if (NrWarning([.. requests.Select(r => r.Mod.Name), .. withVariants && gpu?.Arch?.Id is "rdna3" or "rdna4" ? ["runtime NR de lmxxf"] : Array.Empty<string>()]) is { } nr) notes.Add(nr);
        if (dlss4Folder is not null) notes.Add("DLSS 4 Modelo K se copia desde tu carpeta local (instalación local).");
        notes.Add("Antes de escribir se guarda una copia de todo lo que se sustituya; ningún archivo del juego se sobrescribe. Todo se puede restaurar desde «Verificar y reparar».");
        var lines = new List<(string, string)> { ("Juego", game.Name), ("Carpeta", exeDir), ("Base", $"OptiScaler f6f75dd (+ FidelityFX 2.4.0{(withStreamline ? ", Streamline 2.14.1" : "")})"),
            ("Motor", $"OptiScaler ILION {src.Version} como {proxy}" + (src.Remote ? " (GitHub oficial)" : "")), ("Variantes", withVariants ? "FSR 4, XeMFG, OptiPatcher" + (gpu?.Arch?.Id is "rdna3" or "rdna4" ? ", runtime NR lmxxf" : "") : "no"), ("DLSS 4 local", dlss4Folder is null ? "no" : "sí"), ("Mods", requests.Count == 0 ? "ninguno" : string.Join(", ", requests.Select(r => r.Mod.Name))) };
        if (!Confirm.Ask(this, "Instalación nueva", [.. lines], string.Join("\n\n", notes), "Instalar")) { Say("Instalación cancelada.", Level.Info); return; }
        await RunNew(game, exeDir, src, requests);
    }

    private async Task RunNew(LibraryGame g, string dir, EngineSource src, List<BatchRequest> requests)
    {
        if (RefuseIfOpen(g)) return;
        busy = true; writing = false; cts = new CancellationTokenSource();
        var token = cts.Token;
        var window = InstallProgress.Open(this, requests.Count + 1, () => !writing);
        progressWindow = window;
        window.CancelRequested += () => cts.Cancel();
        var progress = new Progress<ModProgress>(p => { if (p.Stage == "Instalando") writing = true; window.Update(p); Say($"{p.Mod} · {p.Stage}" + (p.Fraction is { } f && f < 1 ? $" {f * 100:0} %" : "") + " · ver progreso", Level.Info); });
        var groups = new HashSet<string>(); if (withStreamline) groups.Add("streamline"); if (withAgility) groups.Add("agility"); if (withVariants) groups.Add("variantes");
        var roots = games.Select(x => x.Root).Concat(engineCandidates.Where(Directory.Exists)).ToArray();
        NewInstallResult result;
        try { result = await Task.Run(() => engine.NewInstallAsync(g, dir, src, proxy, groups, requests, gpu?.Arch, roots, progress, token)); }
        catch (Exception e) when (e is not OutOfMemoryException) { LogError($"Instalación nueva {g.Name}", e); result = new(null, null, [], ModBatch.Friendly(e), false); }
        finally { busy = false; writing = false; }
        Log($"Instalación nueva {g.Name}: motor {(result.Engine is null ? "no" : result.Engine.Version)}; mods {string.Join(", ", result.Mods?.Installed ?? [])}; error {result.Error}; base {string.Join(" | ", result.BaseSources)}");
        var installed = new List<string>();
        if (result.Engine is { } m) installed.Add($"OptiScaler ILION {m.Version} y la base congelada ({m.Files.Length} archivos)");
        installed.AddRange(result.Mods?.Installed ?? []);
        var failed = new List<(string, string)>();
        if (result.Error is { } err) failed.Add(("Instalación", err));
        failed.AddRange(result.Mods?.Failed ?? []);
        if (result.Engine is not null && dlss4Folder is { } d4)
            try { var op = await Task.Run(() => engine.InstallDlss4Local(g, dir, d4)); installed.Add(op.Label); }
            catch (Exception e) when (e is not OutOfMemoryException) { LogError("DLSS 4 Modelo K", e); failed.Add(("DLSS 4 Modelo K (local)", ModBatch.Friendly(e))); }
        lock (gameInfo) gameInfo.Remove(SetupEngine.GameId(g));
        if (failed.Count > 0) Log($"Instalación nueva {g.Name}: fallos {string.Join(" | ", failed.Select(f => f.Item1 + ": " + f.Item2))}");
        window.Retry += () => { if (result.Engine is null) _ = RunNew(g, dir, src, requests); else _ = RunMods(g, dir, [.. requests.Where(r => failed.Any(f => f.Item1 == r.Mod.Name))]); };
        window.Finish(new BatchResult(result.Mods?.BatchId ?? "", [.. installed], [.. failed], result.Mods?.Pending ?? [], [], [], result.Cancelled));
        Say(result.Cancelled ? "Instalación cancelada: no se escribió nada." : failed.Count == 0 ? $"ILION instalado en {g.Name}." : "Instalación terminada con avisos.", failed.Count == 0 ? Level.Ok : Level.Warn);
        ShowMode();
    }

    /// <summary>Instala o completa mods en un juego que ya tiene ILION (reintentos y «Completar mods»).</summary>
    private async Task RunMods(LibraryGame g, string dir, List<BatchRequest> requests)
    {
        if (requests.Count == 0 || busy || RefuseIfOpen(g)) return;
        busy = true; writing = false; cts = new CancellationTokenSource();
        var token = cts.Token;
        var window = InstallProgress.Open(this, requests.Count, () => !writing);
        progressWindow = window; window.CancelRequested += () => cts.Cancel();
        var progress = new Progress<ModProgress>(p => { if (p.Stage == "Instalando") writing = true; window.Update(p); });
        BatchResult result;
        try
        {
            var installer = new ModInstaller(http, data, engine.Operations);
            var roots = games.Select(x => x.Root).Concat(engineSource?.PackageFolder is { } pf ? [pf] : []).Where(r => !r.Equals(g.Root, StringComparison.OrdinalIgnoreCase)).ToArray();
            result = await Task.Run(() => ModBatch.RunAsync(installer, engine.Operations, engine.Updates, SetupEngine.GameId(g), dir, requests, roots, progress, token));
        }
        catch (Exception e) when (e is not OutOfMemoryException) { LogError($"Mods {g.Name}", e); result = new("", [], [("Mods", ModBatch.Friendly(e))], [], [], []); }
        finally { busy = false; writing = false; }
        Log($"Mods {g.Name}: {string.Join(", ", result.Installed)}; fallos {string.Join(" | ", result.Failed.Select(f => f.Mod + ": " + f.Error))}");
        window.Retry += () => _ = RunMods(g, dir, [.. requests.Where(r => result.Failed.Any(f => f.Mod == r.Mod.Name))]);
        window.Finish(result);
        ShowMode();
    }

    // ================================================================ ACTUALIZAR ILION
    private LibraryGame? updateGame;
    private void RenderUpdate()
    {
        var withOpti = games.Where(g => Info(g).Opti.Binary is not null).ToArray();
        updateGame ??= LastGame(withOpti);
        var box = new StackPanel();
        var urow = new WrapPanel();
        urow.Children.Add(Combo(withOpti, GameLabel, updateGame, g => { updateGame = g; RememberGame(g); ShowMode(); }));
        urow.Children.Add(new Border { Width = 10 });
        urow.Children.Add(Key("Buscar versión nueva de ILION", CheckForIlion, 290));
        box.Children.Add(urow);
        if (withOpti.Length == 0) { var none = Note("Ningún juego detectado tiene OptiScaler instalado.", null, 12.5); none.Margin = new Thickness(0, 10, 0, 0); box.Children.Add(none); }
        Page.Children.Add(Section("JUEGO CON ILION INSTALADO", box, $"{withOpti.Length} juegos con OptiScaler"));
        if (updateGame is null) { Watermark("Elige un juego con ILION instalado para actualizar su motor."); return; }
        var info = Info(updateGame);
        var cur = new StackPanel();
        var bin = info.Opti.Binary!;
        var edition = OptiScalerInstaller.EditionOf(bin);
        var sha = ComponentUpdateService.Sha256(bin);
        cur.Children.Add(Note($"{Path.GetFileName(bin)} · {OptiScalerInstaller.Title(bin, edition)} · SHA-256 {sha[..12]}…" + (sha == engine.Base.Engine.Sha256 ? " · ya es la aprobada" : ""), sha == engine.Base.Engine.Sha256 ? Level.Ok : null));
        Page.Children.Add(Section("MOTOR INSTALADO", cur));
        Page.Children.Add(Section("NUEVO MOTOR ILION", EngineBox(() => ShowMode())));
        var go = Key("Comprobar y actualizar", () => UpdateNow(updateGame, info), 260, primary: true);
        go.Height = 44; go.HorizontalAlignment = HorizontalAlignment.Right; go.IsEnabled = engineSource is { Problem: null } && !busy;
        Page.Children.Add(go);
    }

    /// <summary>Lee de GitHub el manifiesto firmado: si aprueba una versión nueva de ILION, la ofrece.</summary>
    private async Task CheckForIlion()
    {
        if (busy) return;
        busy = true; Say("Buscando en GitHub la versión aprobada de ILION…", Level.Info);
        try
        {
            var (changed, notes) = await RefreshManifests();
            var text = changed
                ? $"Disponible ILION {engine.Base.Engine.Version}. Ya está elegido como «Nuevo motor ILION»: pulsa «Comprobar y actualizar» para instalarlo en el juego."
                : $"Tienes la versión aprobada más reciente: ILION {engine.Base.Engine.Version}.";
            Confirm.Info(this, "Versión de ILION", text + (notes.Any(n => n.Contains("sin conexión") || n.Contains("firma")) ? "\n\n" + string.Join("\n", notes) : ""));
            Say(changed ? $"Disponible ILION {engine.Base.Engine.Version}." : $"ILION {engine.Base.Engine.Version} es la versión aprobada más reciente.", changed ? Level.Warn : Level.Ok);
        }
        finally { busy = false; }
        ShowMode();
    }

    private async Task UpdateNow(LibraryGame g, (string? Exe, string ExeDir, string? Api, OptiScalerInstallation Opti) info)
    {
        if (engineSource is not { Problem: null } src || busy || RefuseIfOpen(g)) return;
        if (src.Remote)
        {
            busy = true; Say($"Descargando ILION {src.Version} de su publicación oficial…", Level.Info);
            try { src = await Task.Run(() => engine.DownloadEngineAsync(engineCandidates.Where(Directory.Exists), null, CancellationToken.None)); }
            catch (Exception e) when (e is not OutOfMemoryException) { LogError("Descargar ILION", e); Confirm.Info(this, "Actualizar ILION", ModBatch.Friendly(e), error: true); return; }
            finally { busy = false; }
        }
        var (ok, notes) = engine.CheckUpdate(info.ExeDir, info.Opti, src);
        if (!ok) { Confirm.Info(this, "No se puede actualizar solo la DLL", string.Join("\n\n", notes), error: true); return; }
        if (!Confirm.Ask(this, "Actualizar ILION", [("Juego", g.Name), ("DLL actual", Path.GetFileName(info.Opti.Binary!)), ("Nueva", $"OptiScaler ILION {src.Version}")],
            string.Join("\n\n", notes.Prepend("Solo se sustituye la DLL del motor; la base, los mods y la configuración se conservan. Se guarda una copia de la DLL actual (restaurable).")), "Actualizar")) return;
        try
        {
            busy = true;
            var op = await Task.Run(() => engine.UpdateEngine(g, info.ExeDir, info.Opti, src));
            Log($"Actualizado {g.Name}: {op.Label}");
            lock (gameInfo) gameInfo.Remove(SetupEngine.GameId(g));
            Say($"ILION actualizado en {g.Name}: {op.Label}.");
        }
        catch (Exception e) when (e is not OutOfMemoryException) { LogError("Actualizar ILION", e); Confirm.Info(this, "Actualizar ILION", ModBatch.Friendly(e), error: true); }
        finally { busy = false; }
        ShowMode();
    }

    // ================================================================ VERIFICAR Y REPARAR
    private LibraryGame? verifyGame;
    private VerifyItem[]? verifyItems;
    private void RenderVerify()
    {
        var withOpti = games.Where(g => Info(g).Opti.Binary is not null).ToArray();
        verifyGame ??= LastGame(withOpti);
        var box = new StackPanel();
        var row = new WrapPanel();
        row.Children.Add(Combo(withOpti, GameLabel, verifyGame, g => { verifyGame = g; RememberGame(g); verifyItems = null; ShowMode(); }));
        row.Children.Add(new Border { Width = 10 });
        row.Children.Add(Key("Verificar", () => { if (verifyGame is not null) { RunVerify(verifyGame); ShowMode(); } return Task.CompletedTask; }, 150, primary: true));
        box.Children.Add(row);
        Page.Children.Add(Section("JUEGO", box, $"{withOpti.Length} juegos con OptiScaler"));
        if (verifyGame is null) { Watermark("Elige un juego y pulsa «Verificar»."); return; }
        if (verifyItems is null) { Watermark("Pulsa «Verificar» para comprobar ILION, su base y sus mods en este juego."); return; }
        var info = Info(verifyGame);
        foreach (var area in verifyItems.GroupBy(i => i.Area))
        {
            var list = new StackPanel();
            var shown = area.Key == "Base" ? area.Where(i => i.State != Level.Ok).ToArray() : area.ToArray();
            if (area.Key == "Base") list.Children.Add(Note($"{area.Count(i => i.State == Level.Ok)} de {area.Count()} archivos idénticos a la base congelada.", area.All(i => i.State == Level.Ok) ? Level.Ok : Level.Warn));
            foreach (var i in shown)
            {
                var line = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                line.Children.Add(Lamp(i.State));
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Ui.B("B.Text") };
                t.Inlines.Add(new System.Windows.Documents.Run(i.What + "  ") { FontFamily = (FontFamily)FindResource("F.Strong") });
                t.Inlines.Add(new System.Windows.Documents.Run(i.Detail) { Foreground = Ui.B("B.TextDim") });
                line.Children.Add(t);
                list.Children.Add(line);
            }
            Page.Children.Add(Section(area.Key.ToUpperInvariant(), list));
        }
        // Reparar
        var repair = new WrapPanel();
        var missing = verifyItems.Count(i => i.Area == "Base" && i.Detail == "Falta.");
        var different = verifyItems.Count(i => i.Area == "Base" && i.State == Level.Warn);
        repair.Children.Add(Key($"Reponer lo que falta ({missing})", () => RepairBase(verifyGame, info.ExeDir, false), 260));
        repair.Children.Add(Key($"Sustituir lo distinto ({different})", () => RepairBase(verifyGame, info.ExeDir, true), 260));
        var incomplete = engine.Installable().Where(r => ModInstaller.Installed(r, info.ExeDir) && ModRequirements.Missing(r, info.ExeDir).Length > 0).ToList();
        repair.Children.Add(Key($"Completar mods ({incomplete.Count})", () => RunMods(verifyGame, info.ExeDir, [.. incomplete.Select(r => new BatchRequest(r))]), 220));
        ((Button)repair.Children[0]).IsEnabled = missing > 0; ((Button)repair.Children[1]).IsEnabled = different > 0; ((Button)repair.Children[2]).IsEnabled = incomplete.Count > 0;
        repair.Children.Add(Key(SetupEngine.Dlss4Installed(info.ExeDir) ? "DLSS 4 Modelo K (local): reinstalar…" : "DLSS 4 Modelo K (local)…", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Carpeta Modelo_K de tu copia de ILION" };
            if (dialog.ShowDialog(this) != true) return Task.CompletedTask;
            try { var op = engine.InstallDlss4Local(verifyGame, info.ExeDir, dialog.FolderName); Log(op.Label); Say($"{op.Label} en {verifyGame.Name}."); }
            catch (Exception e) when (e is not OutOfMemoryException) { LogError("DLSS 4 Modelo K", e); Confirm.Info(this, "DLSS 4 Modelo K", ModBatch.Friendly(e), error: true); }
            verifyItems = null; ShowMode(); return Task.CompletedTask;
        }, 300));
        Page.Children.Add(Section("REPARAR", repair));
        var updates = engine.ModUpdates(verifyGame, info.ExeDir);
        if (updates.Length > 0) Page.Children.Add(Section("ACTUALIZACIONES DE MODS", UpdatesBox(verifyGame, info.ExeDir, updates), $"{updates.Length} con una versión aprobada distinta"));
        Page.Children.Add(Section("AÑADIR MODS", AddModsBox(verifyGame, info.ExeDir, info.Api), "Los que aún no están instalados en este juego"));
        Page.Children.Add(undoSection = Section("DESHACER CAMBIOS", RestoreBox(verifyGame, info.ExeDir)));
    }

    /// <summary>Mods del catálogo que este juego aún no tiene: se marcan y se instalan sobre la instalación de ILION existente.</summary>
    private StackPanel AddModsBox(LibraryGame g, string dir, string? api)
    {
        var box = new StackPanel();
        var items = engine.Resolve(gpu?.Arch, api, engine.Base.Engine.Version).Where(i => i.Recipe is not null && !ModInstaller.Installed(i.Recipe, dir)).ToArray();
        if (items.Length == 0) { box.Children.Add(Note("Todos los mods instalables del catálogo ya están en este juego.", Level.Ok, 13)); return box; }
        var checks = new List<(CheckBox Box, CatalogItem Item)>();
        foreach (var i in items)
        {
            var c = new CheckBox { Style = Ui.S("Check"), Margin = new Thickness(0, 0, 0, 6), IsEnabled = i.Compat != Compat.Incompatible || allowOtherArch,
                Content = $"{i.Entry.Name}  ·  {i.Entry.Version}" + (i.Why is { } w ? $"  —  {w}" : "") };
            checks.Add((c, i)); box.Children.Add(c);
        }
        var go = Key("Instalar seleccionados", () =>
        {
            if (Requests([.. checks.Where(x => x.Box.IsChecked == true).Select(x => x.Item)], dir, null) is not { Count: > 0 } requests) return Task.CompletedTask;
            var notes = new List<string> { "La instalación puede tardar varios minutos. Se descargarán, verificarán y prepararán los componentes seleccionados. Mantén la aplicación abierta hasta finalizar." };
            foreach (var issue in ModBatch.Analyze(requests, dir)) notes.Add((issue.Blocking ? "No se instala " : "") + issue.Mod + ": " + issue.Message);
            if (NrWarning(requests.Select(r => r.Mod.Name)) is { } nr) notes.Add(nr);
            if (!Confirm.Ask(this, "Añadir mods", [("Juego", g.Name), ("Mods", string.Join(", ", requests.Select(r => r.Mod.Name)))], string.Join("\n\n", notes), "Instalar")) return Task.CompletedTask;
            return RunMods(g, dir, requests).ContinueWith(_ => Dispatcher.Invoke(() => { verifyItems = null; ShowMode(); }));
        }, 240, primary: true);
        go.Margin = new Thickness(0, 6, 0, 0); go.HorizontalAlignment = HorizontalAlignment.Left;
        box.Children.Add(go);
        return box;
    }

    /// <summary>Los mods elegidos, cada uno con un nombre de carga libre si lo necesita (distinto del de ILION). Null si no cabe alguno.</summary>
    private List<BatchRequest>? Requests(IEnumerable<CatalogItem> chosen, string dir, string? ilionProxy)
    {
        var requests = new List<BatchRequest>();
        foreach (var item in chosen)
        {
            string? p = null;
            if (item.Recipe!.ProxyNames.Length > 0)
            {
                p = ModInstaller.ProxyChoices(item.Recipe, dir).Where(c => c.Free && !c.Name.Equals(ilionProxy, StringComparison.OrdinalIgnoreCase)).Select(c => c.Name).FirstOrDefault();
                if (p is null) { Confirm.Info(this, item.Entry.Name, "Todos los nombres de carga que admite ya están ocupados en la carpeta del juego.", error: true); return null; }
            }
            requests.Add(new(item.Recipe, p));
        }
        return requests;
    }

    /// <summary>Aviso único para lo que usa DLSS Neural Rendering: el archivo oficial de NVIDIA lo pone el usuario.</summary>
    private static string? NrWarning(IEnumerable<string> names)
    {
        var nr = names.Where(n => n.Contains("NR", StringComparison.Ordinal) || n.Contains("Neural", StringComparison.OrdinalIgnoreCase)).ToArray();
        return nr.Length == 0 ? null : $"IMPORTANTE ({string.Join(", ", nr)}): DLSS Neural Rendering necesita el archivo oficial de NVIDIA, que debes tener descargado previamente. Setup no lo descarga ni lo incluye.";
    }

    private async Task RepairBase(LibraryGame g, string dir, bool replaceDifferent)
    {
        if (busy || RefuseIfOpen(g)) return;
        if (replaceDifferent && !Confirm.Ask(this, "Sustituir lo distinto", [("Juego", g.Name)],
            "Los archivos de la base que no son los congelados se cambian por los congelados. Si los actualizaste tú a propósito, perderías ese cambio (queda una copia y se puede restaurar).", "Sustituir", danger: true)) return;
        busy = true;
        var window = InstallProgress.Open(this, 1, () => false); progressWindow = window;
        var progress = new Progress<ModProgress>(window.Update); // creado aquí, en la ventana: informa siempre en el hilo de la interfaz
        try
        {
            var locals = engineCandidates.Where(Directory.Exists).ToArray();
            var op = await Task.Run(() => engine.RepairBaseAsync(g, dir, replaceDifferent, locals, progress, CancellationToken.None));
            Log($"Reparar base {g.Name} ({(replaceDifferent ? "sustituir lo distinto" : "reponer lo que falta")}): {op?.Label ?? "nada que reparar"}");
            window.Finish(new BatchResult("", op is null ? ["No había nada que reparar"] : [op.Label], [], [], [], []));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            LogError($"Reparar base {g.Name}", e);
            window.Finish(new BatchResult("", [], [("Reparar base", ModBatch.Friendly(e))], [], [], []));
        }
        finally { busy = false; }
        verifyItems = null; lock (gameInfo) gameInfo.Remove(SetupEngine.GameId(g)); ShowMode();
    }

    /// <summary>Cambios que Setup hizo en este juego, cada uno deshacible: los mods instalados de una vez, las reparaciones,
    /// el cambio de la DLL y DLSS 4 local; y la instalación de ILION, que se retira con «Desinstalar ILION».</summary>
    private StackPanel RestoreBox(LibraryGame g, string dir)
    {
        var box = new StackPanel();
        var id = SetupEngine.GameId(g);
        // La instalación y la desinstalación de ILION tienen su propio registro: se gestionan solo con «Desinstalar ILION»
        var ops = engine.Operations.All(id).Where(o => o.Completed && o.Kind is not ("Instalación" or "Desinstalación" or "Desinstalación completa")).ToArray();
        var install = engine.Installer.Installed(id);
        if (ops.Length == 0 && install is null) { box.Children.Add(Note("Setup no ha hecho cambios en este juego que se puedan deshacer.", null, 12.5)); return box; }
        var intro = Note("Cada cambio de Setup se puede deshacer: vuelve lo que había justo antes de ese cambio. Si un cambio posterior tocó los mismos archivos, deshaz primero ese. Tus partidas, tu OptiScaler.ini y los archivos que cambiaste tú no se tocan.", null, 12.5);
        intro.Margin = new Thickness(0, 0, 0, 12); box.Children.Add(intro);

        void Row(string title, string? detail, string? blocked, Button button)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            button.IsEnabled = blocked is null; button.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(button, Dock.Right); row.Children.Add(button);
            var text = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            var head = Ui.Text(title, "T.Label"); head.TextWrapping = TextWrapping.Wrap; text.Children.Add(head);
            if (detail is not null) text.Children.Add(Note(detail, null, 12));
            if (blocked is not null) text.Children.Add(Note(blocked, Level.Warn, 12));
            row.Children.Add(text);
            box.Children.Add(row);
        }
        async Task Undo(string what, Func<string> work)
        {
            try { var done = await Task.Run(work); Log($"Deshacer en {g.Name}: {done}"); Say($"Deshecho: {done}."); }
            catch (Exception e) when (e is not OutOfMemoryException) { LogError("Deshacer", e); Confirm.Info(this, what, ModBatch.Friendly(e), error: true); }
            lock (gameInfo) gameInfo.Remove(id); verifyItems = null; ShowMode();
        }

        // Mods instalados de una vez (un «lote»): se quitan juntos, con los archivos oficiales que se pusieron para ellos
        foreach (var batch in ops.Where(o => o.Batch is not null).GroupBy(o => o.Batch!).OrderByDescending(b => b.Max(o => o.When)))
        {
            var mods = batch.Where(o => o.Kind == ModBatch.Kind).ToArray();
            var when = batch.Min(o => o.When);
            var blocked = batch.Select(o => engine.Operations.CanRestore(o)).FirstOrDefault(w => w is not null);
            var names = string.Join(", ", mods.Select(o => o.Label));
            var title = $"Mods instalados el {when:dd/MM/yyyy} a las {when:HH:mm} ({mods.Length})";
            Row(title, names, blocked is null ? null : "Antes hay que deshacer un cambio posterior sobre los mismos archivos.",
                Key("Quitar estos mods", async () =>
                {
                    if (!Confirm.Ask(this, "Quitar estos mods", [("Juego", g.Name), ("Mods", names)],
                        "Se quitan los archivos que pusieron estos mods y vuelve lo que había antes de instalarlos. ILION y los demás cambios se quedan. Si algún archivo lo cambiaste tú después, no se toca.", "Quitar")) return;
                    await Undo("Quitar estos mods", () => string.Join(", ", ModBatch.RestoreBatch(engine.Operations, id, batch.Key)));
                }, 190));
        }

        // Cambios sueltos: reparaciones, cambio de la DLL de ILION, DLSS 4 local…
        foreach (var op in ops.Where(o => o.Batch is null).OrderByDescending(o => o.When))
        {
            var title = op.Kind switch
            {
                "Reparar base" => $"Reparación de la base ({op.Entries.Length} {(op.Entries.Length == 1 ? "archivo" : "archivos")})",
                "DLL base" => "Cambio de la DLL de ILION",
                "DLSS 4 local" => "DLSS 4 Modelo K (instalación local)",
                ModBatch.Kind => $"Mod: {op.Label}",
                _ => op.Label,
            };
            var why = engine.Operations.CanRestore(op);
            Row($"{title} · {op.When:dd/MM/yyyy HH:mm}", op.Kind == "DLL base" ? op.Label : null,
                why is null ? null : "Antes hay que deshacer un cambio posterior sobre los mismos archivos.",
                Key("Deshacer", async () =>
                {
                    if (!Confirm.Ask(this, "Deshacer", [("Juego", g.Name), ("Cambio", title)], "Vuelve lo que había justo antes de este cambio. Lo demás se queda como está.", "Deshacer")) return;
                    await Undo("Deshacer", () => { engine.Operations.Restore(op); return title; });
                }, 150));
        }

        // ILION en sí: el motor y su base
        if (install is not null)
        {
            var bin = Path.Combine(install.Directory, install.Proxy);
            var version = File.Exists(bin) ? OptiScalerInstaller.PublicVersion(bin) ?? install.Version : install.Version;
            var key = Key("Desinstalar ILION", async () =>
            {
                if (!Confirm.Ask(this, "Desinstalar ILION", [("Juego", g.Name), ("Archivos", install.Files.Length.ToString())],
                    "Se retira solo lo que instaló Setup y no ha cambiado; tu OptiScaler.ini se conserva. Queda una copia por si quieres volver atrás.", "Desinstalar", danger: true)) return;
                await Undo("Desinstalar ILION", () => $"ILION desinstalado ({engine.Installer.Uninstall(id, keepIni: true).Length} archivos retirados)");
            }, 200);
            key.Style = Ui.S("Btn.Danger");
            var sep = new Border { BorderBrush = Ui.B("B.Line"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 4, 0, 10) };
            box.Children.Add(sep);
            Row($"ILION {version} (instalado el {install.When:dd/MM/yyyy})", $"El motor y su base: {install.Files.Length} archivos, cargado como {install.Proxy}.", null, key);
        }
        return box;
    }

    // ---------------------------------------------------------------- Registro
    private void Log(string line)
    {
        try { File.AppendAllText(Path.Combine(data, "ilion-setup.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); } catch (IOException) { }
    }

    /// <summary>Error con todo su detalle (tipo, mensaje y pila) en ilion-setup.log: lo que hace falta para diagnosticar un fallo de un usuario.</summary>
    private void LogError(string action, Exception e) => Log($"ERROR {action}: {e}");
}
