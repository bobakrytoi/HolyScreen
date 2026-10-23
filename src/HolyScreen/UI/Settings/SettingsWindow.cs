using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HolyScreen.Core;
using HolyScreen.Editor;
using HolyScreen.Input;
using HolyScreen.Output;
using HolyScreen.UI.Common;
using HolyScreen.UI.Overlay;

namespace HolyScreen.UI.Settings;

/// <summary>Callbacks the settings window uses to act on the rest of the app.</summary>
internal sealed class SettingsHost
{
    /// <summary>Registers (or, with null, clears) a shortcut; returns an error message or null.</summary>
    public required Func<HotkeyAction, HotkeyGesture?, string?> ApplyHotkey { get; init; }
    public required Func<HotkeyAction, HotkeyGesture?> CurrentHotkey { get; init; }
    public required Action<bool> SuspendHotkeys { get; init; }
    public required Action ClearHistory { get; init; }
    public required Action OpenHistoryFolder { get; init; }
}

/// <summary>The settings window — tabbed, every change applies and is saved immediately.</summary>
internal sealed class SettingsWindow : ChromeWindow
{
    private readonly SettingsService _settings;
    private readonly SettingsHost _host;
    private TextBlock? _templatePreview;
    private TextBlock? _folderText;
    private int _recording; // recorders currently listening; global shortcuts stay off while > 0

    private AppSettings S => _settings.Current;

    public SettingsWindow(SettingsService settings, SettingsHost host)
    {
        _settings = settings;
        _host = host;
        Title = "Settings";
        Width = 800;
        Height = 600;

        var tabs = new TabControl();
        AddTab(tabs, "General", BuildGeneral);
        AddTab(tabs, "Shortcuts", BuildShortcuts);
        AddTab(tabs, "Capture", BuildCapture);
        AddTab(tabs, "Output", BuildOutput);
        AddTab(tabs, "Annotations", BuildAnnotations);
        AddTab(tabs, "About", BuildAbout);
        Content = tabs;
        Closed += (_, _) =>
        {
            if (_recording > 0) _host.SuspendHotkeys(false);
            _recording = 0;
        };
    }

    private void AddTab(TabControl tabs, string header, Func<UIElement> build)
    {
        try
        {
            tabs.Items.Add(new TabItem { Header = header, Content = Scroll(build()) });
        }
        catch (Exception ex)
        {
            Log.Error($"Settings tab '{header}' failed to build", ex);
            tabs.Items.Add(new TabItem { Header = header, Content = new TextBlock { Text = "This section could not be shown.", Margin = new Thickness(30) } });
        }
    }

    // ------------------------------------------------------------------ tabs

    private UIElement BuildGeneral()
    {
        var p = NewPage();
        p.Children.Add(Section("General"));
        p.Children.Add(Toggle("Launch at Windows startup", "Start HolyScreen in the tray when you sign in.",
            StartupRegistration.IsEnabled(), v =>
            {
                if (!StartupRegistration.SetEnabled(v)) MessageBox.Show(this, "The startup setting could not be changed.", "HolyScreen");
            }));
        p.Children.Add(Toggle("Animations", "Fades, springs and the shutter flash. Windows' own \"Animation effects\" switch is respected too.",
            S.AnimationsEnabled, v => Update(s => s.AnimationsEnabled = v)));
        p.Children.Add(Toggle("Flash when capturing", "A short white flash over the area that was copied or saved.",
            S.CaptureFlash, v => Update(s => s.CaptureFlash = v)));
        p.Children.Add(Toggle("Shutter sound", "A soft camera click when a capture is copied, saved or pinned.",
            S.CaptureSound, v => Update(s => s.CaptureSound = v)));
        p.Children.Add(IntSlider("Sound volume", null, 0, 100, (int)Math.Round(S.SoundVolume * 100),
            v => Update(s => s.SoundVolume = v / 100.0), v => v + "%"));

        p.Children.Add(Section("History"));
        p.Children.Add(Toggle("Remember recent captures", "Keeps the last captures (original pixels and annotations) so you can copy or edit them again. Stored only on this PC.",
            S.HistoryEnabled, v => Update(s => s.HistoryEnabled = v)));
        p.Children.Add(IntSlider("Captures to keep", null, 5, 100, Math.Max(5, S.HistoryLimit), v => Update(s => s.HistoryLimit = v)));
        p.Children.Add(ActionRow("History folder", AppPaths.HistoryDirectory, "Open", _host.OpenHistoryFolder));
        p.Children.Add(ActionRow("Clear history", "Delete every remembered capture.", "Clear", () =>
        {
            if (MessageBox.Show(this, "Delete every remembered capture?", "HolyScreen", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            {
                _host.ClearHistory();
            }
        }, danger: true));
        return p;
    }

    private UIElement BuildShortcuts()
    {
        var p = NewPage();
        p.Children.Add(Section("Global shortcuts"));
        p.Children.Add(Caption("These work from any app. Print Screen and F13–F24 may be used on their own; other keys need Ctrl, Alt or Win."));
        p.Children.Add(Recorder(HotkeyAction.CaptureRegion, "Capture region", "Freeze the screen and select an area, a window or a monitor.", HotkeyGesture.Default));
        p.Children.Add(Recorder(HotkeyAction.CaptureFullScreen, "Capture full screen", "All monitors at once — saved and copied.", null));
        p.Children.Add(Recorder(HotkeyAction.CaptureText, "Copy text (OCR)", "Select an area and copy the text in it.", null));
        p.Children.Add(Recorder(HotkeyAction.PickColor, "Pick colour", "Click any pixel to copy its colour.", null));
        p.Children.Add(Recorder(HotkeyAction.RepeatLastRegion, "Repeat last region", "Capture the previous area again, straight to the clipboard.", null));

        p.Children.Add(Section("Inside the overlay"));
        p.Children.Add(Caption("Before selecting:  F monitor under the pointer · Ctrl+A all monitors · R last region · C pick colour · O copy text · arrows move the pointer by 1 px (Shift: 10)."));
        p.Children.Add(Caption("Tools:  A arrow · R rectangle · E ellipse · L line · P pencil · M marker · T text · N step · X pixelate · B redact.  Ctrl+Z / Ctrl+Y undo/redo · Delete removes the selected annotation."));
        p.Children.Add(Caption("Enter, Ctrl+C or a double-click copy · Ctrl+S save · Ctrl+P pin · Ctrl+T copy text · Ctrl+B beautify · arrows move the area, Ctrl+arrows resize it · Esc closes.  Holding Shift with Copy/Save does the opposite of the setting on the Capture tab."));
        return p;
    }

    private UIElement BuildCapture()
    {
        var p = NewPage();
        p.Children.Add(Section("After copying or saving"));
        p.Children.Add(Toggle("Close the editor after Copy", "Off: Copy keeps the editor open and Shift+Copy closes it.",
            S.CloseAfterCopy, v => Update(s => s.CloseAfterCopy = v)));
        p.Children.Add(Toggle("Close the editor after Save", "Off: Save keeps the editor open and Shift+Save closes it.",
            S.CloseAfterSave, v => Update(s => s.CloseAfterSave = v)));
        p.Children.Add(Toggle("Copy as soon as an area is selected", "The editor stays open for annotations; copy again to update the clipboard.",
            S.AutoCopyOnSelection, v => Update(s => s.AutoCopyOnSelection = v)));

        p.Children.Add(Section("Selecting"));
        p.Children.Add(Toggle("Magnifier", "A zoomed view of the pixels under the pointer with their colour and coordinates.",
            S.ShowMagnifier, v => Update(s => s.ShowMagnifier = v)));
        return p;
    }

    private UIElement BuildOutput()
    {
        var p = NewPage();
        p.Children.Add(Section("Saving"));

        var folderRow = RowGrid();
        var folderLabel = LabelStack("Save to", null);
        _folderText = new TextBlock { Foreground = Res("SecondaryTextBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        folderLabel.Children.Add(_folderText);
        folderRow.Children.Add(folderLabel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var browse = new Button { Content = "Browse…", MinWidth = 96 };
        browse.Click += (_, _) => BrowseFolder();
        var reset = new Button { Content = "Default", Style = (Style)FindResource("GhostButton"), Margin = new Thickness(4, 0, 0, 0) };
        reset.Click += (_, _) =>
        {
            Update(s => s.SaveFolder = string.Empty);
            RefreshFolder();
        };
        buttons.Children.Add(browse);
        buttons.Children.Add(reset);
        Grid.SetColumn(buttons, 1);
        folderRow.Children.Add(buttons);
        p.Children.Add(Wrap(folderRow));
        RefreshFolder();

        p.Children.Add(Dropdown("File format", "PNG is lossless and best for screenshots; JPEG makes smaller photos.",
            new[] { "PNG", "JPEG" }, S.SaveFormat == ImageFileFormat.Jpeg ? 1 : 0,
            i =>
            {
                Update(s => s.SaveFormat = i == 1 ? ImageFileFormat.Jpeg : ImageFileFormat.Png);
                RefreshTemplatePreview();
            }));
        p.Children.Add(IntSlider("JPEG quality", null, 30, 100, S.JpegQuality, v => Update(s => s.JpegQuality = v)));

        p.Children.Add(Section("File name"));
        p.Children.Add(Caption("Text in braces is a date/time format: {yyyy-MM-dd} {HH-mm-ss}. A number is added if the name is taken."));
        var template = new TextBox { Text = S.FileNameTemplate, Margin = new Thickness(0, 8, 0, 6), FontFamily = (FontFamily)FindResource("MonoFontFamily") };
        template.TextChanged += (_, _) =>
        {
            Update(s => s.FileNameTemplate = template.Text);
            RefreshTemplatePreview();
        };
        p.Children.Add(template);
        _templatePreview = new TextBlock { Foreground = Res("AccentBrush"), FontSize = 12.5 };
        p.Children.Add(_templatePreview);
        RefreshTemplatePreview();
        return p;
    }

    private UIElement BuildAnnotations()
    {
        var p = NewPage();
        p.Children.Add(Section("Defaults for new captures"));

        var colours = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        var dots = new System.Collections.Generic.List<(uint Color, Ellipse Dot)>();
        void Refresh()
        {
            AppSettings.TryParseColor(S.AnnotationColor, out uint selected);
            foreach ((uint color, Ellipse dot) in dots)
            {
                dot.Stroke = color == selected ? Res("AccentStrongBrush") : Brushes.Transparent;
            }
        }

        foreach (uint color in OverlayPanels.PaletteColors)
        {
            uint captured = color;
            var dot = new Ellipse { Width = 26, Height = 26, StrokeThickness = 2.5, Fill = new SolidColorBrush(AnnotationRenderer.ToColor(color)) };
            var button = new Button { Style = (Style)FindResource("IconButton"), Width = 38, Height = 38, Content = dot, ToolTip = $"#{color & 0xFFFFFF:X6}" };
            button.Click += (_, _) =>
            {
                Update(s => s.AnnotationColor = $"#{captured & 0xFFFFFF:X6}");
                Refresh();
            };
            colours.Children.Add(button);
            dots.Add((color, dot));
        }

        Refresh();
        p.Children.Add(Caption("Colour"));
        p.Children.Add(colours);
        p.Children.Add(IntSlider("Stroke size", "Arrows, shapes, lines and pencil. The mouse wheel changes it while drawing.",
            1, 20, (int)Math.Round(S.AnnotationThickness), v => Update(s => s.AnnotationThickness = v)));
        p.Children.Add(IntSlider("Text size", null, 10, 72, (int)Math.Round(S.TextSize), v => Update(s => s.TextSize = v)));
        return p;
    }

    private UIElement BuildAbout()
    {
        var p = NewPage();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        p.Children.Add(new TextBlock { Text = "HolyScreen", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Res("PrimaryTextBrush") });
        p.Children.Add(new TextBlock { Text = "Version " + version, Foreground = Res("SecondaryTextBrush"), Margin = new Thickness(0, 4, 0, 0) });
        p.Children.Add(new TextBlock
        {
            Text = "Screenshots with annotations, pinning, OCR and colour picking — from the tray, with one shortcut.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Res("SecondaryTextBrush"),
            Margin = new Thickness(0, 16, 0, 0),
        });
        p.Children.Add(new TextBlock { Text = SystemInfo.Describe(), Foreground = Res("TertiaryTextBrush"), FontSize = 12, Margin = new Thickness(0, 14, 0, 4) });
        p.Children.Add(new TextBlock
        {
            Text = "OCR languages: " + string.Join(", ", SafeOcrLanguages()),
            Foreground = Res("TertiaryTextBrush"),
            FontSize = 12,
        });
        p.Children.Add(ActionRow("Data folder", AppPaths.DataDirectory, "Open", () => ShellActions.OpenFolder(AppPaths.DataDirectory)));
        p.Children.Add(ActionRow("Diagnostic logs", AppPaths.LogDirectory, "Open", () => ShellActions.OpenFolder(AppPaths.LogDirectory)));
        p.Children.Add(Caption("Logs contain sizes, timings and errors — never the content of your screenshots or recognised text."));
        return p;
    }

    private static string[] SafeOcrLanguages()
    {
        try
        {
            var languages = OcrService.AvailableLanguages;
            return languages.Count == 0 ? new[] { "none installed" } : System.Linq.Enumerable.ToArray(languages);
        }
        catch
        {
            return new[] { "unavailable" };
        }
    }

    // ------------------------------------------------------------------ helpers

    private FrameworkElement Recorder(HotkeyAction action, string label, string description, HotkeyGesture? defaultGesture)
    {
        var grid = RowGrid();
        grid.Children.Add(LabelStack(label, description));
        var recorder = new HotkeyRecorder(defaultGesture)
        {
            Current = _host.CurrentHotkey(action),
            TryApply = gesture => _host.ApplyHotkey(action, gesture),
            RecordingChanged = OnRecordingChanged,
            VerticalAlignment = VerticalAlignment.Center,
        };
        recorder.SetAutomationIds("Hotkey" + action);
        Grid.SetColumn(recorder, 1);
        grid.Children.Add(recorder);
        return Wrap(grid);
    }

    // Counted, not a flag: when focus jumps from one recorder to another the new one may start before the old one
    // reports that it stopped, and a flag would switch the shortcuts back on in the middle of a recording.
    private void OnRecordingChanged(bool recording)
    {
        int before = _recording;
        _recording = Math.Max(0, _recording + (recording ? 1 : -1));
        if ((before > 0) != (_recording > 0)) _host.SuspendHotkeys(_recording > 0);
    }

    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Where should screenshots be saved?",
            InitialDirectory = ScreenshotSaver.ResolveFolder(S),
        };
        if (dialog.ShowDialog(this) == true)
        {
            Update(s => s.SaveFolder = dialog.FolderName);
            RefreshFolder();
        }
    }

    private void RefreshFolder()
    {
        if (_folderText is null) return;
        string folder = ScreenshotSaver.ResolveFolder(S);
        _folderText.Text = string.IsNullOrWhiteSpace(S.SaveFolder) ? folder + "  (Windows Screenshots folder)" : folder;
    }

    private void RefreshTemplatePreview()
    {
        if (_templatePreview is null) return;
        _templatePreview.Text = "→ " + FileNameTemplate.Expand(S.FileNameTemplate, DateTime.Now) + ImageEncoding.Extension(S.SaveFormat);
    }

    private StackPanel NewPage() => new() { Margin = new Thickness(30, 20, 30, 28) };

    private static ScrollViewer Scroll(UIElement content) => new()
    {
        Content = content,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Padding = new Thickness(0, 0, 8, 0),
    };

    private TextBlock Section(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Style = (Style)FindResource("SectionLabel"),
        Margin = new Thickness(0, 16, 0, 8),
    };

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("CaptionText"),
        Margin = new Thickness(0, 2, 0, 4),
    };

    private FrameworkElement Toggle(string label, string? description, bool value, Action<bool> onChanged)
    {
        var grid = RowGrid();
        grid.Children.Add(LabelStack(label, description));
        var check = new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        check.Checked += (_, _) => onChanged(true);
        check.Unchecked += (_, _) => onChanged(false);
        Grid.SetColumn(check, 1);
        grid.Children.Add(check);
        return Wrap(grid);
    }

    private FrameworkElement Dropdown(string label, string? description, string[] options, int selectedIndex, Action<int> onChanged)
    {
        var grid = RowGrid();
        grid.Children.Add(LabelStack(label, description));
        var combo = new ComboBox { Width = 160, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (string option in options) combo.Items.Add(option);
        combo.SelectedIndex = Math.Clamp(selectedIndex, 0, options.Length - 1);
        combo.SelectionChanged += (_, _) => onChanged(combo.SelectedIndex);
        Grid.SetColumn(combo, 1);
        grid.Children.Add(combo);
        return Wrap(grid);
    }

    private FrameworkElement IntSlider(string label, string? description, int min, int max, int value, Action<int> onChanged, Func<int, string>? format = null)
    {
        var text = new TextBlock { Foreground = Res("AccentBrush"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, TextAlignment = TextAlignment.Right };
        text.Text = format?.Invoke(value) ?? value.ToString(CultureInfo.CurrentCulture);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), IsSnapToTickEnabled = true, TickFrequency = 1, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, _) =>
        {
            int v = (int)Math.Round(slider.Value);
            text.Text = format?.Invoke(v) ?? v.ToString(CultureInfo.CurrentCulture);
            onChanged(v);
        };

        var grid = RowGrid();
        grid.Children.Add(LabelStack(label, description));
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(slider);
        right.Children.Add(text);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return Wrap(grid);
    }

    private FrameworkElement ActionRow(string label, string? description, string buttonText, Action onClick, bool danger = false)
    {
        var grid = RowGrid();
        grid.Children.Add(LabelStack(label, description));
        var button = new Button
        {
            Content = buttonText,
            MinWidth = 96,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (danger) button.Style = (Style)FindResource("DangerButton");
        button.Click += (_, _) => onClick();
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return Wrap(grid);
    }

    private static Grid RowGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return grid;
    }

    private static StackPanel LabelStack(string label, string? description)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        stack.Children.Add(new TextBlock { Text = label, Foreground = Res("PrimaryTextBrush"), FontSize = 13.5 });
        if (!string.IsNullOrEmpty(description))
        {
            stack.Children.Add(new TextBlock { Text = description, Foreground = Res("SecondaryTextBrush"), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });
        }

        return stack;
    }

    private static Border Wrap(UIElement content) => new()
    {
        Child = content,
        Padding = new Thickness(0, 10, 0, 10),
        BorderBrush = Res("BorderBrush"),
        BorderThickness = new Thickness(0, 0, 0, 1),
    };

    private void Update(Action<AppSettings> change) => _settings.Update(change);

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
