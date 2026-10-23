using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HolyScreen.Core;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.History;

/// <summary>What the history window can ask for.</summary>
internal sealed class HistoryActions
{
    public required Action<HistoryEntry> Copy { get; init; }
    public required Action<HistoryEntry> Edit { get; init; }
    public required Action<HistoryEntry> Pin { get; init; }
    public required Action<HistoryEntry> Delete { get; init; }
    public required Action OpenFolder { get; init; }
    public required Action ClearAll { get; init; }
}

/// <summary>The recent captures as a grid of cards: click to copy again; edit, pin or delete on hover.</summary>
internal sealed class HistoryWindow : ChromeWindow
{
    private readonly HistoryService _history;
    private readonly HistoryActions _actions;
    private readonly WrapPanel _cards = new() { Margin = new Thickness(12, 0, 4, 0) };
    private readonly TextBlock _summary;
    private readonly TextBlock _empty;

    public HistoryWindow(HistoryService history, HistoryActions actions)
    {
        _history = history;
        _actions = actions;
        Title = "History";
        Width = 860;
        Height = 600;

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _cards, Margin = new Thickness(0, 0, 6, 0) };
        layout.Children.Add(scroller);

        _empty = new TextBlock
        {
            Text = "Nothing here yet. Captures you copy, save or pin show up here.",
            Style = (Style)FindResource("CaptionText"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        layout.Children.Add(_empty);

        var bar = new DockPanel { Margin = new Thickness(18, 10, 18, 16), LastChildFill = false };
        Grid.SetRow(bar, 1);
        _summary = new TextBlock { Style = (Style)FindResource("CaptionText"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(_summary, Dock.Left);
        bar.Children.Add(_summary);

        var clear = new Button { Content = "Clear all", Style = (Style)FindResource("DangerButton"), Margin = new Thickness(10, 0, 0, 0) };
        clear.Click += (_, _) => _actions.ClearAll();
        DockPanel.SetDock(clear, Dock.Right);
        bar.Children.Add(clear);
        var folder = new Button { Content = "Open folder", Style = (Style)FindResource("GhostButton") };
        folder.Click += (_, _) => _actions.OpenFolder();
        DockPanel.SetDock(folder, Dock.Right);
        bar.Children.Add(folder);
        layout.Children.Add(bar);

        Content = layout;
        _history.Changed += OnHistoryChanged;
        Closed += (_, _) => _history.Changed -= OnHistoryChanged;
        Refresh();
    }

    private void OnHistoryChanged() => Dispatcher.BeginInvoke(new Action(Refresh));

    public void Refresh()
    {
        _cards.Children.Clear();
        IReadOnlyList<HistoryEntry> entries = _history.Load();
        int index = 0;
        foreach (HistoryEntry entry in entries)
        {
            FrameworkElement card = BuildCard(entry);
            _cards.Children.Add(card);
            if (SystemInfo.AnimationsEnabled)
            {
                card.Opacity = 0;
                card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(20 * Math.Min(index++, 12)) });
            }
        }

        _empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _summary.Text = entries.Count == 1 ? "1 capture" : $"{entries.Count} captures";
    }

    private FrameworkElement BuildCard(HistoryEntry entry)
    {
        var thumbnail = new Image { Stretch = Stretch.Uniform, Height = 132, Margin = new Thickness(0, 0, 0, 8) };
        try
        {
            thumbnail.Source = HistoryService.LoadBitmap(entry.ThumbnailPath);
        }
        catch (Exception ex)
        {
            Log.Warn("A history thumbnail could not be loaded", ex);
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal };
        tools.Children.Add(ToolButton(Icons.Copy, "Copy", () => _actions.Copy(entry)));
        tools.Children.Add(ToolButton(Icons.Pencil, "Edit again", () => _actions.Edit(entry)));
        tools.Children.Add(ToolButton(Icons.Pin, "Pin to screen", () => _actions.Pin(entry)));
        tools.Children.Add(ToolButton(Icons.Trash, "Delete", () => _actions.Delete(entry)));

        var toolBar = new Border
        {
            Background = (Brush)FindResource("SurfaceOverlayBrush"),
            BorderBrush = (Brush)FindResource("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 12),
            Child = tools,
            Opacity = 0,
        };

        var picture = new Grid { Cursor = Cursors.Hand, Background = Brushes.Transparent };
        picture.Children.Add(thumbnail);
        picture.Children.Add(toolBar);
        picture.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is not FrameworkElement source || source.TemplatedParent is not Button) _actions.Copy(entry);
        };

        var caption = new DockPanel { LastChildFill = false };
        var time = new TextBlock { Text = Describe(entry.CapturedAt), FontSize = 12 };
        DockPanel.SetDock(time, Dock.Left);
        caption.Children.Add(time);
        var size = new TextBlock
        {
            Text = entry.AnnotationCount > 0 ? $"{entry.Region.Width}×{entry.Region.Height} · ✎ {entry.AnnotationCount}" : $"{entry.Region.Width}×{entry.Region.Height}",
            Style = (Style)FindResource("CaptionText"),
        };
        DockPanel.SetDock(size, Dock.Right);
        caption.Children.Add(size);

        var stack = new StackPanel();
        stack.Children.Add(picture);
        stack.Children.Add(caption);

        var card = new Border
        {
            Width = 254,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(10, 10, 10, 10),
            CornerRadius = new CornerRadius(12),
            Background = (Brush)FindResource("SurfaceRaisedBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            Child = stack,
        };

        // Hover: lift the border and reveal the quick actions.
        card.MouseEnter += (_, _) =>
        {
            card.BorderBrush = (Brush)FindResource("BorderFocusBrush");
            toolBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
        };
        card.MouseLeave += (_, _) =>
        {
            card.BorderBrush = (Brush)FindResource("BorderBrush");
            toolBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)));
        };
        return card;
    }

    private Button ToolButton(string icon, string tip, Action action)
    {
        var button = new Button
        {
            Style = (Style)FindResource("PanelButton"),
            Width = 30,
            Height = 30,
            ToolTip = tip,
            Content = new Path
            {
                Data = Icons.Get(icon),
                Width = 15,
                Height = 15,
                Stretch = Stretch.Uniform,
                Stroke = (Brush)FindResource("PrimaryTextBrush"),
                StrokeThickness = 1.8,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            },
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            action();
        };
        return button;
    }

    private static string Describe(DateTime time)
    {
        DateTime today = DateTime.Today;
        string clock = time.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (time.Date == today) return "Today " + clock;
        if (time.Date == today.AddDays(-1)) return "Yesterday " + clock;
        return time.ToString("MMM d", CultureInfo.InvariantCulture) + " " + clock;
    }
}
