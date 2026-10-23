using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.Beautify;

/// <summary>
/// Live preview of the screenshot on a backdrop with padding, rounded corners and a shadow, plus Copy / Save.
/// The chosen look is remembered for next time.
/// </summary>
internal sealed class BeautifyWindow : ChromeWindow
{
    private readonly BitmapSource _source;
    private readonly Image _preview;
    private readonly DispatcherTimer _debounce;
    private readonly Action<BeautifyOptions> _remember;
    private readonly Func<BitmapSource, bool> _copy;
    private readonly Func<BitmapSource, bool> _save;
    private readonly List<(int Index, Ellipse Ring)> _swatches = new();
    private readonly TextBlock _sizeText;
    private BeautifyOptions _options;
    private BitmapSource? _result;

    public BeautifyWindow(BitmapSource source, BeautifyOptions initial, Action<BeautifyOptions> remember,
        Func<BitmapSource, bool> copy, Func<BitmapSource, bool> save)
    {
        _source = source;
        _options = initial;
        _remember = remember;
        _copy = copy;
        _save = save;
        Title = "Beautify";
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            RenderPreview();
        };

        var layout = new Grid { Margin = new Thickness(18, 2, 18, 18) };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });

        _preview = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(14) };
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.HighQuality);
        var previewFrame = new Border
        {
            Width = 640,
            Height = 440,
            CornerRadius = new CornerRadius(10),
            Background = BeautifyRenderer.CheckerBrush(8),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 18, 0),
            Child = _preview,
        };
        layout.Children.Add(previewFrame);

        var panel = new StackPanel();
        Grid.SetColumn(panel, 1);
        layout.Children.Add(panel);

        panel.Children.Add(Section("BACKGROUND"));
        var swatches = new WrapPanel { Margin = new Thickness(0, 0, 0, 18) };
        for (int i = 0; i < BeautifyRenderer.Backgrounds.Length; i++)
        {
            int index = i;
            BeautifyBackground background = BeautifyRenderer.Backgrounds[i];
            var ring = new Ellipse { Width = 34, Height = 34, StrokeThickness = 2, Stroke = Brushes.Transparent };
            var dot = new Ellipse { Width = 26, Height = 26, Fill = BeautifyRenderer.Swatch(background) };
            var cell = new Grid { Width = 40, Height = 40 };
            cell.Children.Add(ring);
            cell.Children.Add(dot);
            var button = new Button
            {
                Style = (Style)FindResource("PanelButton"),
                Width = 42,
                Height = 42,
                Content = cell,
                ToolTip = background.Name,
                Cursor = Cursors.Hand,
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Background" + background.Name);
            button.Click += (_, _) => Update(_options with { Background = index });
            swatches.Children.Add(button);
            _swatches.Add((index, ring));
        }

        panel.Children.Add(swatches);

        panel.Children.Add(Section("PADDING"));
        panel.Children.Add(MakeSlider(0, 200, initial.Padding, v => Update(_options with { Padding = (int)v })));
        panel.Children.Add(Section("CORNERS"));
        panel.Children.Add(MakeSlider(0, 48, initial.Radius, v => Update(_options with { Radius = (int)v })));

        var shadow = new CheckBox { Content = "Drop shadow", IsChecked = initial.Shadow, Margin = new Thickness(0, 4, 0, 18) };
        shadow.Click += (_, _) => Update(_options with { Shadow = shadow.IsChecked == true });
        panel.Children.Add(shadow);

        _sizeText = new TextBlock { Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(_sizeText);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var copyButton = new Button { Content = "Copy", Style = (Style)FindResource("PrimaryButton"), MinWidth = 104, Margin = new Thickness(0, 0, 10, 0) };
        System.Windows.Automation.AutomationProperties.SetAutomationId(copyButton, "BeautifyCopy");
        copyButton.Click += (_, _) => Finish(_copy);
        var saveButton = new Button { Content = "Save", MinWidth = 104 };
        System.Windows.Automation.AutomationProperties.SetAutomationId(saveButton, "BeautifySave");
        saveButton.Click += (_, _) => Finish(_save);
        actions.Children.Add(copyButton);
        actions.Children.Add(saveButton);
        panel.Children.Add(actions);

        Content = layout;
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C) { Finish(_copy); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S) { Finish(_save); e.Handled = true; }
        };

        RenderPreview();
    }

    public BitmapSource? Result => _result;

    private void Update(BeautifyOptions options)
    {
        _options = options;
        _remember(options);
        _debounce.Stop();
        _debounce.Start();
    }

    private void RenderPreview()
    {
        _result = BeautifyRenderer.Render(_source, _options);
        _preview.Source = _result;
        _sizeText.Text = $"{_result.PixelWidth} × {_result.PixelHeight} px";
        foreach ((int index, Ellipse ring) in _swatches)
        {
            ring.Stroke = index == _options.Background ? (Brush)FindResource("AccentStrongBrush") : Brushes.Transparent;
        }
    }

    private void Finish(Func<BitmapSource, bool> action)
    {
        _debounce.Stop();
        RenderPreview();
        if (_result is not null && action(_result)) Close();
    }

    private TextBlock Section(string text) =>
        new() { Text = text, Style = (Style)FindResource("SectionLabel") };

    private static Slider MakeSlider(double min, double max, double value, Action<double> changed)
    {
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Margin = new Thickness(0, 0, 0, 16), IsSnapToTickEnabled = true, TickFrequency = 1 };
        slider.ValueChanged += (_, e) => changed(e.NewValue);
        return slider;
    }
}
