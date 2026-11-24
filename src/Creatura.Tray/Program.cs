using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Creatura.Operator;

// creatura-tray (issue #53): the Creatura emblem in the menu bar or system tray. Click opens the console's page; the
// badge counts what is waiting; the menu lists the cells, syncs now and quits. See TrayProgram.Usage.
var model = TrayProgram.Model(args, Console.Error);
if (model is null) return 2;
TrayApp.Model = model;
return AppBuilder.Configure<TrayApp>().UsePlatformDetect().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

/// <summary>The application: no window, one tray icon, its menu rebuilt from <see cref="TrayModel.Refresh"/> every 30 seconds.</summary>
internal sealed class TrayApp : Application
{
    public static TrayModel Model { get; set; } = null!;

    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);
    private TrayIcon _icon = null!;
    private Bitmap _emblem = null!;
    private string? _note;

    public override void OnFrameworkInitializationCompleted()
    {
        using (var png = typeof(TrayApp).Assembly.GetManifestResourceStream("icon.png")!) _emblem = new Bitmap(png);
        _icon = new TrayIcon { ToolTipText = "Creatura", Menu = new NativeMenu(), IsVisible = true };
        _icon.Clicked += (_, _) => Open();
        TrayIcon.SetIcons(this, [_icon]);
        Update();
        DispatcherTimer.Run(() =>
        {
            Update();
            return true;
        }, Every);
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Reads the Fleet and the hub off the UI thread, then redraws the icon and the menu.</summary>
    private void Update() => Task.Run(() => Model.Refresh()).ContinueWith(t =>
    {
        if (t.IsCompletedSuccessfully) Dispatcher.UIThread.Post(() => Draw(t.Result));
    }, TaskScheduler.Default);

    private void Draw(TrayState state)
    {
        _icon.Icon = new WindowIcon(Badged(state.Badge, state.Dot, state.Working));
        _icon.ToolTipText = state.Tooltip;
        // The same menu, its items replaced: macOS refuses another menu on an icon that has one (it ended the program).
        var menu = _icon.Menu!;
        menu.Items.Clear();
        foreach (var program in state.Programs)
        {
            var inside = new NativeMenu();
            foreach (var line in program.Lines) inside.Add(Item(line, null));
            if (program.Link is { } link)
            {
                if (program.Lines.Count > 0) inside.Add(new NativeMenuItemSeparator());
                inside.Add(Item($"Open {program.Name}", () => Task.Run(() => Model.Open(link))));
            }

            menu.Add(Under(TrayMenu.Title(program), inside));
        }

        // Creatura's own part is one line: its cells, hub and errors are inside it.
        var own = new NativeMenu();
        own.Add(Item("Open Creatura", Open));
        own.Add(new NativeMenuItemSeparator());
        foreach (var line in TrayMenu.Creatura(state)) own.Add(Item(line, null));
        if (_note is not null) own.Add(Item(TrayMenu.Short(_note), null));
        own.Add(new NativeMenuItemSeparator());
        own.Add(Item("Sync now", SyncNow));
        menu.Add(Under(TrayMenu.CreaturaTitle(state), own));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item("Quit", Quit));
    }

    private static NativeMenuItem Under(string header, NativeMenu inside) =>
        inside.Items.Count > 0 ? new NativeMenuItem(header) { Menu = inside } : Item(header, null);

    private static NativeMenuItem Item(string header, Action? click)
    {
        var item = new NativeMenuItem(header) { IsEnabled = click is not null };
        if (click is not null) item.Click += (_, _) => click();
        return item;
    }

    private void Open() => Task.Run(() => Model.OpenAsync()).ContinueWith(t =>
    {
        _note = t.IsCompletedSuccessfully ? t.Result : "could not open the page: " + t.Exception?.GetBaseException().Message;
        Update();
    }, TaskScheduler.Default);

    private void SyncNow() => Task.Run(Model.SyncNow).ContinueWith(t =>
    {
        _note = t.IsCompletedSuccessfully ? t.Result : "sync failed: " + t.Exception?.GetBaseException().Message;
        Update();
    }, TaskScheduler.Default);

    private void Quit() => Task.Run(async () => await Model.DisposeAsync()).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
    {
        _icon.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }), TaskScheduler.Default);

    /// <summary>The emblem with a red count in its corner when something waits, an amber dot when the hub or a program needs a look, and a green one while a program works.</summary>
    private Bitmap Badged(int count, bool warning, bool working)
    {
        const int size = 64;
        var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawImage(_emblem, new Rect(0, 0, size, size));
            if (warning) context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xF5, 0xA6, 0x23)), new Pen(Brushes.White, 2), new Point(12, 12), 10, 10);
            if (working) context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B)), new Pen(Brushes.White, 2), new Point(12, size - 12), 10, 10);
            if (count > 0)
            {
                var text = count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var radius = text.Length > 1 ? 20 : 16;
                context.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xD9, 0x30, 0x25)), new Pen(Brushes.White, 2), new Point(size - radius, size - radius), radius, radius);
                var label = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold), text.Length > 2 ? 14 : 22, Brushes.White);
                context.DrawText(label, new Point(size - radius - label.Width / 2, size - radius - label.Height / 2));
            }
        }

        return bitmap;
    }
}
