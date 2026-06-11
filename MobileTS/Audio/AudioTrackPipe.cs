using Android.Media;
using TSLib;
using TSLib.Audio;

namespace MobileTS.Audio
{
    public class AudioTrackPipe : IAudioPassiveConsumer, IDisposable
    {
        private static readonly AudioAttributes audioAttributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)!
            .SetContentType(AudioContentType.Speech)!
            .Build()!;
        private static readonly AudioFormat audioFormat = new AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(48000)!
                .SetChannelMask(ChannelOut.Stereo)
                .Build()!;

        private readonly Dictionary<ClientId, AudioTrack> audioTracks = new();

        // Желаемая громкость по клиенту (0..1). Применяется к существующему треку и к вновь
        // создаваемому в GetAudioTrack (трек создаётся лениво при первом кадре от клиента).
        private readonly Dictionary<ClientId, float> volumes = new();

        // Переиспользуемый буфер для маршалинга в AudioTrack.Write, чтобы не аллоцировать
        // новый массив на каждом аудиокадре (горячий путь воспроизведения).
        private byte[] writeBuffer = Array.Empty<byte>();

        public AudioTrackPipe() { }

        public bool Active => true;

        // Громкость собеседника 0..1. Сохраняем и применяем к уже созданному треку.
        public void SetClientVolume(ClientId clientId, float volume) {
            volume = Math.Clamp(volume, 0f, 1f);
            volumes[clientId] = volume;
            if (audioTracks.TryGetValue(clientId, out var track))
                track.SetVolume(volume);
        }

        public float GetClientVolume(ClientId clientId) =>
            volumes.TryGetValue(clientId, out var v) ? v : 1f;

        public void Write(Span<byte> data, Meta? meta)
        {
            if (meta is null)
                return;

            var audioTrack = GetAudioTrack(meta.In.Sender);

            // Растим буфер только при необходимости; Opus-кадры фикс. размера → аллокация один раз.
            if (writeBuffer.Length < data.Length)
                writeBuffer = new byte[data.Length];

            data.CopyTo(writeBuffer);
            audioTrack.Write(writeBuffer, 0, data.Length);
        }

        private AudioTrack GetAudioTrack(ClientId clientId)
        {
            if (audioTracks.TryGetValue(clientId, out var audioTrack))
                return audioTrack;

            audioTrack = new AudioTrack.Builder()
                .SetAudioAttributes(audioAttributes)
                .SetAudioFormat(audioFormat)
                .SetBufferSizeInBytes(4096 * 8)
                .SetTransferMode(AudioTrackMode.Stream)
                .Build();
            audioTracks.Add(clientId, audioTrack);
            // Применяем заранее заданную громкость (если пользователь её менял до появления трека).
            if (volumes.TryGetValue(clientId, out var volume))
                audioTrack.SetVolume(volume);
            audioTrack.Play();
            return audioTrack;
        }

        public void Dispose()
        {
            foreach (var audioTrack in audioTracks.Values)
            {
                audioTrack.Stop();
                audioTrack.Release();
                audioTrack.Dispose();
            }
            audioTracks.Clear();
        }
    }
}
