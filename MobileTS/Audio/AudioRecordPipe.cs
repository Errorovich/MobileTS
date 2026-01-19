using Android.Media;
using Android.Media.TV;
using TSLib.Audio;

namespace MobileTS.Audio
{
    public class AudioRecordPipe : IAudioPassiveProducer
    {
        private readonly AudioRecord audioRecord;
        private readonly Meta voiceMeta;
        public AudioRecordPipe()
        {
            voiceMeta = new Meta()
            {
                Out = new MetaOut()
                {
                    SendMode = TargetSendMode.Voice
                }
            };
            audioRecord = new AudioRecord(
                AudioSource.VoiceCommunication,
                48000,
                ChannelIn.Mono,
                Encoding.Pcm16bit,
                1920);
            audioRecord.StartRecording();
        }

        public int Read(byte[] buffer, int offset, int length, out Meta? meta)
        {
            meta = voiceMeta;
            return audioRecord.Read(buffer, offset, length);
        }

        public void Dispose() => audioRecord.Dispose();
    }
}
