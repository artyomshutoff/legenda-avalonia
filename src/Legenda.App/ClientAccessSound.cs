using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Legenda.App;

/// <summary>Short result cues played through the Windows audio output.</summary>
public static class ClientAccessSound
{
    private static readonly byte[] Accepted = CreateWave((660, 120), (880, 180));
    private static readonly byte[] Denied = CreateWave((330, 190), (220, 260));
    private static readonly byte[] Unknown = CreateWave((520, 100), (520, 100), (520, 100));
    private static int _playing;

    public static void Play(string state)
    {
        if (!OperatingSystem.IsWindows() || Interlocked.CompareExchange(ref _playing, 1, 0) != 0) return;
        var wave = state switch { "accepted" => Accepted, "denied" => Denied, _ => Unknown };
        _ = Task.Run(() =>
        {
            try
            {
                // Synchronous playback keeps the marshalled memory alive until playback ends.
                // SND_MEMORY | SND_NODEFAULT: never substitute a system alert on failure.
                PlaySound(wave, IntPtr.Zero, 0x0004 | 0x0002);
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            finally { Volatile.Write(ref _playing, 0); }
        });
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);

    private static byte[] CreateWave(params (int Frequency, int Milliseconds)[] notes)
    {
        const int sampleRate = 22050;
        using var samples = new MemoryStream();
        using (var writer = new BinaryWriter(samples, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            foreach (var (frequency, milliseconds) in notes)
            {
                var count = sampleRate * milliseconds / 1000;
                var fade = sampleRate / 100; // Fade edges to avoid clicks.
                for (var i = 0; i < count; i++)
                {
                    var envelope = Math.Min(1.0, Math.Min((double)i / fade, (double)(count - 1 - i) / fade));
                    writer.Write((short)(short.MaxValue * 0.28 * envelope * Math.Sin(2 * Math.PI * frequency * i / sampleRate)));
                }
                for (var i = 0; i < sampleRate * 60 / 1000; i++) writer.Write((short)0);
            }
        }
        using var result = new MemoryStream();
        using var header = new BinaryWriter(result);
        header.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        header.Write(36 + (int)samples.Length);
        header.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        header.Write(16); header.Write((short)1); header.Write((short)1);
        header.Write(sampleRate); header.Write(sampleRate * 2);
        header.Write((short)2); header.Write((short)16);
        header.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        header.Write((int)samples.Length); header.Write(samples.ToArray());
        return result.ToArray();
    }
}
