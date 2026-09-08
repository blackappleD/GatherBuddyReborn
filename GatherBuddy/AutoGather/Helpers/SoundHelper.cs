using System;
using System.IO;
using System.Media;
using System.Reflection;
using System.Threading.Tasks;
using CSCore;
using CSCore.Codecs.WAV;
using CSCore.SoundOut;

namespace GatherBuddy.AutoGather.Helpers;

public class SoundHelper
{
    private const string SoundResource = "GatherBuddy.CustomInfo.honk-sound.wav";

    public void StartHonkSoundTask(int repeatCount)
        => StartCompletionSoundTask(repeatCount, GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume);

    /// <summary>
    /// Plays the embedded completion sound without requiring an AutoGather instance.
    /// This is also used by the crafting list and crafting list queue completion paths.
    /// </summary>
    public static void StartCompletionSoundTask(int repeatCount, int volumePercent)
    {
        if (repeatCount <= 0 || volumePercent <= 0)
            return;

        Task.Run(() => PlayHonkSound(repeatCount, volumePercent));
    }

    private static void PlayHonkSound(int repeatCount, int volumePercent)
    {
        try
        {
            var       assembly       = Assembly.GetExecutingAssembly();
            using var resourceStream = assembly.GetManifestResourceStream(SoundResource);
            if (resourceStream == null)
                throw new FileNotFoundException($"Embedded resource {SoundResource} not found.");

            using var ms = new MemoryStream();
            resourceStream.CopyTo(ms);
            var soundData = ms.ToArray(); // Keep a copy of the sound's bytes

            for (int i = 0; i < repeatCount; i++)
            {
                using var audioStream = new MemoryStream(soundData); // Fresh each time
                using var soundSource = new WaveFileReader(audioStream).ToSampleSource().ToMono();
                using var soundOut    = new WasapiOut();
                soundOut.Initialize(soundSource.ToWaveSource());
                soundOut.Volume = Math.Clamp(volumePercent, 0, 100) / 100f;

                soundOut.Play();
                while (soundOut.PlaybackState == PlaybackState.Playing)
                    Task.Delay(10).Wait();

                Task.Delay(200).Wait();
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Error during honk: {ex}");
        }
    }

    private void PlaySound(Stream stream)
    { }
}
