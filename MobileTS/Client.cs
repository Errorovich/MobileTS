using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.TV;
using Android.Provider;
using Android.Runtime;
using MobileTS.Audio;
using TSLib;
using TSLib.Audio;
using TSLib.Audio.Opus;
using TSLib.Full;
using TSLib.Messages;
using TSLib.Scheduler;

namespace MobileTS {
    internal static class Client {
        public static TsFullClient? Instance { get => client; }
        public static event Action<TsFullClient>? OnInstanceReady;
        public static event Action<VoiceActivationTrackerPipe.ClientVoiceStatus> OnClientIsTalkingChanged {
            add => voiceActivationTrackerPipe.OnClientIsTalkingChanged += value;
            remove => voiceActivationTrackerPipe.OnClientIsTalkingChanged -= value;
        }

        private static IdentityData identity;
        private static Thread clientThread;
        private static TsFullClient client;
        private static ContextWrapper context;
        private static VoiceActivationTrackerPipe voiceActivationTrackerPipe;
        private static DedicatedTaskScheduler clientScheduler;
        public static void Init(ContextWrapper contextWrapper) {
            context = contextWrapper;
            //identity = TsCrypt.GenerateNewIdentity();

            ISharedPreferences? sharedPreferences = context.GetSharedPreferences("ts_client", FileCreationMode.Private);

            if (sharedPreferences == null)
                return;

            string? privateKey = sharedPreferences.GetString("ts_private_key", null);
            if (privateKey == null)
                return;
            if (ulong.TryParse(sharedPreferences.GetString("ts_key_offset", null), out ulong keyOffset))
                identity = TsCrypt.LoadIdentity(privateKey, keyOffset).Value;
            else {
                identity = TsCrypt.GenerateNewIdentity();
                ISharedPreferencesEditor? editor = sharedPreferences.Edit();
                if (editor != null) {
                    editor.PutString("ts_private_key", identity.PrivateKeyString);
                    editor.PutString("ts_key_offset", identity.ValidKeyOffset.ToString());
                    editor.Commit();
                }
            }

            var audioManager = (AudioManager)context.GetSystemService("audio");

            var result = audioManager.RequestAudioFocus(
                null,
                Android.Media.Stream.Music,
                AudioFocus.Gain);
        }

        public static void SubscribeInstance(Action<TsFullClient> action) {
            if (Instance != null) {
                action(Instance);
                return;
            }

            void Handler(TsFullClient instance) {
                action(instance);
                OnInstanceReady -= Handler;
            }

            OnInstanceReady += Handler;
        }

        public static void UnsubscribeInstance(Action<TsFullClient> action) {
            if (Instance != null) {
                action(Instance);
                return;
            }
        }

        public static void Connect(ServerInfo serverInfo) => Connect(serverInfo.Address, serverInfo.Nickname, serverInfo.ServerPassword, serverInfo.DefaultChannel, serverInfo.DefaultChannelPassword);

        public static void Connect(string address, string? nickname = null, string? serverPassword = null, string? defaultChannel = null, string? defaultChannelPassword = null) {
            ConnectionDataFull conData = new ConnectionDataFull(
                address,
                identity,
                TsVersionSigned.VER_AND_3_5_0,
                nickname,
                serverPassword == null ? Password.Empty : Password.FromPlain(serverPassword),
                defaultChannel,
                defaultChannelPassword == null ? Password.Empty : Password.FromPlain(defaultChannelPassword));
            clientThread = new Thread(() => {
                DedicatedTaskScheduler.FromCurrentThread(() => ClientThread(conData));
            });
            clientThread.Start();
        }

        private static async void ClientThread(ConnectionDataFull conData) {
            clientScheduler = (DedicatedTaskScheduler)TaskScheduler.Current;
            client = new TsFullClient(clientScheduler);
            OnInstanceReady?.Invoke(client);

            AudioRecordPipe audioRecordPipe = new AudioRecordPipe();
            PreciseTimedPipe preciseTimedPipe = audioRecordPipe.Into(new PreciseTimedPipe(new SampleInfo(48000, 1, 16), TSLib.Helper.Id.Null));
            EncoderPipe encoderPipe = preciseTimedPipe.Chain(new EncoderPipe(Codec.OpusVoice));
            encoderPipe.Chain(client);

            DecoderPipe decoderPipe = client.Chain(new DecoderPipe());
            voiceActivationTrackerPipe = decoderPipe.Chain(new VoiceActivationTrackerPipe());
            AudioTrackPipe audioTrackPipe = voiceActivationTrackerPipe.Chain(new AudioTrackPipe());

            preciseTimedPipe.ReadBufferSize = 960 * 2;
            preciseTimedPipe.Paused = false;
            await client.Connect(conData);
        }

        public static Task<(bool ok, T[] data)> Invoke<T>(Func<TsFullClient, Task<R<T[], CommandError>>> action) {
            if (client == null || clientScheduler == null)
                return Task.FromResult<(bool, T[])>((false, Array.Empty<T>()));

            return clientScheduler.InvokeAsync(async () =>
            {
                var resp = await action(client);
                bool ok = resp.GetOk(out T[]? data);
                return (ok, data ?? Array.Empty<T>());
            });
        }

        public static Task<bool> Invoke(Func<TsFullClient, Task<E<CommandError>>> action) {
            if (client == null || clientScheduler == null)
                return Task.FromResult(false);

            return clientScheduler.InvokeAsync(async () =>
                (await action(client)).GetOk(out _)
            );
        }

        public static Task Invoke(Func<TsFullClient, Task> action) {
            if (client == null || clientScheduler == null)
                return Task.FromResult(false);

            return clientScheduler.InvokeAsync(async () =>
                await action(client)
            );
        }

        public static Task Disconnect() =>
            Invoke(c => c.Disconnect());
    }
}
