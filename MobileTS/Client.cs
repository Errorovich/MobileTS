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

        /// <summary>
        /// Срабатывает на потоке планировщика, когда в <see cref="TsFullClient.Book"/> изменилось
        /// дерево каналов/клиентов (зашёл/вышел/переместился клиент, изменился канал и т.п.).
        /// UI должен пересобрать список через <see cref="GetBookSnapshot"/>.
        /// </summary>
        public static event Action? OnBookChanged;

        private static IdentityData? identity;
        private static bool initialized;

        // Настоящее имя сервера из initserver. Не берём из Book.Server.Name: генерируемый
        // UpdateInitServer перезатирает его ником клиента (полем Name поверх ServerName).
        private static string? serverName;

        private static Thread? clientThread;
        private static TsFullClient? client;
        private static ContextWrapper? context;
        private static DedicatedTaskScheduler? clientScheduler;

        // Сегменты аудиоконвейера, которые нужно освобождать при дисконнекте.
        private static AudioRecordPipe? audioRecordPipe;
        private static PreciseTimedPipe? preciseTimedPipe;
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

            // Грузим сохранённую identity только если есть и приватный ключ, и его offset.
            // Иначе (первый запуск либо повреждённые данные) генерируем новую и сохраняем.
            string? privateKey = sharedPreferences.GetString("ts_private_key", null);
            if (privateKey != null
                && ulong.TryParse(sharedPreferences.GetString("ts_key_offset", null), out ulong keyOffset)) {
                identity = TsCrypt.LoadIdentity(privateKey, keyOffset).Value;
            }
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
            preciseTimedPipe = audioRecordPipe.Into(new PreciseTimedPipe(new SampleInfo(SampleRate, 1, 16), TSLib.Helper.Id.Null));
            EncoderPipe encoderPipe = preciseTimedPipe.Chain(new EncoderPipe(Codec.OpusVoice));
            encoderPipe.Chain(localClient);

            DecoderPipe decoderPipe = localClient.Chain(new DecoderPipe());
            voiceActivationTrackerPipe = decoderPipe.Chain(new VoiceActivationTrackerPipe());
            audioTrackPipe = voiceActivationTrackerPipe.Chain(new AudioTrackPipe());

            // Пробрасываем события говорения через статическое событие Client, чтобы подписки
            // не терялись при пересоздании конвейера.
            voiceActivationTrackerPipe.OnClientIsTalkingChanged += status => OnClientIsTalkingChanged?.Invoke(status);

            // Любое изменение дерева каналов/клиентов в Book — сигналим UI. Подписываемся на
            // OnEach*, т.к. Book обновляется ровно перед ними (батч-события On* идут до апдейта).
            static void RaiseBookChanged<T>(object? sender, T e) => OnBookChanged?.Invoke();
            localClient.OnEachChannelListFinished += RaiseBookChanged;
            localClient.OnEachClientEnterView += RaiseBookChanged;
            localClient.OnEachClientLeftView += RaiseBookChanged;
            localClient.OnEachClientMoved += RaiseBookChanged;
            localClient.OnEachChannelCreated += RaiseBookChanged;
            localClient.OnEachChannelDeleted += RaiseBookChanged;
            localClient.OnEachChannelEdited += RaiseBookChanged;
            localClient.OnEachChannelMoved += RaiseBookChanged;

            // Имя сервера берём из initserver напрямую (ServerName), т.к. в Book оно затирается ником.
            localClient.OnEachInitServer += (sender, e) => {
                serverName = e.ServerName;
                OnBookChanged?.Invoke();
            };

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

        /// <summary>
        /// Снимок дерева сервера из <see cref="TsFullClient.Book"/>. Полный клиент не отвечает на
        /// server-query команды (channellist/clientlist) — состояние строится из нотификаций в Book,
        /// читать который нужно на потоке планировщика.
        /// </summary>
        public static Task<(string serverName, TSLib.Full.Book.Channel[] channels, TSLib.Full.Book.Client[] clients)> GetBookSnapshot() {
            var c = client;
            var scheduler = clientScheduler;
            if (c == null || scheduler == null)
                return Task.FromResult(("", Array.Empty<TSLib.Full.Book.Channel>(), Array.Empty<TSLib.Full.Book.Client>()));

            return scheduler.Invoke(() => (
                serverName ?? "",
                c.Book.Channels.Values.ToArray(),
                c.Book.Clients.Values.ToArray()
            ));
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

            // Сначала останавливаем поток захвата (Dispose делает Join таймер-потока), затем сразу
            // освобождаем микрофон — до (возможно долгого) дисконнекта клиента, иначе AudioRecord
            // остаётся захваченным и системный индикатор микрофона продолжает гореть.
            preciseTimedPipe?.Dispose();
            audioRecordPipe?.Dispose();

            if (c != null && scheduler != null) {
                try {
                    await scheduler.InvokeAsync(() => c.Disconnect());
                }
                catch {
                    // игнорируем — всё равно освобождаем ресурсы ниже
                }
                c.Dispose();
            }

            // Трек воспроизведения освобождаем после дисконнекта: клиент мог дописывать
            // декодированный звук в конвейер вплоть до завершения соединения.
            audioTrackPipe?.Dispose();

            // Завершает цикл DoWork выделенного потока, созданного через FromCurrentThread.
            scheduler?.Dispose();

            client = null;
            clientScheduler = null;
            clientThread = null;
            serverName = null;
            audioRecordPipe = null;
            preciseTimedPipe = null;
            audioTrackPipe = null;
            voiceActivationTrackerPipe = null;
            OnInstanceReady = null;
        }

        private const int SampleRate = 48000;
        private const int FrameSize = 960;
    }
}
