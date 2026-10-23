using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Capture;
using HolyScreen.Core;
using HolyScreen.Editor;
using Xunit;

namespace HolyScreen.Tests;

/// <summary>A synthetic frozen frame with a known pattern, for editor and export tests.</summary>
internal static class TestFrames
{
    public static CapturedFrame Gradient(int width = 200, int height = 120, int x = 0, int y = 0)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                int o = (row * width + col) * 4;
                pixels[o] = (byte)(col * 255 / Math.Max(1, width - 1));
                pixels[o + 1] = (byte)(row * 255 / Math.Max(1, height - 1));
                pixels[o + 2] = (byte)((col * 7 + row * 3) % 256);
                pixels[o + 3] = 0xFF;
            }
        }

        var bounds = new PixelRect(x, y, width, height);
        var monitor = new MonitorInfo(IntPtr.Zero, "TEST", bounds, bounds, 96, true);
        return new CapturedFrame(bounds, pixels, new[] { monitor }, Array.Empty<WindowInfo>(), DateTime.Now);
    }
}

public class AnnotationDocumentTests
{
    [Fact]
    public void AddUndoRedo()
    {
        var doc = new AnnotationDocument();
        var arrow = new ArrowAnnotation { Start = new Point(0, 0), End = new Point(10, 10) };
        doc.Execute(new AddCommand(arrow));
        Assert.Single(doc.Items);
        Assert.True(doc.Undo());
        Assert.Empty(doc.Items);
        Assert.True(doc.Redo());
        Assert.Same(arrow, doc.Items[0]);
        Assert.False(doc.Redo());
    }

    [Fact]
    public void Remove_UndoRestoresZOrder()
    {
        var doc = new AnnotationDocument();
        var a = new LineAnnotation();
        var b = new LineAnnotation();
        var c = new LineAnnotation();
        foreach (Annotation x in new Annotation[] { a, b, c }) doc.Execute(new AddCommand(x));

        doc.Execute(new RemoveCommand(b));
        Assert.Equal(new Annotation[] { a, c }, doc.Items);
        doc.Undo();
        Assert.Equal(new Annotation[] { a, b, c }, doc.Items);
    }

    [Fact]
    public void TwoModificationsOfTheSameObject_UndoInOrder_AndIdentityIsKept()
    {
        var doc = new AnnotationDocument();
        var rect = new RectangleAnnotation { A = new Point(0, 0), B = new Point(10, 10), Color = 1 };
        doc.Execute(new AddCommand(rect));

        Annotation before = rect.Clone();
        rect.Color = 2;
        doc.Record(new ModifyCommand(rect, before, rect));

        before = rect.Clone();
        rect.B = new Point(50, 50);
        doc.Record(new ModifyCommand(rect, before, rect));

        doc.Undo();
        Assert.Same(rect, doc.Items[0]);
        Assert.Equal(new Point(10, 10), rect.B);
        Assert.Equal(2u, rect.Color);

        doc.Undo();
        Assert.Equal(1u, rect.Color);

        doc.Redo();
        doc.Redo();
        Assert.Equal(2u, rect.Color);
        Assert.Equal(new Point(50, 50), rect.B);
    }

    [Fact]
    public void NewEditAfterUndo_ClearsRedo()
    {
        var doc = new AnnotationDocument();
        doc.Execute(new AddCommand(new LineAnnotation()));
        doc.Undo();
        Assert.True(doc.CanRedo);
        doc.Execute(new AddCommand(new LineAnnotation()));
        Assert.False(doc.CanRedo);
    }
}

public class AnnotationEditorTests
{
    private static AnnotationEditor NewEditor() => new(TestFrames.Gradient(), new AppSettings());

    private static void Drag(AnnotationEditor editor, Point from, Point to, ModifierKeys modifiers = ModifierKeys.None)
    {
        editor.Begin(from, modifiers, 1, 1.0, 6, insideSelection: true);
        editor.Move(new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), modifiers);
        editor.Move(to, modifiers);
        editor.End(to, modifiers);
    }

    [Fact]
    public void DrawingAnArrow_AddsIt_TinyDragsAreIgnored()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Arrow);
        Drag(editor, new Point(10, 10), new Point(100, 50));
        var arrow = Assert.IsType<ArrowAnnotation>(Assert.Single(editor.Document.Items));
        Assert.Equal(new Point(10, 10), arrow.Start);
        Assert.Equal(new Point(100, 50), arrow.End);
        Assert.Equal(0xFFFF3B30u, arrow.Color);

        Drag(editor, new Point(150, 100), new Point(151, 100)); // 1 px: a slip, not an arrow
        Assert.Single(editor.Document.Items);
    }

    [Fact]
    public void Shift_SnapsLinesTo45Degrees_AndBoxesToSquares()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Line);
        Drag(editor, new Point(10, 10), new Point(110, 18), ModifierKeys.Shift);
        var line = (LineAnnotation)editor.Document.Items[0];
        Assert.Equal(10, line.End.Y, 3);

        editor.SetTool(ToolKind.Rectangle);
        Drag(editor, new Point(20, 20), new Point(80, 40), ModifierKeys.Shift);
        Rect box = ((RectangleAnnotation)editor.Document.Items[1]).Box;
        Assert.Equal(box.Width, box.Height, 3);
    }

    [Fact]
    public void Steps_NumberThemselves_AndContinueAfterDeletion()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Step);
        foreach (int x in new[] { 20, 60, 100 }) editor.Begin(new Point(x, 30), ModifierKeys.None, 1, 1, 6, true);
        Assert.Equal(new[] { 1, 2, 3 }, editor.Document.Items.OfType<StepAnnotation>().Select(s => s.Number));

        editor.SetTool(null);
        editor.Begin(new Point(100, 30), ModifierKeys.None, 1, 1, 6, true); // select step 3
        editor.End(new Point(100, 30), ModifierKeys.None);
        Assert.True(editor.DeleteSelected());

        editor.SetTool(ToolKind.Step);
        editor.Begin(new Point(140, 30), ModifierKeys.None, 1, 1, 6, true);
        Assert.Equal(new[] { 1, 2, 3 }, editor.Document.Items.OfType<StepAnnotation>().Select(s => s.Number));
    }

    [Fact]
    public void MovingAnExistingObject_IsOneUndoStep()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Rectangle);
        Drag(editor, new Point(20, 20), new Point(60, 60));
        var rect = (RectangleAnnotation)editor.Document.Items[0];

        editor.SetTool(null);
        Drag(editor, new Point(20, 40), new Point(50, 70)); // grab the left edge, move by (30, 30)
        Assert.Equal(new Rect(50, 50, 40, 40), rect.Box);
        Assert.Same(rect, editor.Selected);

        editor.Undo();
        Assert.Equal(new Rect(20, 20, 40, 40), rect.Box);
        editor.Undo();
        Assert.Empty(editor.Document.Items);
    }

    [Fact]
    public void DraggingAHandle_ReshapesAndCanFlip()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Rectangle);
        Drag(editor, new Point(20, 20), new Point(60, 60));
        var rect = (RectangleAnnotation)editor.Document.Items[0];

        editor.SetTool(null);
        editor.Begin(new Point(20, 40), ModifierKeys.None, 1, 1, 6, true); // select
        editor.End(new Point(20, 40), ModifierKeys.None);
        Drag(editor, new Point(60, 60), new Point(10, 10)); // bottom-right handle past top-left
        Assert.Equal(new Rect(10, 10, 10, 10), rect.Box);
    }

    [Fact]
    public void ControlClick_DrawsOverAnExistingObjectInsteadOfGrabbingIt()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Pencil);
        Drag(editor, new Point(20, 50), new Point(120, 50));
        Drag(editor, new Point(70, 50), new Point(70, 90), ModifierKeys.Control);
        Assert.Equal(2, editor.Document.Items.Count);
    }

    [Fact]
    public void Text_IsAddedOnCommit_EmptyTextIsDropped_EditingCanBeUndone()
    {
        AnnotationEditor editor = NewEditor();
        TextAnnotation? started = null;
        editor.TextEditStarted += t => started = t;
        editor.SetTool(ToolKind.Text);

        editor.Begin(new Point(40, 40), ModifierKeys.None, 1, 1, 6, true);
        Assert.NotNull(started);
        editor.CommitText(); // nothing typed
        Assert.Empty(editor.Document.Items);

        editor.Begin(new Point(40, 40), ModifierKeys.None, 1, 1, 6, true);
        editor.UpdateEditingText("Hello");
        editor.CommitText();
        var text = Assert.IsType<TextAnnotation>(Assert.Single(editor.Document.Items));
        Assert.Equal("Hello", text.Text);

        editor.SetTool(null);
        editor.Begin(text.Origin + new Vector(2, 2), ModifierKeys.None, 2, 1, 6, true); // double-click edits
        editor.UpdateEditingText("Hello, world");
        editor.CommitText();
        Assert.Equal("Hello, world", text.Text);
        editor.Undo();
        Assert.Equal("Hello", text.Text);

        editor.Begin(text.Origin + new Vector(2, 2), ModifierKeys.None, 2, 1, 6, true);
        editor.UpdateEditingText("   ");
        editor.CommitText();
        Assert.Empty(editor.Document.Items);
    }

    [Fact]
    public void CancellingAnEdit_RestoresTheText()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Text);
        editor.Begin(new Point(40, 40), ModifierKeys.None, 1, 1, 6, true);
        editor.UpdateEditingText("Keep me");
        editor.CommitText();
        var text = (TextAnnotation)editor.Document.Items[0];

        editor.Begin(text.Origin + new Vector(2, 2), ModifierKeys.None, 2, 1, 6, true);
        editor.UpdateEditingText("oops");
        editor.CancelText();
        Assert.Equal("Keep me", text.Text);
        Assert.False(editor.Document.CanRedo);
    }

    [Fact]
    public void ColourAndSize_ApplyToTheSelectedObject_AndAreUndoable()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Ellipse);
        Drag(editor, new Point(20, 20), new Point(80, 60));
        var ellipse = (EllipseAnnotation)editor.Document.Items[0];

        editor.SetTool(null);
        editor.Begin(new Point(20, 40), ModifierKeys.None, 1, 1, 6, true);
        editor.End(new Point(20, 40), ModifierKeys.None);
        Assert.Same(ellipse, editor.Selected);

        editor.SetColor(0xFF0A84FF);
        editor.AdjustSize(+3);
        Assert.Equal(0xFF0A84FFu, ellipse.Color);
        Assert.Equal(7, ellipse.Thickness);

        editor.Undo();
        editor.Undo();
        Assert.Equal(0xFFFF3B30u, ellipse.Color);
        Assert.Equal(4, ellipse.Thickness);
    }

    [Fact]
    public void ThicknessScalesWithTheMonitor()
    {
        AnnotationEditor editor = NewEditor();
        editor.SetTool(ToolKind.Line);
        editor.Begin(new Point(10, 10), ModifierKeys.None, 1, 1.5, 6, true);
        editor.Move(new Point(90, 10), ModifierKeys.None);
        editor.End(new Point(90, 10), ModifierKeys.None);
        Assert.Equal(6, editor.Document.Items[0].Thickness); // 4 DIPs on a 150 % monitor
    }
}

public class AnnotationGeometryTests
{
    [Fact]
    public void RectangleOutline_IsHittable_InteriorIsNot()
    {
        var rect = new RectangleAnnotation { A = new Point(0, 0), B = new Point(100, 100), Thickness = 4 };
        Assert.True(AnnotationGeometry.HitTest(rect, new Point(1, 50), 4));
        Assert.True(AnnotationGeometry.HitTest(rect, new Point(103, 50), 4));
        Assert.False(AnnotationGeometry.HitTest(rect, new Point(50, 50), 4));
        Assert.False(AnnotationGeometry.HitTest(rect, new Point(120, 50), 4));
    }

    [Fact]
    public void Ellipse_IsHitNearItsCurveOnly()
    {
        var ellipse = new EllipseAnnotation { A = new Point(0, 0), B = new Point(200, 100), Thickness = 4 };
        Assert.True(AnnotationGeometry.HitTest(ellipse, new Point(100, 1), 4));
        Assert.True(AnnotationGeometry.HitTest(ellipse, new Point(199, 50), 4));
        Assert.False(AnnotationGeometry.HitTest(ellipse, new Point(100, 50), 4));
    }

    [Fact]
    public void SolidBoxes_AreHitAnywhereInside()
    {
        var redact = new RedactAnnotation { A = new Point(10, 10), B = new Point(60, 60) };
        Assert.True(AnnotationGeometry.HitTest(redact, new Point(35, 35), 2));
        Assert.False(AnnotationGeometry.HitTest(redact, new Point(70, 35), 2));
    }

    [Fact]
    public void DistanceToSegment_ClampsToTheEnds()
    {
        Assert.Equal(5, AnnotationGeometry.DistanceToSegment(new Point(5, 5), new Point(0, 0), new Point(10, 0)), 6);
        Assert.Equal(5, AnnotationGeometry.DistanceToSegment(new Point(-3, 4), new Point(0, 0), new Point(10, 0)), 6);
    }

    [Fact]
    public void SnapAngle_RoundsToTheNearest45Degrees()
    {
        Point p = AnnotationGeometry.SnapAngle(new Point(100, 90), new Point(0, 0));
        Assert.Equal(p.X, p.Y, 6);
    }
}

public class AnnotationSerializationTests
{
    [Fact]
    public void EveryAnnotationType_RoundTripsThroughJson()
    {
        var items = new List<Annotation>
        {
            new ArrowAnnotation { Start = new Point(1, 2), End = new Point(3, 4), Color = 0xFF112233, Thickness = 5 },
            new LineAnnotation { Start = new Point(5, 6), End = new Point(7, 8) },
            new RectangleAnnotation { A = new Point(1, 1), B = new Point(9, 9) },
            new EllipseAnnotation { A = new Point(2, 2), B = new Point(8, 8) },
            new PencilAnnotation { Points = { new Point(0, 0), new Point(1, 1), new Point(2, 0) } },
            new MarkerAnnotation { Points = { new Point(3, 3), new Point(9, 3) } },
            new TextAnnotation { Origin = new Point(4, 4), Text = "Привет", FontSize = 30 },
            new StepAnnotation { Center = new Point(10, 10), Number = 7 },
            new PixelateAnnotation { A = new Point(0, 0), B = new Point(40, 40), BlockSize = 16 },
            new RedactAnnotation { A = new Point(5, 5), B = new Point(15, 15) },
        };

        string json = JsonSerializer.Serialize(items);
        List<Annotation> back = JsonSerializer.Deserialize<List<Annotation>>(json)!;

        Assert.Equal(items.Select(i => i.GetType()), back.Select(i => i.GetType()));
        Assert.Equal(JsonSerializer.Serialize(items), JsonSerializer.Serialize(back));
        Assert.Equal("Привет", ((TextAnnotation)back[6]).Text);
        Assert.Equal(7, ((StepAnnotation)back[7]).Number);
    }
}

public class AnnotationExportTests
{
    [Fact]
    public void WithoutAnnotations_TheExportIsTheOriginalPixels()
    {
        Sta.Run(() =>
        {
            CapturedFrame frame = TestFrames.Gradient();
            var region = new PixelRect(10, 5, 100, 60);
            BitmapSource exported = AnnotationExporter.Render(frame, region, Array.Empty<Annotation>());
            Assert.Equal(TestImages.ReadBgra(frame.CreateBitmap(region)), TestImages.ReadBgra(exported));
        });
    }

    [Fact]
    public void UntouchedPixels_StayExact_WhenAnnotationsArePresent()
    {
        Sta.Run(() =>
        {
            CapturedFrame frame = TestFrames.Gradient();
            var region = new PixelRect(0, 0, 200, 120);
            var redact = new RedactAnnotation { A = new Point(150, 80), B = new Point(190, 110) };
            byte[] exported = TestImages.ReadBgra(AnnotationExporter.Render(frame, region, new Annotation[] { redact }));
            byte[] original = TestImages.ReadBgra(frame.CreateBitmap(region));

            // Away from the box nothing may change (the background is drawn 1:1, not resampled).
            for (int y = 0; y < 70; y += 3)
            {
                for (int x = 0; x < 140; x += 3)
                {
                    int o = (y * 200 + x) * 4;
                    Assert.Equal(original[o], exported[o]);
                    Assert.Equal(original[o + 1], exported[o + 1]);
                    Assert.Equal(original[o + 2], exported[o + 2]);
                }
            }
        });
    }

    [Fact]
    public void Redact_FillsExactlyItsBox()
    {
        Sta.Run(() =>
        {
            CapturedFrame frame = TestFrames.Gradient();
            var region = new PixelRect(0, 0, 200, 120);
            var redact = new RedactAnnotation { A = new Point(20, 30), B = new Point(60, 50) };
            byte[] pixels = TestImages.ReadBgra(AnnotationExporter.Render(frame, region, new Annotation[] { redact }));

            int inside = (40 * 200 + 40) * 4;
            Assert.Equal(new byte[] { 0x16, 0x13, 0x11 }, pixels[inside..(inside + 3)]);
            int corner = (30 * 200 + 20) * 4;
            Assert.Equal(new byte[] { 0x16, 0x13, 0x11 }, pixels[corner..(corner + 3)]);
            int outside = (29 * 200 + 20) * 4;
            Assert.NotEqual(new byte[] { 0x16, 0x13, 0x11 }, pixels[outside..(outside + 3)]);
        });
    }

    [Fact]
    public void Pixelate_ProducesUniformBlocksOfTheAveragedOriginal()
    {
        Sta.Run(() =>
        {
            CapturedFrame frame = TestFrames.Gradient();
            var region = new PixelRect(0, 0, 200, 120);
            var pixelate = new PixelateAnnotation { A = new Point(40, 20), B = new Point(100, 68), BlockSize = 12 };
            byte[] pixels = TestImages.ReadBgra(AnnotationExporter.Render(frame, region, new Annotation[] { pixelate }));

            // Block (1, 1) covers x 52..63, y 32..43: every pixel equals the block average.
            long b = 0, g = 0, r = 0;
            for (int y = 32; y < 44; y++)
            {
                for (int x = 52; x < 64; x++)
                {
                    var c = frame.GetPixel(x, y);
                    b += c.B; g += c.G; r += c.R;
                }
            }

            byte[] expected = { (byte)(b / 144), (byte)(g / 144), (byte)(r / 144) };
            for (int y = 32; y < 44; y++)
            {
                for (int x = 52; x < 64; x++)
                {
                    int o = (y * 200 + x) * 4;
                    Assert.Equal(expected, pixels[o..(o + 3)]);
                }
            }
        });
    }

    [Fact]
    public void Arrow_PutsItsColourOnTheShaft()
    {
        Sta.Run(() =>
        {
            CapturedFrame frame = TestFrames.Gradient();
            var region = new PixelRect(0, 0, 200, 120);
            var arrow = new ArrowAnnotation { Start = new Point(20, 60), End = new Point(180, 60), Color = 0xFF0000FF, Thickness = 6 };
            byte[] pixels = TestImages.ReadBgra(AnnotationExporter.Render(frame, region, new Annotation[] { arrow }));

            int o = (60 * 200 + 120) * 4;
            Assert.Equal(new byte[] { 0xFF, 0x00, 0x00 }, pixels[o..(o + 3)]); // pure blue, BGR order
        });
    }

    [Fact]
    public void Text_LeavesInkWithinItsBounds()
    {
        Sta.Run(() =>
        {
            CapturedFrame frame = TestFrames.Gradient();
            var region = new PixelRect(0, 0, 200, 120);
            var text = new TextAnnotation { Origin = new Point(20, 20), Text = "HolyScreen", FontSize = 28, Color = 0xFFFFFFFF };
            byte[] before = TestImages.ReadBgra(frame.CreateBitmap(region));
            byte[] after = TestImages.ReadBgra(AnnotationExporter.Render(frame, region, new Annotation[] { text }));

            Rect bounds = AnnotationGeometry.Bounds(text);
            int changed = 0;
            for (int y = (int)bounds.Top; y < (int)bounds.Bottom; y++)
            {
                for (int x = (int)bounds.Left; x < Math.Min(200, (int)bounds.Right); x++)
                {
                    int o = (y * 200 + x) * 4;
                    if (before[o] != after[o] || before[o + 1] != after[o + 1]) changed++;
                }
            }

            Assert.True(changed > 200, $"only {changed} pixels changed under the text");
        });
    }
}
