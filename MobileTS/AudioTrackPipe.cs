using Android.Media;
using TSLib;
using TSLib.Audio;

namespace MobileTS {
    public class AudioTrackPipe : IAudioPassiveConsumer {
        private static readonly AudioAttributes audioAttributes = new AudioAttributes.Builder()
            .SetUsage(AudioUsageKind.Media)
            .SetContentType(AudioContentType.Speech)
            .Build();
        private static readonly AudioFormat audioFormat = new AudioFormat.Builder()
                .SetEncoding(Encoding.Pcm16bit)!
                .SetSampleRate(48000)
                .SetChannelMask(ChannelOut.Stereo)
                .Build();

        private readonly Dictionary<ClientId, AudioTrack> audioTracks = new();
        public AudioTrackPipe() {

        }

        public bool Active => true;

        public void Write(Span<byte> data, Meta? meta) {
            if (meta is null)
                return;

            var audioTrack = GetAudioTrack(meta.In.Sender);
            audioTrack.Write(data.ToArray(), 0, data.Length);
        }

        private AudioTrack GetAudioTrack(ClientId clientId) {
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
    }
}
