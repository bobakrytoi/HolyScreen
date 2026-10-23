using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HolyScreen.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace HolyScreen.Output;

/// <summary>Recognised text plus which OCR language produced it.</summary>
internal sealed record OcrText(string Text, string Language, int Lines, int Words);

/// <summary>
/// Text recognition with the OCR engine built into Windows (Windows.Media.Ocr — offline and free). One engine reads
/// one language, so every installed OCR language is tried and the result that looks most like real text wins
/// (e.g. Russian text read by the English engine comes out as a short run of Latin look-alikes).
/// </summary>
internal static class OcrService
{
    public static IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public static async Task<OcrText?> RecognizeAsync(BitmapSource image) =>
        (await RecognizeAllAsync(image)).OrderByDescending(Score).FirstOrDefault();

    /// <summary>The reading of every installed OCR language (empty readings are dropped).</summary>
    internal static async Task<List<OcrText>> RecognizeAllAsync(BitmapSource image)
    {
        SoftwareBitmap bitmap = ToSoftwareBitmap(Prepare(image));
        var candidates = new List<OcrText>();

        foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
        {
            OcrEngine? engine = OcrEngine.TryCreateFromLanguage(language);
            if (engine is null) continue;

            try
            {
                OcrResult result = await engine.RecognizeAsync(bitmap);
                string text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
                int words = result.Lines.Sum(l => l.Words.Count);
                if (text.Trim().Length > 0) candidates.Add(new OcrText(text, language.LanguageTag, result.Lines.Count, words));
            }
            catch (Exception ex)
            {
                Log.Warn($"OCR with {language.LanguageTag} failed", ex);
            }
        }

        return candidates;
    }

    /// <summary>
    /// Rates how plausible a reading is, word by word. An engine reading a script it does not know still returns
    /// letters (Russian through the English engine gives "npv1BeT rOTOB"), but the words have shapes real text
    /// rarely has: digits inside words, case flipping mid-word, two alphabets in one word. Well-shaped words count
    /// by their length, a little more in the engine's own alphabet.
    /// </summary>
    internal static double Score(OcrText candidate)
    {
        bool cyrillicEngine = candidate.Language.Split('-')[0].ToLowerInvariant() is "ru" or "uk" or "be" or "bg" or "sr" or "kk" or "mk";
        double score = 0;
        foreach (Match match in Regex.Matches(candidate.Text, @"[\p{L}\p{Nd}]+"))
        {
            string word = match.Value;
            int cyrillic = 0, latin = 0, otherLetters = 0, digits = 0, upper = 0;
            bool zigzag = false;
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                if (char.IsDigit(c)) digits++;
                else if (c is >= 'Ѐ' and <= 'ӿ') cyrillic++;
                else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
                else otherLetters++;
                if (char.IsUpper(c)) upper++;
                if (i > 0 && char.IsLower(word[i - 1]) && char.IsUpper(c)) zigzag = true;
            }

            int letters = cyrillic + latin + otherLetters;
            double weight;
            if (letters == 0) weight = 0.8;                         // a number
            else if (cyrillic > 0 && latin > 0) weight = -1;        // two alphabets in one word: a misreading
            else if (digits > 0) weight = word.Length <= 4 ? 0.3 : -0.5; // "x64" happens, "npv1BeT" does not
            else if (zigzag) weight = 0.3;                          // camelCase exists, but rarely in prose
            else if (upper == letters && letters > 1) weight = 0.7; // ALL CAPS
            else weight = 1;

            bool native = cyrillicEngine ? cyrillic > 0 : latin > 0;
            if (weight > 0 && native) weight *= 1.1;
            score += weight * word.Length;
        }

        return score;
    }

    /// <summary>Upscales small images (the engine likes text at least ~20 px tall) and keeps within its size limit.</summary>
    private static BitmapSource Prepare(BitmapSource image)
    {
        double scale = image.PixelHeight < 300 || image.PixelWidth < 300 ? 2.5 : image.PixelHeight < 700 ? 1.5 : 1;
        double max = OcrEngine.MaxImageDimension;
        scale = Math.Min(scale, max / Math.Max(image.PixelWidth, image.PixelHeight));
        if (Math.Abs(scale - 1) < 0.01) return image;

        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        RenderOptions.SetBitmapScalingMode(scaled, BitmapScalingMode.HighQuality);
        scaled.Freeze();
        return scaled;
    }

    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource image)
    {
        BitmapSource bgra = ImageEncoding.Convert(image, PixelFormats.Bgra32);
        int stride = bgra.PixelWidth * 4;
        byte[] pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, bgra.PixelWidth, bgra.PixelHeight,
            BitmapAlphaMode.Premultiplied);
    }

    /// <summary>A one-line summary for the toast, e.g. "3 lines · 18 words".</summary>
    public static string Describe(OcrText text)
    {
        var sb = new StringBuilder();
        sb.Append(text.Lines).Append(text.Lines == 1 ? " line" : " lines");
        sb.Append(" · ").Append(text.Words).Append(text.Words == 1 ? " word" : " words");
        return sb.ToString();
    }
}
