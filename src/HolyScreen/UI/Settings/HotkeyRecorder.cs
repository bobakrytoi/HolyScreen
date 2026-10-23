using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HolyScreen.Input;

namespace HolyScreen.UI.Settings;

/// <summary>
/// Captures a keyboard shortcut: click "Change", press the combination, then Save or Cancel. Applying is delegated
/// to <see cref="TryApply"/>, which returns an error message (shown inline) or null on success — a shortcut that
/// cannot be registered never silently replaces the working one. Optional shortcuts can be cleared.
/// </summary>
internal sealed class HotkeyRecorder : UserControl
{
    private readonly TextBlock _display;
    private readonly Border _box;
    private readonly Button _changeButton;
    private readonly Button _saveButton;
    private readonly Button _cancelButton;
    private readonly Button _resetButton;
    private readonly TextBlock _error;
    private readonly StackPanel _editActions;
    private readonly StackPanel _idleActions;

    private HotkeyGesture? _current;
    private HotkeyGesture _pending;
    private bool _recording;
    private bool _hasPending;

    /// <param name="defaultGesture">What "Reset" restores; null makes it a "Clear" button (optional shortcut).</param>
    public HotkeyRecorder(HotkeyGesture? defaultGesture)
    {
        DefaultGesture = defaultGesture;
        Focusable = false;

        _display = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _box = new Border
        {
            Background = Res("InputBrush"),
            BorderBrush = Res("BorderStrongBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            MinWidth = 150,
            Child = _display,
            Focusable = true,
        };
        _box.PreviewKeyDown += OnRecordKey;
        _box.LostKeyboardFocus += OnBoxLostFocus;

        _changeButton = new Button { Content = "Change", Margin = new Thickness(8, 0, 0, 0), MinWidth = 84 };
        _changeButton.Click += (_, _) => BeginRecording();
        _resetButton = new Button
        {
            Content = defaultGesture is null ? "Clear" : "Reset",
            Style = (Style)Application.Current.FindResource("GhostButton"),
            Margin = new Thickness(4, 0, 0, 0),
        };
        _resetButton.Click += (_, _) => ResetOrClear();

        _saveButton = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0), MinWidth = 72 };
        _saveButton.Click += (_, _) => Commit();
        _cancelButton = new Button { Content = "Cancel", Margin = new Thickness(4, 0, 0, 0) };
        _cancelButton.Click += (_, _) => CancelRecording();

        _editActions = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        _editActions.Children.Add(_saveButton);
        _editActions.Children.Add(_cancelButton);

        _idleActions = new StackPanel { Orientation = Orientation.Horizontal };
        _idleActions.Children.Add(_changeButton);
        _idleActions.Children.Add(_resetButton);

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(_box);
        row.Children.Add(_idleActions);
        row.Children.Add(_editActions);

        _error = new TextBlock
        {
            Foreground = Res("DangerBrush"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 6, 0, 0),
            MaxWidth = 380,
            HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed,
        };

        var container = new StackPanel();
        container.Children.Add(row);
        container.Children.Add(_error);
        Content = container;
        Current = null;
    }

    public HotkeyGesture? DefaultGesture { get; }

    /// <summary>Gives the parts UI Automation ids ("&lt;id&gt;.Change", ".Save", ".Cancel", ".Reset", ".Value", ".Error").</summary>
    public void SetAutomationIds(string id)
    {
        AutomationProperties.SetAutomationId(this, id);
        AutomationProperties.SetAutomationId(_changeButton, id + ".Change");
        AutomationProperties.SetAutomationId(_saveButton, id + ".Save");
        AutomationProperties.SetAutomationId(_cancelButton, id + ".Cancel");
        AutomationProperties.SetAutomationId(_resetButton, id + ".Reset");
        AutomationProperties.SetAutomationId(_display, id + ".Value");
        AutomationProperties.SetAutomationId(_error, id + ".Error");
    }

    /// <summary>Applies a gesture (or clears with null); returns an error message or null on success.</summary>
    public Func<HotkeyGesture?, string?>? TryApply { get; set; }

    /// <summary>Called with true while recording (the app suspends its global shortcuts so they can be pressed here).</summary>
    public Action<bool>? RecordingChanged { get; set; }

    public HotkeyGesture? Current
    {
        get => _current;
        set
        {
            _current = value;
            ShowCurrent();
        }
    }

    private void ShowCurrent()
    {
        _display.Text = _current?.ToDisplayString() ?? "Not set";
        _display.Foreground = Res(_current is null ? "TertiaryTextBrush" : "PrimaryTextBrush");
    }

    private void BeginRecording()
    {
        _recording = true;
        _hasPending = false;
        RecordingChanged?.Invoke(true);
        _error.Visibility = Visibility.Collapsed;
        _display.Text = "Press a shortcut…";
        _display.Foreground = Res("AccentBrush");
        _idleActions.Visibility = Visibility.Collapsed;
        _editActions.Visibility = Visibility.Visible;
        _saveButton.IsEnabled = false;
        Keyboard.Focus(_box);
    }

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Moving focus to our own Save/Cancel must not cancel (or Save would revert before its click is handled).
        if (!_recording) return;
        if (e.NewFocus is DependencyObject target && IsWithinRecorder(target)) return;
        CancelRecording();
    }

    private bool IsWithinRecorder(DependencyObject node)
    {
        for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this)) return true;
        }

        return false;
    }

    private void OnRecordKey(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            CancelRecording();
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        int vk = KeyInterop.VirtualKeyFromKey(key);

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= HotkeyModifiers.Control;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= HotkeyModifiers.Alt;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) modifiers |= HotkeyModifiers.Win;

        if (HotkeyGesture.IsModifierKey(vk))
        {
            var preview = new HotkeyGesture(modifiers, 0);
            _display.Text = modifiers == HotkeyModifiers.None ? "Press a shortcut…" : preview + "…";
            _saveButton.IsEnabled = false;
            return;
        }

        _pending = new HotkeyGesture(modifiers, vk);
        _hasPending = true;
        _display.Text = _pending.ToDisplayString();

        string? problem = _pending.Validate();
        if (problem is not null)
        {
            ShowError(problem);
            _saveButton.IsEnabled = false;
        }
        else
        {
            _error.Visibility = Visibility.Collapsed;
            _saveButton.IsEnabled = true;
        }
    }

    private void Commit()
    {
        if (!_hasPending) return;
        EndRecording();
        string? error = TryApply?.Invoke(_pending);
        if (error is not null)
        {
            ShowCurrent();
            ShowError(error);
            return;
        }

        Current = _pending;
    }

    private void ResetOrClear()
    {
        string? error = TryApply?.Invoke(DefaultGesture);
        if (error is not null)
        {
            ShowError(error);
            return;
        }

        Current = DefaultGesture;
        _error.Visibility = Visibility.Collapsed;
    }

    private void CancelRecording()
    {
        EndRecording();
        ShowCurrent();
    }

    private void EndRecording()
    {
        if (_recording) RecordingChanged?.Invoke(false);
        _recording = false;
        _hasPending = false;
        _editActions.Visibility = Visibility.Collapsed;
        _idleActions.Visibility = Visibility.Visible;
    }

    private void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
    }

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
