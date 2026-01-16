using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.TV;
using Android.Provider;
using Android.Runtime;
using TSLib;
using TSLib.Audio;
using TSLib.Audio.Opus;
using TSLib.Full;
using TSLib.Messages;
using TSLib.Scheduler;

namespace MobileTS
{
    internal static class Client
    {
        static IdentityData identity;
        static Thread clientThread;
        static TsFullClient client;
        static ContextWrapper context;
        public static void Init(ContextWrapper contextWrapper)
        {
            context = contextWrapper;

            ISharedPreferences sharedPreferences = context.GetSharedPreferences("ts_client", FileCreationMode.Private);

            if (sharedPreferences == null)
                return;

            string? privateKey = sharedPreferences.GetString("ts_private_key", null);
            if(privateKey != null)
                identity = new IdentityData(new Org.BouncyCastle.Math.BigInteger(privateKey));
            else
            {
                identity = TsCrypt.GenerateNewIdentity();
                ISharedPreferencesEditor editor = sharedPreferences.Edit();
                if(editor != null)
                {
                    editor.PutString("ts_private_key", identity.PrivateKeyString);
                    editor.Commit();
                }
            }

            var audioManager = (AudioManager)context.GetSystemService("audio");

            var result = audioManager.RequestAudioFocus(
                null,
                Android.Media.Stream.VoiceCall,
                AudioFocus.Gain);
        }

        public static void Connect(string address, string? nickname = null, string? serverPassword = null, string? defaultChannel = null, string? defaultChannelPassword = null)
        {
            ConnectionDataFull conData = new ConnectionDataFull(
                address, 
                identity, 
                TsVersionSigned.VER_AND_3_5_0, 
                nickname, 
                serverPassword == null ? null : Password.FromPlain(serverPassword), 
                defaultChannel,
                defaultChannelPassword == null ? null : Password.FromPlain(defaultChannelPassword));
            clientThread = new Thread(() => {
                DedicatedTaskScheduler.FromCurrentThread(() => ClientThread(conData));
            });
            clientThread.Start();
        }

        public static async void ClientThread(ConnectionDataFull conData)
        {
            client = new TsFullClient((DedicatedTaskScheduler)TaskScheduler.Current);
            await client.Connect(conData);

            byte[] readBuffer = new byte[1920];
            byte[] encoderBuffer = new byte[4096];

            OpusEncoder encoder = OpusEncoder.Create(48000, 1, TSLib.Audio.Opus.Application.Voip);

            AudioRecord audioRecord = new AudioRecord(
                AudioSource.VoiceCommunication,
                48000,
                ChannelIn.Mono,
                Encoding.Pcm16bit,
                readBuffer.Length);
            audioRecord.StartRecording();

            while (client.Connected)
            {
                audioRecord.Read(readBuffer, 0, readBuffer.Length);
                var encoded = encoder.Encode(readBuffer, readBuffer.Length, encoderBuffer);
                client.SendAudio(encoded, Codec.OpusVoice);
            }
        }

        public static async Task<(bool, ChannelListResponse[])> GetChannels()
        {
            bool ok = (await client.ChannelList()).GetOk(out ChannelListResponse[]? response);
            return (ok, response!);
        }
        public static async Task<(bool, ClientList[])> GetClients()
        {
            bool ok = (await client.ClientList()).GetOk(out ClientList[]? response);
            return (ok, response!);
        }
        public static async Task<bool> Move(ChannelId channel)
        {
            return (await client.ClientMove(client.ClientId, channel)).GetOk(out _);
        }
        public static async void Disconnect()
        {
            await client.Disconnect();
        }
    }
}
