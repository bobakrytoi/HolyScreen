using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HolyScreen.Capture;
using HolyScreen.Core;

namespace HolyScreen.Editor;

/// <summary>
/// The annotation editor of one capture: the active tool, colour and size, drawing new objects, selecting, moving
/// and reshaping existing ones, text entry, and undo/redo. Works in virtual-screen pixels; the overlay feeds it
/// pointer events and draws whatever <see cref="Render"/> produces.
/// </summary>
internal sealed class AnnotationEditor
{
    public const double MinThickness = 1;
    public const double MaxThickness = 40;
    public const double MinTextSize = 8;
    public const double MaxTextSize = 160;

    private readonly CapturedFrame _frame;
    private Annotation? _draft;
    private Annotation? _selected;
    private Annotation? _dragStart;
    private int _dragHandle = -1;
    private bool _dragMoved;
    private Point _press;
    private Point _anchor;
    private double _scale = 1;
    private TextAnnotation? _editingText;
    private TextAnnotation? _editingBefore;
    private bool _editingIsNew;

    public AnnotationEditor(CapturedFrame frame, AppSettings settings)
    {
        _frame = frame;
        Color = AppSettings.TryParseColor(settings.AnnotationColor, out uint argb) ? argb : 0xFFFF3B30;
        Thickness = Math.Clamp(settings.AnnotationThickness, MinThickness, MaxThickness);
        TextSize = Math.Clamp(settings.TextSize, MinTextSize, MaxTextSize);
        Document.Changed += () => Changed?.Invoke();
    }

    public AnnotationDocument Document { get; } = new();

    public ToolKind? Tool { get; private set; }

    /// <summary>Colour for new objects (0xAARRGGBB).</summary>
    public uint Color { get; private set; }

    /// <summary>Stroke size for new objects, in DIPs (multiplied by the monitor scale when drawing).</summary>
    public double Thickness { get; private set; }

    /// <summary>Font size for new text, in DIPs.</summary>
    public double TextSize { get; private set; }

    public Annotation? Selected => _selected;

    public TextAnnotation? EditingText => _editingText;

    public bool IsDragging => _draft is not null || _dragStart is not null;

    public bool HasContent => Document.Items.Count > 0 || _editingText is not null;

    /// <summary>Anything visible changed (redraw).</summary>
    public event Action? Changed;

    /// <summary>A text object needs an input box (new text, or a double-click on existing text).</summary>
    public event Action<TextAnnotation>? TextEditStarted;

    public event Action? TextEditEnded;

    // ------------------------------------------------------------------ tool / style

    public void SetTool(ToolKind? tool)
    {
        CommitText();
        Tool = tool;
        _selected = null;
        Changed?.Invoke();
    }

    public void ToggleTool(ToolKind tool) => SetTool(Tool == tool ? null : tool);

    public void SetColor(uint argb)
    {
        Color = argb;
        if (_editingText is not null)
        {
            _editingText.Color = argb;
        }
        else if (_selected is not null && _selected is not RedactAnnotation and not PixelateAnnotation)
        {
            Modify(_selected, a => a.Color = argb);
        }

        Changed?.Invoke();
    }

    public void SetThickness(double thickness)
    {
        Thickness = Math.Clamp(thickness, MinThickness, MaxThickness);
        if (_selected is not null && _selected is not TextAnnotation and not PixelateAnnotation and not RedactAnnotation)
        {
            double value = Thickness * _scale;
            Modify(_selected, a => a.Thickness = value);
        }

        Changed?.Invoke();
    }

    /// <summary>Mouse wheel: bigger/smaller stroke, or font for text. Returns a short label for the change.</summary>
    public string AdjustSize(int steps)
    {
        bool text = Tool == ToolKind.Text || _selected is TextAnnotation || _editingText is not null;
        if (text)
        {
            TextSize = Math.Clamp(TextSize + 2 * steps, MinTextSize, MaxTextSize);
            double size = TextSize * _scale;
            if (_editingText is not null) _editingText.FontSize = size;
            else if (_selected is TextAnnotation selectedText) Modify(selectedText, a => ((TextAnnotation)a).FontSize = size);
            Changed?.Invoke();
            return $"Text {TextSize:0}";
        }

        SetThickness(Thickness + steps);
        return $"Size {Thickness:0}";
    }

    // ------------------------------------------------------------------ pointer

    /// <summary>Whether a press at <paramref name="p"/> belongs to the editor (rather than to the selection frame).</summary>
    public bool WantsPress(Point p, double tolerance, bool insideSelection)
    {
        if (_selected is not null && AnnotationGeometry.HandleAt(_selected, p, tolerance + 2) >= 0) return true;
        if (AnnotationGeometry.HitTest(Document.Items, p, tolerance) is not null) return true;
        return Tool is not null && insideSelection;
    }

    public void Begin(Point p, ModifierKeys modifiers, int clickCount, double scale, double tolerance, bool insideSelection)
    {
        CommitText();
        _scale = scale;
        _press = p;
        _dragMoved = false;

        if (_selected is not null)
        {
            int handle = AnnotationGeometry.HandleAt(_selected, p, tolerance + 2);
            if (handle >= 0)
            {
                _dragStart = _selected.Clone();
                _dragHandle = handle;
                return;
            }
        }

        bool forceDraw = modifiers.HasFlag(ModifierKeys.Control) && Tool is not null;
        Annotation? hit = forceDraw ? null : AnnotationGeometry.HitTest(Document.Items, p, tolerance);
        if (hit is not null)
        {
            if (clickCount >= 2 && hit is TextAnnotation text)
            {
                StartTextEdit(text, isNew: false);
                return;
            }

            _selected = hit;
            _dragStart = hit.Clone();
            _dragHandle = -1;
            Changed?.Invoke();
            return;
        }

        _selected = null;
        if (Tool is not ToolKind tool || !insideSelection)
        {
            Changed?.Invoke();
            return;
        }

        _anchor = p;
        switch (tool)
        {
            case ToolKind.Step:
                Document.Execute(new AddCommand(new StepAnnotation
                {
                    Center = p,
                    Number = NextStepNumber(),
                    Color = Color,
                    Thickness = Thickness * scale,
                }));
                break;

            case ToolKind.Text:
                double fontSize = TextSize * scale;
                StartTextEdit(new TextAnnotation
                {
                    Origin = p - new Vector(0, fontSize * 0.65),
                    FontSize = fontSize,
                    Color = Color,
                }, isNew: true);
                break;

            default:
                _draft = Create(tool, p, scale);
                Changed?.Invoke();
                break;
        }
    }

    public void Move(Point p, ModifierKeys modifiers)
    {
        bool constrain = modifiers.HasFlag(ModifierKeys.Shift);
        if (_draft is not null)
        {
            UpdateDraft(_draft, p, constrain);
            Changed?.Invoke();
            return;
        }

        if (_dragStart is not null && _selected is not null)
        {
            Vector delta = p - _press;
            if (Math.Abs(delta.X) < 0.5 && Math.Abs(delta.Y) < 0.5 && !_dragMoved) return;
            _dragMoved = true;
            if (_dragHandle >= 0)
            {
                AnnotationGeometry.DragHandle(_selected, _dragStart, _dragHandle, delta, constrain);
            }
            else
            {
                _selected.CopyFrom(_dragStart);
                AnnotationGeometry.Offset(_selected, delta);
            }

            Changed?.Invoke();
        }
    }

    public void End(Point p, ModifierKeys modifiers)
    {
        if (_draft is not null)
        {
            UpdateDraft(_draft, p, modifiers.HasFlag(ModifierKeys.Shift));
            Annotation finished = _draft;
            _draft = null;
            if (IsMeaningful(finished)) Document.Execute(new AddCommand(finished));
            else Changed?.Invoke();
            return;
        }

        if (_dragStart is not null && _selected is not null)
        {
            if (_dragMoved) Document.Record(new ModifyCommand(_selected, _dragStart, _selected));
            _dragStart = null;
            _dragHandle = -1;
            Changed?.Invoke();
        }
    }

    /// <summary>The pointer shape for hovering at <paramref name="p"/>, or null when the editor has no opinion.</summary>
    public Cursor? CursorAt(Point p, double tolerance, bool insideSelection)
    {
        if (_selected is not null && AnnotationGeometry.HandleAt(_selected, p, tolerance + 2) >= 0) return Cursors.Hand;
        if (AnnotationGeometry.HitTest(Document.Items, p, tolerance) is not null) return Cursors.SizeAll;
        if (Tool is ToolKind tool && insideSelection) return tool == ToolKind.Text ? Cursors.IBeam : Cursors.Cross;
        return null;
    }

    // ------------------------------------------------------------------ commands

    public bool Undo()
    {
        CommitText();
        bool undone = Document.Undo();
        DropStaleSelection();
        return undone;
    }

    public bool Redo()
    {
        CommitText();
        bool redone = Document.Redo();
        DropStaleSelection();
        return redone;
    }

    public bool DeleteSelected()
    {
        if (_selected is null) return false;
        Annotation victim = _selected;
        _selected = null;
        Document.Execute(new RemoveCommand(victim));
        return true;
    }

    public bool Nudge(int dx, int dy)
    {
        if (_selected is null) return false;
        Modify(_selected, a => AnnotationGeometry.Offset(a, new Vector(dx, dy)));
        return true;
    }

    public void Deselect()
    {
        if (_selected is null) return;
        _selected = null;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ text editing

    public void UpdateEditingText(string text)
    {
        if (_editingText is null) return;
        _editingText.Text = text;
        Changed?.Invoke();
    }

    /// <summary>Finishes text entry: adds new text, records an edit, or removes text that was emptied.</summary>
    public void CommitText()
    {
        if (_editingText is not TextAnnotation text) return;
        _editingText = null;
        text.Text = text.Text.TrimEnd();
        bool empty = string.IsNullOrWhiteSpace(text.Text);

        if (_editingIsNew)
        {
            if (!empty) Document.Execute(new AddCommand(text));
        }
        else if (empty)
        {
            Document.Execute(new RemoveCommand(text));
        }
        else if (_editingBefore is not null &&
                 (_editingBefore.Text != text.Text || _editingBefore.Color != text.Color || _editingBefore.FontSize != text.FontSize))
        {
            Document.Record(new ModifyCommand(text, _editingBefore, text));
        }

        _editingBefore = null;
        TextEditEnded?.Invoke();
        Changed?.Invoke();
    }

    public void CancelText()
    {
        if (_editingText is not TextAnnotation text) return;
        _editingText = null;
        if (!_editingIsNew && _editingBefore is not null) text.CopyFrom(_editingBefore);
        _editingBefore = null;
        TextEditEnded?.Invoke();
        Changed?.Invoke();
    }

    private void StartTextEdit(TextAnnotation text, bool isNew)
    {
        _editingText = text;
        _editingIsNew = isNew;
        _editingBefore = isNew ? null : (TextAnnotation)text.Clone();
        _selected = null;
        TextEditStarted?.Invoke(text);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ drawing

    /// <summary>Draws every annotation (and the one being drawn) in virtual-screen coordinates.</summary>
    public void Render(DrawingContext dc)
    {
        foreach (Annotation annotation in Document.Items)
        {
            if (!ReferenceEquals(annotation, _editingText)) AnnotationRenderer.Render(dc, annotation, _frame);
        }

        if (_draft is not null) AnnotationRenderer.Render(dc, _draft, _frame);
    }

    /// <summary>Outline and handles of the selected object, for the chrome layer.</summary>
    public (Rect Bounds, Point[] Handles)? SelectionAdorner =>
        _selected is null ? null : (AnnotationGeometry.Bounds(_selected), AnnotationGeometry.Handles(_selected));

    private Annotation Create(ToolKind tool, Point p, double scale)
    {
        double thickness = Thickness * scale;
        return tool switch
        {
            ToolKind.Arrow => new ArrowAnnotation { Start = p, End = p, Color = Color, Thickness = thickness },
            ToolKind.Line => new LineAnnotation { Start = p, End = p, Color = Color, Thickness = thickness },
            ToolKind.Rectangle => new RectangleAnnotation { A = p, B = p, Color = Color, Thickness = thickness },
            ToolKind.Ellipse => new EllipseAnnotation { A = p, B = p, Color = Color, Thickness = thickness },
            ToolKind.Pencil => new PencilAnnotation { Points = { p }, Color = Color, Thickness = thickness },
            ToolKind.Marker => new MarkerAnnotation { Points = { p }, Color = Color, Thickness = thickness },
            ToolKind.Pixelate => new PixelateAnnotation { A = p, B = p, BlockSize = Math.Max(10, (int)Math.Round(12 * scale)) },
            ToolKind.Redact => new RedactAnnotation { A = p, B = p },
            _ => throw new ArgumentOutOfRangeException(nameof(tool)),
        };
    }

    private void UpdateDraft(Annotation draft, Point p, bool constrain)
    {
        switch (draft)
        {
            case TwoPointAnnotation line:
                line.End = constrain ? AnnotationGeometry.SnapAngle(p, _anchor) : p;
                break;

            case BoxAnnotation box:
                box.B = constrain ? AnnotationGeometry.SnapSquare(p, _anchor) : p;
                break;

            case MarkerAnnotation marker when constrain:
                // Shift: a straight highlighter stroke, snapped like a line.
                marker.Points = new() { _anchor, AnnotationGeometry.SnapAngle(p, _anchor) };
                break;

            case StrokeAnnotation stroke:
                if (stroke.Points.Count == 0 || (p - stroke.Points[^1]).Length >= 1.5 * _scale) stroke.Points.Add(p);
                break;
        }
    }

    private static bool IsMeaningful(Annotation annotation) => annotation switch
    {
        TwoPointAnnotation line => (line.End - line.Start).Length >= 3,
        BoxAnnotation box => box.Box.Width >= 3 && box.Box.Height >= 3,
        MarkerAnnotation marker => marker.Points.Count >= 2,
        StrokeAnnotation stroke => stroke.Points.Count >= 1,
        _ => true,
    };

    private int NextStepNumber() => Document.Items.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max() + 1;

    private void Modify(Annotation target, Action<Annotation> change)
    {
        Annotation before = target.Clone();
        change(target);
        Document.Record(new ModifyCommand(target, before, target));
    }

    private void DropStaleSelection()
    {
        if (_selected is not null && Document.IndexOf(_selected) < 0) _selected = null;
        Changed?.Invoke();
    }
}
