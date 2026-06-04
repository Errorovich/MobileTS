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
        public static TsFullClient? Instance => client;
        public static event Action<TsFullClient>? OnInstanceReady;

        /// <summary>
        /// Срабатывает, когда клиент начинает/прекращает говорить. Подписка живёт между
        /// переподключениями: каждый новый конвейер пробрасывает события сюда.
        /// </summary>
        public static event Action<VoiceActivationTrackerPipe.ClientVoiceStatus>? OnClientIsTalkingChanged;

        private static IdentityData? identity;
        private static bool initialized;

        private static Thread? clientThread;
        private static TsFullClient? client;
        private static ContextWrapper? context;
        private static DedicatedTaskScheduler? clientScheduler;

        // Сегменты аудиоконвейера, которые нужно освобождать при дисконнекте.
        private static AudioRecordPipe? audioRecordPipe;
        private static AudioTrackPipe? audioTrackPipe;
        private static VoiceActivationTrackerPipe? voiceActivationTrackerPipe;

        public static void Init(ContextWrapper contextWrapper) {
            // Init вызывается и из ServersListActivity, и из ClientService — выполняем только один раз.
            if (initialized)
                return;
            initialized = true;

            context = contextWrapper;

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

            var audioManager = (AudioManager?)context.GetSystemService(Context.AudioService);
            audioManager?.RequestAudioFocus(null, Android.Media.Stream.Music, AudioFocus.Gain);
        }

        /// <summary>
        /// Вызывает <paramref name="action"/> сразу, если клиент уже создан, иначе — один раз
        /// при готовности экземпляра.
        /// </summary>
        public static void SubscribeInstance(Action<TsFullClient> action) {
            if (Instance != null) {
                action(Instance);
                return;
            }

            void Handler(TsFullClient instance) {
                OnInstanceReady -= Handler;
                action(instance);
            }

            OnInstanceReady += Handler;
        }

        public static void Connect(ServerInfo serverInfo) => Connect(serverInfo.Address, serverInfo.Nickname, serverInfo.ServerPassword, serverInfo.DefaultChannel, serverInfo.DefaultChannelPassword);

        public static void Connect(string address, string? nickname = null, string? serverPassword = null, string? defaultChannel = null, string? defaultChannelPassword = null) {
            // Защита от повторного подключения поверх живого соединения (утечка потока/микрофона).
            if (client != null || clientThread != null)
                return;

            if (identity == null)
                return;

            ConnectionDataFull conData = new ConnectionDataFull(
                address,
                identity,
                TsVersionSigned.VER_AND_3_5_0,
                nickname,
                serverPassword == null ? Password.Empty : Password.FromPlain(serverPassword),
                defaultChannel,
                defaultChannelPassword == null ? Password.Empty : Password.FromPlain(defaultChannelPassword));
            clientThread = new Thread(() => {
                // ClientThread — async; продолжения после await пампятся тем же планировщиком в DoWork.
                DedicatedTaskScheduler.FromCurrentThread(() => _ = ClientThread(conData));
            });
            clientThread.Start();
        }

        private static async Task ClientThread(ConnectionDataFull conData) {
            clientScheduler = (DedicatedTaskScheduler)TaskScheduler.Current;
            var localClient = new TsFullClient(clientScheduler);
            client = localClient;

            audioRecordPipe = new AudioRecordPipe();
            PreciseTimedPipe preciseTimedPipe = audioRecordPipe.Into(new PreciseTimedPipe(new SampleInfo(SampleRate, 1, 16), TSLib.Helper.Id.Null));
            EncoderPipe encoderPipe = preciseTimedPipe.Chain(new EncoderPipe(Codec.OpusVoice));
            encoderPipe.Chain(localClient);

            DecoderPipe decoderPipe = localClient.Chain(new DecoderPipe());
            voiceActivationTrackerPipe = decoderPipe.Chain(new VoiceActivationTrackerPipe());
            audioTrackPipe = voiceActivationTrackerPipe.Chain(new AudioTrackPipe());

            // Пробрасываем события говорения через статическое событие Client, чтобы подписки
            // не терялись при пересоздании конвейера.
            voiceActivationTrackerPipe.OnClientIsTalkingChanged += status => OnClientIsTalkingChanged?.Invoke(status);

            preciseTimedPipe.ReadBufferSize = FrameSize * 2;
            preciseTimedPipe.Paused = false;

            OnInstanceReady?.Invoke(localClient);

            try {
                await localClient.Connect(conData);
            }
            catch {
                // Ошибка установления соединения; статус уйдёт через OnStatusChangedEvent,
                // ресурсы освободит Disconnect.
            }
        }

        public static Task<(bool ok, T[] data)> Invoke<T>(Func<TsFullClient, Task<R<T[], CommandError>>> action) {
            var c = client;
            var scheduler = clientScheduler;
            if (c == null || scheduler == null)
                return Task.FromResult<(bool, T[])>((false, Array.Empty<T>()));

            return scheduler.InvokeAsync(async () =>
            {
                var resp = await action(c);
                bool ok = resp.GetOk(out T[]? data);
                return (ok, data ?? Array.Empty<T>());
            });
        }

        public static Task<bool> Invoke(Func<TsFullClient, Task<E<CommandError>>> action) {
            var c = client;
            var scheduler = clientScheduler;
            if (c == null || scheduler == null)
                return Task.FromResult(false);

            return scheduler.InvokeAsync(async () =>
                (await action(c)).GetOk(out _)
            );
        }

        public static Task Invoke(Func<TsFullClient, Task> action) {
            var c = client;
            var scheduler = clientScheduler;
            if (c == null || scheduler == null)
                return Task.CompletedTask;

            return scheduler.InvokeAsync(async () =>
                await action(c)
            );
        }

        /// <summary>
        /// Корректно завершает соединение: graceful-дисконнект, остановка микрофона,
        /// освобождение аудиотреков, завершение выделенного потока планировщика.
        /// </summary>
        public static async Task Disconnect() {
            var c = client;
            var scheduler = clientScheduler;

            if (c != null && scheduler != null) {
                try {
                    await scheduler.InvokeAsync(() => c.Disconnect());
                }
                catch {
                    // игнорируем — всё равно освобождаем ресурсы ниже
                }
                c.Dispose();
            }

            audioRecordPipe?.Dispose();
            audioTrackPipe?.Dispose();

            // Завершает цикл DoWork выделенного потока, созданного через FromCurrentThread.
            scheduler?.Dispose();

            client = null;
            clientScheduler = null;
            clientThread = null;
            audioRecordPipe = null;
            audioTrackPipe = null;
            voiceActivationTrackerPipe = null;
            OnInstanceReady = null;
        }

        private const int SampleRate = 48000;
        private const int FrameSize = 960;
    }
}
