using System;
using System.IO;
using System.Runtime.InteropServices;
using HolyScreen.Core;

namespace HolyScreen.UI.Common;

/// <summary>
/// A short, soft camera-shutter click, synthesised at runtime (no audio file to ship alongside the exe) and played
/// asynchronously through winmm.
/// </summary>
internal static class ShutterSound
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;
    private const int SampleRate = 44100;

    private static byte[]? _wav;      // kept alive while playing (async playback reads it from memory)
    private static double _wavVolume = -1;

    public static void Play(double volume)
    {
        if (volume <= 0) return;
        try
        {
            if (_wav is null || Math.Abs(_wavVolume - volume) > 0.001)
            {
                _wav = CreateWav(volume);
                _wavVolume = volume;
            }

            PlaySound(_wav, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not play the shutter sound", ex);
        }
    }

    /// <summary>A 16-bit mono PCM WAV: two filtered noise clicks with a short body, like a mirrorless shutter.</summary>
    internal static byte[] CreateWav(double volume)
    {
        int count = (int)(SampleRate * 0.13);
        var samples = new double[count];
        var random = new Random(7);
        double lowPass = 0;

        foreach (double start in new[] { 0.0, 0.048 })
        {
            int offset = (int)(start * SampleRate);
            for (int i = 0; offset + i < count; i++)
            {
                double t = i / (double)SampleRate;
                double noise = random.NextDouble() * 2 - 1;
                lowPass += 0.45 * (noise - lowPass);
                double click = lowPass * Math.Exp(-t / 0.007);
                double body = Math.Sin(2 * Math.PI * 140 * t) * 0.45 * Math.Exp(-t / 0.018);
                samples[offset + i] += (click + body) * (start == 0 ? 1.0 : 0.7);
            }
        }

        double peak = 0;
        foreach (double s in samples) peak = Math.Max(peak, Math.Abs(s));
        double gain = peak > 0 ? 0.65 * Math.Clamp(volume, 0, 1) / peak : 0;

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = count * 2;
            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1);          // PCM
            writer.Write((short)1);          // mono
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2);    // byte rate
            writer.Write((short)2);          // block align
            writer.Write((short)16);         // bits per sample
            writer.Write("data"u8.ToArray());
            writer.Write(dataBytes);
            foreach (double s in samples) writer.Write((short)Math.Round(Math.Clamp(s * gain, -1, 1) * short.MaxValue));
        }

        return stream.ToArray();
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
}
