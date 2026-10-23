using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HolyScreen.Capture;
using HolyScreen.Editor;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.Overlay;

/// <summary>What the panels can ask the session to do.</summary>
internal sealed class PanelActions
{
    /// <summary>Copy or Save; the flag is whether Shift was held (it inverts "close after copy/save").</summary>
    public required Action<OverlayCommand, bool> Command { get; init; }
    public required Action Close { get; init; }
    public required Action Undo { get; init; }
    public required Action Redo { get; init; }
    public required Action<ToolKind> ToggleTool { get; init; }
    public required Action<uint> PickColor { get; init; }
    public required Action<double> PickThickness { get; init; }
    public Action? Pin { get; init; }
    public Action? Ocr { get; init; }
    public Action? Beautify { get; init; }
}

/// <summary>
/// The glass toolbars around a confirmed selection, laid out like Lightshot: actions in a row under the selection
/// (right-aligned), tools in a column to its right (bottom-aligned) — an "L" hugging the bottom-right corner — and
/// the colour/size palette sliding out next to the tools. When there is no room they flip or tuck inside.
/// </summary>
internal sealed class OverlayPanels
{
    private const double Gap = 8;
    private const double Margin = 6;

    public static readonly uint[] PaletteColors =
    {
        0xFFFF3B30, 0xFFFF9500, 0xFFFFCC00, 0xFF34C759, 0xFF30B0C7,
        0xFF0A84FF, 0xFFAF52DE, 0xFFFF2D55, 0xFFFFFFFF, 0xFF1C1C1E,
    };

    public static readonly double[] Sizes = { 2, 4, 8 };

    private static readonly (ToolKind Tool, string Icon, string Tip)[] ToolDefinitions =
    {
        (ToolKind.Arrow, Icons.Arrow, "Arrow  (A)"),
        (ToolKind.Rectangle, Icons.Rectangle, "Rectangle  (R)"),
        (ToolKind.Ellipse, Icons.Ellipse, "Ellipse  (E)"),
        (ToolKind.Line, Icons.Line, "Line  (L)"),
        (ToolKind.Pencil, Icons.Pencil, "Pencil  (P)"),
        (ToolKind.Marker, Icons.Marker, "Marker  (M)"),
        (ToolKind.Text, Icons.Text, "Text  (T)"),
        (ToolKind.Step, Icons.Step, "Numbered step  (N)"),
        (ToolKind.Pixelate, Icons.Pixelate, "Pixelate  (X)"),
        (ToolKind.Redact, Icons.EyeOff, "Redact  (B)"),
    };

    private readonly Dictionary<OverlayWindow, BitmapSource> _backdrops = new();
    private readonly Dictionary<ToolKind, ToggleButton> _toolButtons = new();
    private readonly List<(uint Color, Ellipse Dot)> _colorButtons = new();
    private readonly List<(double Size, Ellipse Dot)> _sizeButtons = new();
    private readonly Ellipse _swatch;
    private readonly Button _undo;
    private readonly Button _redo;
    private OverlayWindow? _host;
    private bool _paletteOpen;

    public OverlayPanels(PanelActions actions)
    {
        Tools = new GlassPanel(Orientation.Vertical, "ToolsPanel") { TooltipPlacement = PlacementMode.Left };
        foreach ((ToolKind tool, string icon, string tip) in ToolDefinitions)
        {
            ToolKind captured = tool;
            _toolButtons[tool] = Tools.AddToggle("Tool" + tool, icon, tip, () => actions.ToggleTool(captured));
        }

        Tools.AddSeparator();
        _swatch = new Ellipse { Width = 18, Height = 18, Stroke = Brushes.White, StrokeThickness = 2 };
        Button swatchButton = Tools.AddContentButton("ColorSwatch", _swatch, "Colour and size  (mouse wheel)", TogglePalette);
        ToolTipService.SetPlacement(swatchButton, PlacementMode.Left);

        Actions = new GlassPanel(Orientation.Horizontal, "ActionsPanel") { TooltipPlacement = PlacementMode.Bottom };
        _undo = Actions.AddButton("Undo", Icons.Undo, "Undo  (Ctrl+Z)", actions.Undo);
        _redo = Actions.AddButton("Redo", Icons.Redo, "Redo  (Ctrl+Y)", actions.Redo);
        Actions.AddSeparator();
        if (actions.Pin is not null) Actions.AddButton("Pin", Icons.Pin, "Pin to screen  (Ctrl+P)", actions.Pin);
        if (actions.Ocr is not null) Actions.AddButton("Ocr", Icons.ScanText, "Copy text (OCR)  (Ctrl+T)", actions.Ocr);
        if (actions.Beautify is not null) Actions.AddButton("Beautify", Icons.Sparkles, "Beautify  (Ctrl+B)", actions.Beautify);
        if (actions.Pin is not null || actions.Ocr is not null || actions.Beautify is not null) Actions.AddSeparator();
        Actions.AddButton("Save", Icons.Download, "Save  (Ctrl+S)", () => actions.Command(OverlayCommand.Save, ShiftHeld()));
        Actions.AddButton("Copy", Icons.Copy, "Copy  (Ctrl+C)", () => actions.Command(OverlayCommand.Copy, ShiftHeld()));
        Actions.AddSeparator();
        Actions.AddButton("Close", Icons.Close, "Close  (Esc)", actions.Close);

        Palette = new GlassPanel(Orientation.Horizontal, "PalettePanel") { TooltipPlacement = PlacementMode.Top };
        foreach (uint color in PaletteColors)
        {
            uint captured = color;
            var dot = new Ellipse
            {
                Width = 18,
                Height = 18,
                Fill = OverlayTheme.Frozen(AnnotationRenderer.ToColor(color)),
                Stroke = OverlayTheme.Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = 1,
            };
            Palette.AddContentButton($"Color{color:X8}", dot, $"#{color & 0xFFFFFF:X6}", () => actions.PickColor(captured), width: 30);
            _colorButtons.Add((color, dot));
        }

        Palette.AddSeparator();
        foreach (double size in Sizes)
        {
            double captured = size;
            var dot = new Ellipse { Width = 3 + size * 1.3, Height = 3 + size * 1.3, Fill = Brushes.White };
            Palette.AddContentButton($"Size{size}", dot, $"Size {size}", () => actions.PickThickness(captured), width: 30);
            _sizeButtons.Add((size, dot));
        }
    }

    public GlassPanel Actions { get; }

    public GlassPanel Tools { get; }

    public GlassPanel Palette { get; }

    public bool PaletteOpen => _paletteOpen;

    /// <summary>Reflects the editor state: active tool, current colour/size, undo/redo availability.</summary>
    public void Sync(AnnotationEditor editor)
    {
        foreach ((ToolKind tool, ToggleButton button) in _toolButtons) button.IsChecked = editor.Tool == tool;
        Color current = AnnotationRenderer.ToColor(editor.Color);
        _swatch.Fill = OverlayTheme.Frozen(current);
        _undo.IsEnabled = editor.Document.CanUndo;
        _redo.IsEnabled = editor.Document.CanRedo;

        foreach ((uint color, Ellipse dot) in _colorButtons)
        {
            bool selected = color == editor.Color;
            dot.Stroke = selected ? Brushes.White : OverlayTheme.Frozen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF));
            dot.StrokeThickness = selected ? 2.5 : 1;
        }

        foreach ((double size, Ellipse dot) in _sizeButtons)
        {
            dot.Fill = Math.Abs(size - editor.Thickness) < 0.01 ? OverlayTheme.Highlight : Brushes.White;
        }
    }

    public void TogglePalette()
    {
        _paletteOpen = !_paletteOpen;
        if (!_paletteOpen) Palette.Hide();
        Relayout?.Invoke();
    }

    public void ClosePalette()
    {
        if (!_paletteOpen) return;
        _paletteOpen = false;
        Palette.Hide();
    }

    /// <summary>Asks the session to lay the panels out again (e.g. after the palette was toggled).</summary>
    public Action? Relayout { get; set; }

    /// <summary>Shows the panels next to <paramref name="selection"/>, or hides them when it is null.</summary>
    public void Update(PixelRect? selection, IReadOnlyList<OverlayWindow> windows, IReadOnlyList<MonitorInfo> monitors, bool animate)
    {
        if (selection is not PixelRect sel || windows.Count == 0)
        {
            Actions.Hide();
            Tools.Hide();
            Palette.Hide();
            return;
        }

        MonitorInfo monitor = DisplayLayout.FromPoint(monitors, sel.Right - 1, sel.Bottom - 1);
        OverlayWindow host = windows.FirstOrDefault(w => w.Monitor.Bounds == monitor.Bounds) ?? windows[0];
        MoveTo(host);

        Rect s = host.ToLocal(sel);
        Size area = host.RenderSize;
        Size actions = Measure(Actions);
        Size tools = Measure(Tools);

        // Actions: under the selection, right edges aligned; else above; else inside the bottom edge.
        double ax = s.Right - actions.Width;
        double ay = s.Bottom + Gap;
        if (ay + actions.Height > area.Height - Margin)
        {
            ay = s.Top - Gap - actions.Height;
            if (ay < Margin) ay = s.Bottom - Gap - actions.Height;
        }

        var actionsRect = new Rect(Clamp(ax, Margin, area.Width - actions.Width - Margin),
            Clamp(ay, Margin, area.Height - actions.Height - Margin), actions.Width, actions.Height);

        // Tools: right of the selection, bottom edges aligned; else left; else inside the right edge.
        double tx = s.Right + Gap;
        bool toolsOnRight = true;
        if (tx + tools.Width > area.Width - Margin)
        {
            toolsOnRight = false;
            tx = s.Left - Gap - tools.Width;
            if (tx < Margin) tx = s.Right - Gap - tools.Width;
        }

        double ty = s.Bottom - tools.Height;
        var toolsRect = new Rect(Clamp(tx, Margin, area.Width - tools.Width - Margin),
            Clamp(ty, Margin, area.Height - tools.Height - Margin), tools.Width, tools.Height);
        if (toolsRect.IntersectsWith(actionsRect))
        {
            // Pushed into each other near a corner: stack the tools above the actions, or beside them.
            toolsRect.Y = Clamp(actionsRect.Top - Gap - tools.Height, Margin, area.Height - tools.Height - Margin);
            if (toolsRect.IntersectsWith(actionsRect))
            {
                toolsRect.X = Clamp(actionsRect.Left - Gap - tools.Width, Margin, area.Width - tools.Width - Margin);
            }
        }

        Place(host, Actions, actionsRect.TopLeft, animate);
        Place(host, Tools, toolsRect.TopLeft, animate);

        if (_paletteOpen)
        {
            // The palette slides out beside the tool column, level with the colour swatch at its bottom.
            Size palette = Measure(Palette);
            double px = toolsOnRight ? toolsRect.Right + Gap : toolsRect.Left - Gap - palette.Width;
            if (px + palette.Width > area.Width - Margin || px < Margin) px = toolsRect.Left - Gap - palette.Width;
            double py = toolsRect.Bottom - palette.Height;
            Place(host, Palette, new Point(Clamp(px, Margin, area.Width - palette.Width - Margin),
                Clamp(py, Margin, area.Height - palette.Height - Margin)), animate);
        }
        else
        {
            Palette.Hide();
        }
    }

    public void Hide()
    {
        Actions.Hide();
        Tools.Hide();
        Palette.Hide();
    }

    private void Place(OverlayWindow host, GlassPanel panel, Point topLeft, bool animate)
    {
        topLeft = new Point(Math.Round(topLeft.X), Math.Round(topLeft.Y));
        Canvas.SetLeft(panel, topLeft.X);
        Canvas.SetTop(panel, topLeft.Y);
        panel.UpdateBackdrop(Backdrop(host), topLeft, host.Scale);
        panel.Show(animate);
    }

    private void MoveTo(OverlayWindow host)
    {
        if (ReferenceEquals(_host, host)) return;
        foreach (GlassPanel panel in new[] { Actions, Tools, Palette })
        {
            _host?.PanelLayer.Children.Remove(panel);
            panel.Hide();
            host.PanelLayer.Children.Add(panel);
        }

        _host = host;
    }

    private BitmapSource? Backdrop(OverlayWindow host)
    {
        if (_backdrops.TryGetValue(host, out BitmapSource? cached)) return cached;
        if (host.Bitmap is not BitmapSource bitmap) return null;
        BitmapSource blurred = GlassBackdrop.Create(bitmap);
        _backdrops[host] = blurred;
        return blurred;
    }

    private static Size Measure(GlassPanel panel)
    {
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return panel.DesiredSize;
    }

    private static double Clamp(double value, double min, double max) => max < min ? min : Math.Clamp(value, min, max);

    private static bool ShiftHeld() => Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
}
