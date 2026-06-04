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

        // Переиспользуемый буфер для маршалинга в AudioTrack.Write, чтобы не аллоцировать
        // новый массив на каждом аудиокадре (горячий путь воспроизведения).
        private byte[] writeBuffer = Array.Empty<byte>();

        public AudioTrackPipe() { }

        public bool Active => true;

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
