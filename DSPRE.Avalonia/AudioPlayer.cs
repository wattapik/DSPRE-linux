using System.Diagnostics;

namespace DSPRE.Avalonia;

/// <summary>Plays a WAV file through whatever the system has for it (Avalonia has no audio of its own): PulseAudio/PipeWire,
/// ALSA or ffplay on Linux, afplay on macOS, the Windows media player class on Windows. A new sound stops the one before it.</summary>
public static class AudioPlayer
{
    private static Process? playing;
    private static string? playingFile;

    /// <summary>Loudness of what is played next, 0 (silent) to 1 (as rendered); the player programs have no common volume switch, so the samples are scaled.</summary>
    public static double Volume { get; set; } = 0.7;

    /// <returns>Null when playing began, otherwise why it could not.</returns>
    public static string? Play(byte[] wav)
    {
        Stop();
        string file = Path.Combine(Path.GetTempPath(), $"dspre-sound-{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(file, Scaled(wav, Volume));

        (string Program, string[] Arguments)[] candidates = OperatingSystem.IsWindows()
            ? [("powershell", ["-NoProfile", "-Command", $"(New-Object Media.SoundPlayer '{file}').PlaySync()"])]
            : OperatingSystem.IsMacOS()
                ? [("afplay", [file])]
                : [("paplay", [file]), ("pw-play", [file]), ("aplay", ["-q", file]), ("ffplay", ["-nodisp", "-autoexit", "-loglevel", "quiet", file])];

        foreach ((string program, string[] arguments) in candidates)
        {
            try
            {
                var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                Process? process = Process.Start(start);
                if (process is null) continue;
                playing = process;
                playingFile = file;
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) =>
                {
                    try { if (File.Exists(file)) File.Delete(file); } catch (IOException) { }
                };
                return null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Not installed; try the next one.
            }
        }

        File.Delete(file);
        return OperatingSystem.IsLinux() ? "No audio player was found; install paplay (PulseAudio/PipeWire), aplay or ffplay." : "No audio player was available.";
    }

    private static byte[] Scaled(byte[] wav, double volume)
    {
        if (volume >= 0.999) return wav;
        byte[] copy = (byte[])wav.Clone();
        int at = 12;
        while (at + 8 <= copy.Length)
        {
            int size = BitConverter.ToInt32(copy, at + 4);
            if (copy[at] == 'd' && copy[at + 1] == 'a' && copy[at + 2] == 't' && copy[at + 3] == 'a')
            {
                int end = Math.Min(copy.Length, at + 8 + size);
                for (int i = at + 8; i + 1 < end; i += 2)
                {
                    short sample = BitConverter.ToInt16(copy, i);
                    short scaled = (short)Math.Clamp((int)Math.Round(sample * volume), short.MinValue, short.MaxValue);
                    copy[i] = (byte)scaled;
                    copy[i + 1] = (byte)(scaled >> 8);
                }

                break;
            }

            at += 8 + size + (size & 1);
        }

        return copy;
    }

    public static void Stop()
    {
        try
        {
            if (playing is { HasExited: false }) playing.Kill();
        }
        catch (InvalidOperationException)
        {
        }

        playing = null;
        if (playingFile is not null)
        {
            try { if (File.Exists(playingFile)) File.Delete(playingFile); } catch (IOException) { }
            playingFile = null;
        }
    }
}
