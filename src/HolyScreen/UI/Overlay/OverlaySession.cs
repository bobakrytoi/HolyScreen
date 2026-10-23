using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.Editor;
using HolyScreen.Interop;
using HolyScreen.UI.Common;

namespace HolyScreen.UI.Overlay;

internal enum OverlayMode
{
    /// <summary>No selection yet: crosshair, magnifier and the window under the cursor highlighted.</summary>
    Hover,

    /// <summary>Button down, not moved past the drag threshold yet (a click selects the highlighted window).</summary>
    Pressing,

    /// <summary>Dragging out a new selection.</summary>
    Creating,

    /// <summary>A selection exists; handles and panels are shown.</summary>
    Selected,
    Moving,
    Resizing,

    /// <summary>The annotation editor owns the current drag (drawing, moving or reshaping an object).</summary>
    Annotating,
    Closing,
}

internal enum OverlayCommand
{
    Copy,
    Save,
    Pin,
    Ocr,
    Beautify,
    PickColor,
}

/// <summary>What the overlay was opened for.</summary>
internal enum OverlayIntent
{
    /// <summary>Select, annotate, copy/save/pin/…</summary>
    Capture,

    /// <summary>One click copies the colour under the cursor (no veil, so colours look true).</summary>
    PickColor,

    /// <summary>The selection is read with OCR and the text copied.</summary>
    ExtractText,
}

/// <summary>
/// One capture: the frozen frame, a window per monitor, the selection state machine and the annotation editor.
/// All coordinates are virtual-screen pixels; the windows convert to their own DIPs when they draw.
/// </summary>
internal sealed class OverlaySession
{
    private const int CloseAnimationMs = 170;
    private const int FramesBeforeReveal = 3;

    private readonly CapturedFrame _frame;
    private readonly AppSettings _settings;
    private readonly OverlayWindowPool _pool;
    private readonly IntPtr _previousForeground;
    private readonly bool _animate;
    private readonly bool _warmUp;
    private readonly Stopwatch _sinceHotkey;
    private readonly List<OverlayWindow> _windows = new();
    private readonly DispatcherTimer _transientTimer;

    private OverlayMode _mode = OverlayMode.Hover;
    private PixelRect? _selection;
    private PixelRect? _highlight;
    private string? _highlightTitle;
    private int _cursorX;
    private int _cursorY;
    private int _pressX;
    private int _pressY;
    private PixelRect _dragStart;
    private HandleKind _dragHandle;
    private HandleKind _hoverHandle;
    private double _hoverScale = 1;
    private bool _pointerOverPanel;
    private int _pendingFirstRenders;
    private int _framesSinceShow;
    private bool _revealed;
    private DispatcherTimer? _revealFallback;
    private string _timings = string.Empty;
    private OverlayPanels? _panels;
    private AnnotationEditor? _editor;
    private OverlayWindow? _textWindow;
    private string? _transient;
    private OverlayIntent _intent;
    private PixelRect? _preselect;
    private IReadOnlyList<Annotation>? _preload;

    /// <param name="warmUp">
    /// Run the whole open path invisibly (windows stay cloaked) and close again — done once after startup so JIT,
    /// the render thread and the pooled windows are ready before the first real capture.
    /// </param>
    public OverlaySession(CapturedFrame frame, AppSettings settings, OverlayWindowPool pool, IntPtr previousForeground,
        bool animate, Stopwatch sinceHotkey, bool warmUp = false, OverlayIntent intent = OverlayIntent.Capture)
    {
        _frame = frame;
        _settings = settings;
        _pool = pool;
        _previousForeground = previousForeground;
        _animate = animate;
        _sinceHotkey = sinceHotkey;
        _warmUp = warmUp;
        _intent = intent;
        _transientTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _transientTimer.Tick += (_, _) =>
        {
            _transientTimer.Stop();
            _transient = null;
            Render();
        };
    }

    /// <summary>A command was requested for the current selection. The bool is "keep the overlay open".</summary>
    public event Action<OverlaySession, OverlayCommand, bool>? CommandRequested;

    /// <summary>All windows are gone and focus has been handed back.</summary>
    public event Action<OverlaySession>? Closed;

    public CapturedFrame Frame => _frame;
    public PixelRect? Selection => _selection;
    public OverlayMode Mode => _mode;
    public IReadOnlyList<OverlayWindow> Windows => _windows;
    public AnnotationEditor? Editor => _editor;
    public bool IsWarmUp => _warmUp;

    /// <summary>Commands beyond copy/save that the host supports (shown as panel buttons).</summary>
    public HashSet<OverlayCommand> ExtraCommands { get; } = new();

    public OverlayIntent Intent => _intent;

    /// <summary>The colour picked in <see cref="OverlayIntent.PickColor"/> mode, and whether RGB notation was asked for.</summary>
    public Color? PickedColor { get; private set; }

    public bool PickedAsRgb { get; private set; }

    /// <summary>Opens straight into an existing selection with annotations (re-editing a capture from history).</summary>
    public void Preload(PixelRect selection, IReadOnlyList<Annotation> annotations)
    {
        _preselect = selection;
        _preload = annotations;
    }

    // ------------------------------------------------------------------ open / reveal / close

    public void Open()
    {
        long t0 = _sinceHotkey.ElapsedMilliseconds;
        var bitmaps = new List<BitmapSource>();
        foreach (MonitorInfo monitor in _frame.Monitors)
        {
            // Bitmap DPI = monitor DPI, so its natural WPF size is exactly the monitor in DIPs (drawn 1:1).
            bitmaps.Add(_frame.CreateBitmap(monitor.Bounds, 96 * monitor.Scale));
        }

        long t1 = _sinceHotkey.ElapsedMilliseconds;
        _editor = new AnnotationEditor(_frame, _settings);
        _editor.Changed += OnEditorChanged;
        _editor.TextEditStarted += OnTextEditStarted;
        _editor.TextEditEnded += OnTextEditEnded;

        _pool.Retain(_frame.Monitors);
        for (int i = 0; i < _frame.Monitors.Count; i++)
        {
            OverlayWindow window = _pool.Get(_frame.Monitors[i]);
            window.Attach(this, bitmaps[i], _settings.ShowMagnifier);
            window.Annotations.Editor = _editor;
            _windows.Add(window);
        }

        long t2 = _sinceHotkey.ElapsedMilliseconds;
        AnnotationEditor editor = _editor;
        _panels = new OverlayPanels(new PanelActions
        {
            Command = RequestFinish,
            Close = Cancel,
            Undo = () => editor.Undo(),
            Redo = () => editor.Redo(),
            ToggleTool = editor.ToggleTool,
            PickColor = editor.SetColor,
            PickThickness = editor.SetThickness,
            Pin = ExtraCommands.Contains(OverlayCommand.Pin) ? () => RequestCommand(OverlayCommand.Pin, false) : null,
            Ocr = ExtraCommands.Contains(OverlayCommand.Ocr) ? () => RequestCommand(OverlayCommand.Ocr, false) : null,
            Beautify = ExtraCommands.Contains(OverlayCommand.Beautify) ? () => RequestCommand(OverlayCommand.Beautify, false) : null,
        })
        {
            Relayout = Render,
        };
        _panels.Sync(editor);

        Win32.POINT cursor = DisplayLayout.GetCursorPosition();
        _cursorX = cursor.X;
        _cursorY = cursor.Y;
        UpdateHighlight();
        if (_preselect is PixelRect preselect)
        {
            if (_preload is not null) editor.Document.Load(_preload);
            _selection = preselect.Intersect(_frame.Bounds);
            _mode = OverlayMode.Selected;
        }

        Render();

        _pendingFirstRenders = _windows.Count(w => !w.HasRendered);
        foreach (OverlayWindow window in _windows) window.Show();
        long t3 = _sinceHotkey.ElapsedMilliseconds;
        _timings = $"bitmaps {t1 - t0} ms, windows {t2 - t1} ms, show {t3 - t2} ms";

        CompositionTarget.Rendering += OnCompositionFrame;

        // Normally the windows are ready within a few frames; never wait forever for a straggler.
        _revealFallback = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Normal, (_, _) => Reveal(),
            Dispatcher.CurrentDispatcher);
        _revealFallback.Start();
    }

    internal void OnWindowFirstRendered(OverlayWindow window)
    {
        _pendingFirstRenders--;
        TryReveal();
    }

    private void OnCompositionFrame(object? sender, EventArgs e)
    {
        _framesSinceShow++;
        TryReveal();
    }

    /// <summary>
    /// A new window reports its first frame (ContentRendered); a reused one has no such event, so we also let a few
    /// frames pass for the new pixels to be rendered and presented. Revealing earlier could flash the previous
    /// capture's frame for an instant.
    /// </summary>
    private void TryReveal()
    {
        if (_pendingFirstRenders > 0 || _framesSinceShow < FramesBeforeReveal) return;
        Reveal();
    }

    private void Reveal()
    {
        CompositionTarget.Rendering -= OnCompositionFrame;
        _revealFallback?.Stop();
        if (_revealed || _mode == OverlayMode.Closing) return;
        _revealed = true;

        if (_warmUp)
        {
            // Exercise the selection, panel and annotation drawing paths once too, then close without uncloaking.
            _selection = new PixelRect(_frame.Bounds.X + 10, _frame.Bounds.Y + 10, 200, 160);
            _mode = OverlayMode.Selected;
            _editor?.Document.Execute(new AddCommand(new ArrowAnnotation { Start = new Point(_frame.Bounds.X + 20, _frame.Bounds.Y + 20), End = new Point(_frame.Bounds.X + 120, _frame.Bounds.Y + 90) }));
            Render();
            Log.Info($"Overlay warm-up finished in {_sinceHotkey.ElapsedMilliseconds} ms ({_timings}).");
            BeginClose(flash: null);
            return;
        }

        Win32.DwmFlush(); // make sure DWM has composed the presented frame before it becomes visible
        foreach (OverlayWindow window in _windows) window.Reveal(_animate);

        OverlayWindow target = WindowAt(_cursorX, _cursorY) ?? _windows[0];
        Foreground.Force(target.Handle);
        target.Activate();
        target.Focus();
        Keyboard.Focus(target);

        Log.Info($"Overlay visible {_sinceHotkey.ElapsedMilliseconds} ms after the hotkey ({_windows.Count} window(s), {_intent}; {_timings}).");
    }

    public void Cancel() => BeginClose(flash: null);

    private void BeginClose(PixelRect? flash)
    {
        if (_mode == OverlayMode.Closing) return;
        _mode = OverlayMode.Closing;
        _revealFallback?.Stop();
        _transientTimer.Stop();

        foreach (OverlayWindow window in _windows)
        {
            if (window.IsMouseCaptured) window.ReleaseMouseCapture();
            window.EndTextEdit();
            window.AnimateOut(flash, _animate && _revealed);
        }

        if (_animate && _revealed)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CloseAnimationMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                FinishClose();
            };
            timer.Start();
        }
        else
        {
            FinishClose();
        }
    }

    private void FinishClose()
    {
        CompositionTarget.Rendering -= OnCompositionFrame;
        foreach (OverlayWindow window in _windows)
        {
            try { window.Detach(); } catch (Exception ex) { Log.Warn("Hiding an overlay window failed", ex); }
        }

        _windows.Clear();
        if (_revealed && !_warmUp) Foreground.Restore(_previousForeground);
        Closed?.Invoke(this);
    }

    /// <summary>Tears everything down immediately (opening failed half-way, or a warm-up must make room).</summary>
    public void ForceClose()
    {
        if (_mode == OverlayMode.Closing && _windows.Count == 0) return;
        _mode = OverlayMode.Closing;
        _revealFallback?.Stop();
        FinishClose();
    }

    // ------------------------------------------------------------------ result

    /// <summary>The finished image for the current selection: frozen pixels plus every annotation.</summary>
    public BitmapSource RenderResult()
    {
        if (_selection is not PixelRect region) throw new InvalidOperationException("There is no selection.");
        _editor?.CommitText();
        return AnnotationExporter.Render(_frame, region, _editor?.Document.Items ?? Array.Empty<Annotation>());
    }

    /// <summary>
    /// Copy or Save asked for by the user. The "close after copy/save" setting decides what happens by default and
    /// holding Shift does the opposite (so with the defaults, Shift keeps the editor open).
    /// </summary>
    private void RequestFinish(OverlayCommand command, bool shift) =>
        RequestCommand(command, KeepsEditorOpen(command == OverlayCommand.Save ? _settings.CloseAfterSave : _settings.CloseAfterCopy, shift));

    internal static bool KeepsEditorOpen(bool closeByDefault, bool shift) => closeByDefault == shift;

    private void RequestCommand(OverlayCommand command, bool keepOpen)
    {
        if (_mode == OverlayMode.Closing || _selection is not PixelRect region) return;
        _editor?.CommitText();

        CommandRequested?.Invoke(this, command, keepOpen);
        if (keepOpen)
        {
            if (_settings.CaptureFlash && _animate)
            {
                foreach (OverlayWindow window in _windows)
                {
                    if (region.IntersectsWith(window.Monitor.Bounds)) window.Flash(region);
                }
            }
        }
        else
        {
            BeginClose(_settings.CaptureFlash && command is OverlayCommand.Copy or OverlayCommand.Save ? region : null);
        }
    }

    // ------------------------------------------------------------------ pointer input

    internal void OnPointerDown(OverlayWindow source, int x, int y, ModifierKeys modifiers, int clickCount)
    {
        if (_mode == OverlayMode.Closing) return;
        SetCursor(x, y, source);

        if (_intent == OverlayIntent.PickColor && _mode == OverlayMode.Hover)
        {
            PickedColor = _frame.GetPixel(x, y);
            PickedAsRgb = modifiers.HasFlag(ModifierKeys.Shift);
            CommandRequested?.Invoke(this, OverlayCommand.PickColor, false);
            BeginClose(flash: null);
            return;
        }

        if (_mode == OverlayMode.Selected && _selection is PixelRect selection && _editor is AnnotationEditor editor)
        {
            if (_panels?.PaletteOpen == true)
            {
                _panels.ClosePalette();
                Render();
            }

            int tolerance = HandleTolerance(source);
            var point = new Point(x, y);
            bool inside = selection.Contains(x, y);

            // 1) The selection frame: edges and corners, but with a tool active only the handle dots, so strokes
            //    can start right next to the edge.
            HandleKind frame = FrameHandleAt(selection, x, y, tolerance);
            if (frame != HandleKind.None)
            {
                editor.CommitText();
                _mode = OverlayMode.Resizing;
                _dragStart = selection;
                _dragHandle = frame;
                _hoverHandle = frame;
                _pressX = x;
                _pressY = y;
                Render();
                return;
            }

            // 2) Annotations: draw with the active tool, or select / move / reshape an existing object.
            if (editor.WantsPress(point, tolerance, inside))
            {
                _mode = OverlayMode.Annotating;
                editor.Begin(point, modifiers, clickCount, source.Scale, tolerance, inside);
                if (_mode == OverlayMode.Annotating && !editor.IsDragging) _mode = OverlayMode.Selected; // a click (step, text)
                Render();
                return;
            }

            editor.CommitText();
            editor.Deselect();

            // 3) Inside, no tool: drag moves the selection, double-click copies.
            if (inside)
            {
                if (clickCount >= 2)
                {
                    RequestFinish(OverlayCommand.Copy, modifiers.HasFlag(ModifierKeys.Shift));
                    return;
                }

                _mode = OverlayMode.Moving;
                _dragStart = selection;
                _dragHandle = HandleKind.Move;
                _pressX = x;
                _pressY = y;
                Render();
                return;
            }

            // 4) Outside: never throw away annotations by starting a new selection.
            if (editor.HasContent)
            {
                Render();
                return;
            }
        }

        // A click picks the highlighted window, a drag draws a new rectangle.
        _mode = OverlayMode.Pressing;
        _pressX = x;
        _pressY = y;
        UpdateHighlight();
        Render();
    }

    internal void OnPointerMove(OverlayWindow source, int x, int y, ModifierKeys modifiers, bool overPanel)
    {
        if (_mode == OverlayMode.Closing) return;
        SetCursor(x, y, source);
        _pointerOverPanel = overPanel;
        bool square = modifiers.HasFlag(ModifierKeys.Shift);

        switch (_mode)
        {
            case OverlayMode.Hover:
                UpdateHighlight();
                break;

            case OverlayMode.Pressing:
                int threshold = DragThreshold(source);
                if (Math.Abs(x - _pressX) >= threshold || Math.Abs(y - _pressY) >= threshold)
                {
                    _mode = OverlayMode.Creating;
                    _highlight = null;
                    _selection = SelectionGeometry.FromDrag(_pressX, _pressY, x, y, square, _frame.Bounds);
                }

                break;

            case OverlayMode.Creating:
                _selection = SelectionGeometry.FromDrag(_pressX, _pressY, x, y, square, _frame.Bounds);
                break;

            case OverlayMode.Moving:
                _selection = SelectionGeometry.Move(_dragStart, x - _pressX, y - _pressY, _frame.Bounds);
                break;

            case OverlayMode.Resizing:
                (PixelRect rect, HandleKind handle) = SelectionGeometry.Resize(_dragStart, _dragHandle, x - _pressX, y - _pressY, _frame.Bounds);
                _selection = rect;
                _hoverHandle = handle;
                break;

            case OverlayMode.Annotating:
                _editor?.Move(new Point(x, y), modifiers);
                return; // the editor's Changed event redraws

            case OverlayMode.Selected:
                _hoverHandle = _selection is PixelRect selection && !overPanel
                    ? FrameHandleAt(selection, x, y, HandleTolerance(source))
                    : HandleKind.None;
                break;
        }

        Render();
    }

    internal void OnPointerUp(OverlayWindow source, int x, int y, ModifierKeys modifiers)
    {
        if (_mode == OverlayMode.Closing) return;
        SetCursor(x, y, source);

        switch (_mode)
        {
            case OverlayMode.Pressing:
                // A click without a drag: take the window (or monitor) under the cursor.
                UpdateHighlight();
                if (_highlight is PixelRect target) Select(target);
                break;

            case OverlayMode.Creating:
                if (_selection is { Width: >= 2, Height: >= 2 } created)
                {
                    Select(created);
                }
                else
                {
                    _selection = null;
                    _mode = OverlayMode.Hover;
                    UpdateHighlight();
                }

                break;

            case OverlayMode.Moving:
            case OverlayMode.Resizing:
                _mode = OverlayMode.Selected;
                _hoverHandle = _selection is PixelRect s ? FrameHandleAt(s, x, y, HandleTolerance(source)) : HandleKind.None;
                break;

            case OverlayMode.Annotating:
                _mode = OverlayMode.Selected;
                _editor?.End(new Point(x, y), modifiers);
                break;
        }

        Render();
    }

    internal void OnRightClick(OverlayWindow source, int x, int y)
    {
        if (_mode == OverlayMode.Closing) return;
        SetCursor(x, y, source);

        if (_mode is OverlayMode.Selected && _editor is { HasContent: true } editor)
        {
            // With annotations, right-click only puts the pen down (no tool, nothing selected) — never discards work.
            editor.CommitText();
            editor.SetTool(null);
            Render();
            return;
        }

        if (_selection is not null && _mode is OverlayMode.Selected)
        {
            // First right-click drops the selection, the second one closes the overlay.
            _editor?.SetTool(null);
            _selection = null;
            _mode = OverlayMode.Hover;
            UpdateHighlight();
            Render();
            return;
        }

        Cancel();
    }

    internal void OnWheel(OverlayWindow source, int delta)
    {
        if (_mode is not (OverlayMode.Selected or OverlayMode.Annotating) || _editor is null) return;
        string label = _editor.AdjustSize(delta > 0 ? 1 : -1);
        _transient = label;
        _transientTimer.Stop();
        _transientTimer.Start();
        Render();
    }

    // ------------------------------------------------------------------ keyboard

    internal bool OnKeyDown(Key key, ModifierKeys modifiers)
    {
        if (_mode == OverlayMode.Closing) return true;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);
        bool selected = _mode is OverlayMode.Selected or OverlayMode.Annotating;

        if (selected && _editor is AnnotationEditor editor)
        {
            if (ctrl && key == Key.Z)
            {
                if (shift) editor.Redo();
                else editor.Undo();
                return true;
            }

            if (ctrl && key == Key.Y)
            {
                editor.Redo();
                return true;
            }

            if (key is Key.Delete or Key.Back && editor.DeleteSelected()) return true;

            if (!ctrl && !alt && !shift && ToolForKey(key) is ToolKind tool)
            {
                editor.ToggleTool(tool);
                return true;
            }

            if (key is Key.Left or Key.Right or Key.Up or Key.Down && editor.Selected is not null)
            {
                int step = shift ? 10 : 1;
                editor.Nudge(key == Key.Left ? -step : key == Key.Right ? step : 0, key == Key.Up ? -step : key == Key.Down ? step : 0);
                return true;
            }

            if (ctrl && key == Key.P && ExtraCommands.Contains(OverlayCommand.Pin))
            {
                RequestCommand(OverlayCommand.Pin, keepOpen: false);
                return true;
            }

            if (ctrl && key == Key.T && ExtraCommands.Contains(OverlayCommand.Ocr))
            {
                RequestCommand(OverlayCommand.Ocr, keepOpen: false);
                return true;
            }

            if (ctrl && key == Key.B && ExtraCommands.Contains(OverlayCommand.Beautify))
            {
                RequestCommand(OverlayCommand.Beautify, keepOpen: false);
                return true;
            }
        }

        switch (key)
        {
            case Key.Escape:
                if (_panels?.PaletteOpen == true)
                {
                    _panels.ClosePalette();
                    Render();
                    return true;
                }

                Cancel();
                return true;

            case Key.Enter:
                if (selected) RequestFinish(OverlayCommand.Copy, shift);
                return true;

            case Key.C when ctrl:
                if (selected) RequestFinish(OverlayCommand.Copy, shift);
                return true;

            case Key.S when ctrl:
                if (selected) RequestFinish(OverlayCommand.Save, shift);
                return true;

            case Key.A when ctrl:
                Select(_frame.Bounds);
                return true;

            case Key.F when !ctrl && !alt && !selected:
                Select(DisplayLayout.FromPoint(_frame.Monitors, _cursorX, _cursorY).Bounds);
                return true;

            case Key.C when !ctrl && !alt && _mode == OverlayMode.Hover:
                _intent = _intent == OverlayIntent.PickColor ? OverlayIntent.Capture : OverlayIntent.PickColor;
                UpdateHighlight();
                Render();
                return true;

            case Key.O when !ctrl && !alt && _mode == OverlayMode.Hover:
                _intent = _intent == OverlayIntent.ExtractText ? OverlayIntent.Capture : OverlayIntent.ExtractText;
                Render();
                return true;

            case Key.R when !ctrl && !alt && !selected:
                if (PixelRect.FromArray(_settings.LastRegion) is PixelRect last && !last.Intersect(_frame.Bounds).IsEmpty)
                {
                    Select(last.Intersect(_frame.Bounds));
                }

                return true;

            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
                Nudge(key, shift ? 10 : 1, ctrl);
                return true;
        }

        return false;
    }

    private static ToolKind? ToolForKey(Key key) => key switch
    {
        Key.A => ToolKind.Arrow,
        Key.R => ToolKind.Rectangle,
        Key.E => ToolKind.Ellipse,
        Key.L => ToolKind.Line,
        Key.P => ToolKind.Pencil,
        Key.M => ToolKind.Marker,
        Key.T => ToolKind.Text,
        Key.N => ToolKind.Step,
        Key.X => ToolKind.Pixelate,
        Key.B => ToolKind.Redact,
        _ => null,
    };

    private void Nudge(Key key, int step, bool resize)
    {
        int dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
        int dy = key == Key.Up ? -step : key == Key.Down ? step : 0;

        if (_mode == OverlayMode.Selected && _selection is PixelRect selection)
        {
            _selection = resize
                ? SelectionGeometry.Resize(selection, HandleKind.BottomRight, dx, dy, _frame.Bounds).Rect
                : SelectionGeometry.Move(selection, dx, dy, _frame.Bounds);
            Render();
            return;
        }

        if (_mode is OverlayMode.Hover or OverlayMode.Pressing or OverlayMode.Creating)
        {
            // Pixel-precise cursor positioning; the resulting mouse move updates everything else.
            int x = Math.Clamp(_cursorX + dx, _frame.Bounds.X, _frame.Bounds.Right - 1);
            int y = Math.Clamp(_cursorY + dy, _frame.Bounds.Y, _frame.Bounds.Bottom - 1);
            Win32.SetCursorPos(x, y);
        }
    }

    // ------------------------------------------------------------------ editor events

    private void OnEditorChanged()
    {
        if (_editor is null) return;
        _panels?.Sync(_editor);
        if (_editor.EditingText is TextAnnotation text) _textWindow?.UpdateTextEdit(text);
        Render();
    }

    private void OnTextEditStarted(TextAnnotation text)
    {
        OverlayWindow window = WindowAt((int)text.Origin.X, (int)text.Origin.Y) ?? WindowAt(_cursorX, _cursorY) ?? _windows[0];
        _textWindow = window;
        AnnotationEditor editor = _editor!;
        window.BeginTextEdit(text, editor.UpdateEditingText, editor.CommitText, editor.CancelText);
    }

    private void OnTextEditEnded()
    {
        _textWindow?.EndTextEdit();
        _textWindow = null;
    }

    // ------------------------------------------------------------------ helpers

    private void Select(PixelRect region)
    {
        PixelRect clipped = region.Intersect(_frame.Bounds);
        if (clipped.IsEmpty) return;

        _selection = clipped;
        _highlight = null;
        _mode = OverlayMode.Selected;
        _hoverHandle = FrameHandleAt(clipped, _cursorX, _cursorY, 6);
        Render();

        if (_intent == OverlayIntent.ExtractText)
        {
            RequestCommand(OverlayCommand.Ocr, keepOpen: false);
            return;
        }

        if (_settings.AutoCopyOnSelection)
        {
            RequestCommand(OverlayCommand.Copy, keepOpen: true);
        }
    }

    /// <summary>
    /// The edge/corner of the selection under the pointer. With a drawing tool active only the eight handle dots
    /// count (so a stroke can start right at the border); the interior never counts here.
    /// </summary>
    private HandleKind FrameHandleAt(PixelRect selection, int x, int y, int tolerance)
    {
        HandleKind hit = SelectionGeometry.HitTest(selection, x, y, tolerance);
        if (hit is HandleKind.None or HandleKind.Move) return HandleKind.None;
        if (_editor?.Tool is null) return hit;

        var handles = new (HandleKind Kind, int X, int Y)[]
        {
            (HandleKind.TopLeft, selection.X, selection.Y), (HandleKind.Top, selection.X + selection.Width / 2, selection.Y),
            (HandleKind.TopRight, selection.Right, selection.Y), (HandleKind.Right, selection.Right, selection.Y + selection.Height / 2),
            (HandleKind.BottomRight, selection.Right, selection.Bottom), (HandleKind.Bottom, selection.X + selection.Width / 2, selection.Bottom),
            (HandleKind.BottomLeft, selection.X, selection.Bottom), (HandleKind.Left, selection.X, selection.Y + selection.Height / 2),
        };
        foreach ((HandleKind kind, int hx, int hy) in handles)
        {
            if (Math.Abs(x - hx) <= tolerance && Math.Abs(y - hy) <= tolerance) return kind;
        }

        return HandleKind.None;
    }

    private void SetCursor(int x, int y, OverlayWindow source)
    {
        _cursorX = x;
        _cursorY = y;
        _hoverScale = source.Scale;
    }

    private void UpdateHighlight()
    {
        if (_intent == OverlayIntent.PickColor)
        {
            _highlight = null; // nothing veiled or framed while picking a colour
            return;
        }

        WindowInfo? window = WindowFinder.HitTest(_frame.Windows, _cursorX, _cursorY);
        if (window is not null)
        {
            _highlight = window.Bounds;
            _highlightTitle = string.IsNullOrWhiteSpace(window.Title) ? null : window.Title;
        }
        else
        {
            _highlight = DisplayLayout.FromPoint(_frame.Monitors, _cursorX, _cursorY).Bounds;
            _highlightTitle = "Screen";
        }
    }

    private OverlayWindow? WindowAt(int x, int y) => _windows.FirstOrDefault(w => w.Monitor.Bounds.Contains(x, y));

    /// <summary>Grab distance for edges and corners: about 7 DIPs, so it feels the same on every monitor.</summary>
    private static int HandleTolerance(OverlayWindow window) => (int)Math.Ceiling(7 * window.Scale);

    private static int DragThreshold(OverlayWindow window) =>
        Math.Max(3, Win32.GetSystemMetricsForDpi(Win32.SM_CXDRAG, (uint)Math.Round(96 * window.Scale)));

    private void Render()
    {
        if (_windows.Count == 0) return;

        // While hovering or pressing (before a drag starts) we preview what a click would take: the highlighted
        // window. Any previous selection is hidden until the new gesture decides what happens to it.
        bool hovering = _mode is OverlayMode.Hover or OverlayMode.Pressing;
        PixelRect? selection = hovering ? null : _selection;
        PixelRect? highlight = hovering ? _highlight : null;

        bool selectedLike = _mode is OverlayMode.Selected or OverlayMode.Moving or OverlayMode.Resizing or OverlayMode.Annotating;
        bool crosshair = (_mode is OverlayMode.Hover or OverlayMode.Pressing or OverlayMode.Creating) && !_pointerOverPanel;
        bool magnifier = (_settings.ShowMagnifier || _intent == OverlayIntent.PickColor) && !_pointerOverPanel &&
                         _mode is OverlayMode.Hover or OverlayMode.Pressing or OverlayMode.Creating or OverlayMode.Resizing;
        string? hint = _mode is OverlayMode.Hover or OverlayMode.Pressing or OverlayMode.Creating
            ? _intent switch
            {
                OverlayIntent.PickColor => "Click to copy a colour  ·  Shift+click for RGB  ·  Esc to cancel",
                OverlayIntent.ExtractText => "Select the text to copy  ·  Esc to cancel",
                _ => null,
            }
            : null;

        PixelRect? labelAnchor = selection ?? highlight;
        MonitorInfo? labelMonitor = labelAnchor is PixelRect anchor ? DisplayLayout.FromPoint(_frame.Monitors, anchor.X, anchor.Y) : null;
        (Rect Bounds, Point[] Handles)? adorner = selectedLike ? _editor?.SelectionAdorner : null;

        var view = new OverlayView(
            selection,
            highlight,
            highlight is null ? null : _highlightTitle,
            selectedLike,
            _mode == OverlayMode.Resizing ? _hoverHandle : HandleKind.None,
            crosshair,
            magnifier,
            _cursorX,
            _cursorY,
            _frame.GetPixel(_cursorX, _cursorY),
            CursorFor(),
            labelMonitor,
            adorner?.Bounds,
            adorner?.Handles,
            _transient,
            hint,
            Veil: _intent != OverlayIntent.PickColor);

        foreach (OverlayWindow window in _windows) window.Render(view);

        // Panels while a selection is settled or being annotated; they get out of the way while the frame moves.
        bool panels = _mode is OverlayMode.Selected or OverlayMode.Annotating;
        _panels?.Update(panels ? _selection : null, _windows, _frame.Monitors, _animate && _revealed);
    }

    private Cursor CursorFor()
    {
        if (_pointerOverPanel) return Cursors.Arrow;

        switch (_mode)
        {
            case OverlayMode.Moving:
                return Cursors.SizeAll;
            case OverlayMode.Resizing:
                return ResizeCursor(_hoverHandle);
            case OverlayMode.Annotating:
                return _editor?.Selected is not null ? Cursors.SizeAll : Cursors.Cross;
            case OverlayMode.Selected when _selection is PixelRect selection:
            {
                if (_hoverHandle != HandleKind.None) return ResizeCursor(_hoverHandle);
                bool inside = selection.Contains(_cursorX, _cursorY);
                int tolerance = (int)Math.Ceiling(7 * _hoverScale);
                if (_editor?.CursorAt(new Point(_cursorX, _cursorY), tolerance, inside) is Cursor editorCursor) return editorCursor;
                if (inside) return Cursors.SizeAll;
                return _editor?.HasContent == true ? Cursors.Arrow : Cursors.Cross;
            }

            default:
                return Cursors.Cross;
        }
    }

    private static Cursor ResizeCursor(HandleKind handle) => handle switch
    {
        HandleKind.Move => Cursors.SizeAll,
        HandleKind.TopLeft or HandleKind.BottomRight => Cursors.SizeNWSE,
        HandleKind.TopRight or HandleKind.BottomLeft => Cursors.SizeNESW,
        HandleKind.Top or HandleKind.Bottom => Cursors.SizeNS,
        HandleKind.Left or HandleKind.Right => Cursors.SizeWE,
        _ => Cursors.Cross,
    };

    internal void OnWindowDpiChanged(OverlayWindow window) => Render();
}
