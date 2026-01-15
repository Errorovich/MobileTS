using TSLib.Full;
using TSLib;
using TSLib.Scheduler;
using TSLib.Audio.Opus;
using Android.Media;
using Android;
using Android.Content.PM;

namespace MobileTS {
    [Activity(Label = "@string/app_name", MainLauncher = true)]
    public class MainActivity : Activity
    {
        const int FrameSamples = 960;                  // 20 ms
        const int FrameBytes = FrameSamples * 2;       // PCM16 mono

        byte[] readBuffer, encoderBuffer;
        protected override async void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            if (CheckSelfPermission(Manifest.Permission.RecordAudio)
            != Permission.Granted) {
                RequestPermissions([Manifest.Permission.RecordAudio], 1001);
            }

            var audioManager = (AudioManager)GetSystemService(AudioService);

            var result = audioManager.RequestAudioFocus(
                null,
                Android.Media.Stream.VoiceCall,
                AudioFocus.Gain);

            if (result != AudioFocusRequest.Granted)
                throw new Exception("Audio focus not granted");

            readBuffer = new byte[FrameBytes];
            encoderBuffer = new byte[4096]; // Opus output

            Thread thread = new Thread(() => {  
                DedicatedTaskScheduler.FromCurrentThread(() => Start());
            });
            thread.Start();

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.activity_main);
        }

        async void Start() {
            OpusEncoder encoder = OpusEncoder.Create(48000, 1, TSLib.Audio.Opus.Application.Voip);

            IdentityData identity = TsCrypt.GenerateNewIdentity();
            ConnectionDataFull conData = new ConnectionDataFull("79.174.93.181", identity, TsVersionSigned.VER_AND_3_5_0, "Kazah", Password.FromPlain("qwerty.xyz"));
            TsFullClient client = new TsFullClient((DedicatedTaskScheduler)TaskScheduler.Current);
            await client.Connect(conData);

            AudioRecord audioRecord = new AudioRecord(
                AudioSource.VoiceCommunication,
                48000,
                ChannelIn.Mono,
                Encoding.Pcm16bit,
                readBuffer.Length);

            if (audioRecord.State != State.Initialized)
                throw new Exception("AudioRecord init failed");

            audioRecord.StartRecording();
            while (true) {
                int read = audioRecord.Read(readBuffer, 0, readBuffer.Length);
                var encoded = encoder.Encode(readBuffer, FrameBytes, encoderBuffer);
                client.SendAudio(encoded, Codec.OpusVoice);
            }
        }
    }
}