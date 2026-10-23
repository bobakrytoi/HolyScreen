using System.Globalization;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Output;
using Xunit;

namespace HolyScreen.Tests;

/// <summary>Runs the real Windows OCR engine on rendered text (needs the en-US and ru OCR languages, present here).</summary>
public class OcrTests
{
    private static BitmapSource RenderText(string text, double size = 28)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.Black, 1.0);
        int width = (int)formatted.Width + 40;
        int height = (int)formatted.Height + 30;
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawText(formatted, new Point(20, 15));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    [Fact]
    public async Task ReadsEnglish()
    {
        BitmapSource image = Sta.Run(() => RenderText("HolyScreen copies text 2026"));
        OcrText? result = await OcrService.RecognizeAsync(image);
        Assert.NotNull(result);
        Assert.Contains("HolyScreen", result!.Text);
        Assert.Contains("2026", result.Text);
    }

    [Fact]
    public async Task ReadsRussian_PicksTheCyrillicEngine()
    {
        if (!OcrService.AvailableLanguages.Any(l => l.StartsWith("ru"))) return; // no Russian OCR pack on this machine
        BitmapSource image = Sta.Run(() => RenderText("Привет, мир! Скриншот готов"));
        var all = await OcrService.RecognizeAllAsync(image);
        string readings = string.Join(" | ", all.Select(c => $"{c.Language}: '{c.Text}' ({c.Words} words, score {OcrService.Score(c):F1})"));
        OcrText? result = await OcrService.RecognizeAsync(image);
        Assert.NotNull(result);
        Assert.True(result!.Language.StartsWith("ru"), readings);
        Assert.Contains("Привет", result.Text);
        Assert.Contains("мир", result.Text);
    }

    [Fact]
    public async Task BlankImage_HasNoText()
    {
        BitmapSource image = Sta.Run(() => RenderText("   "));
        Assert.Null(await OcrService.RecognizeAsync(image));
    }

    [Fact]
    public void Score_PrefersRealWordsOverLookAlikeGarbage()
    {
        // Actual readings of "Привет, мир! Скриншот готов" by the two engines on this machine.
        var byRu = new OcrText("Привет, мир! Скриншот готов", "ru", 1, 4);
        var byEn = new OcrText("npv1BeT, MVIP! CKPVIHUJOT rOTOB", "en-US", 1, 4);
        Assert.True(OcrService.Score(byRu) > OcrService.Score(byEn) * 1.5);

        // The same Latin text read by both engines: the Latin engine wins the tie.
        Assert.True(OcrService.Score(new OcrText("Copy text 2026", "en-US", 1, 3)) > OcrService.Score(new OcrText("Copy text 2026", "ru", 1, 3)));
    }
}
