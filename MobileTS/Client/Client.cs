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
using TSLib.Commands;
using TSLib.Full;
using TSLib.Messages;
using TSLib.Scheduler;

namespace MobileTS {
    internal static partial class Client {
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

        // 0 — соединение живо/не начато, 1 — дисконнект идёт или завершён. Дисконнект может прийти
        // одновременно из ClientThread (после неудачного/прерванного Connect) и из
        // ClientService.OnDestroy (выход/отмена) — очистку выполняем строго один раз.
        private static int disconnecting;

        // Настоящее имя сервера из initserver. Не берём из Book.Server.Name: генерируемый
        // UpdateInitServer перезатирает его ником клиента (полем Name поверх ServerName).
        private static string? serverName;

        // Адрес текущего подключения — ключ для кэша иконки сервера на диске.
        private static string? currentAddress;

        // Локальное состояние AFK для тумблера (сервер не возвращает наш away отдельным геттером).
        private static bool awayActive;

        // AFK по клиентам. Book.Client.AwayMessage НЕ сбрасывается в null при снятии AFK (баг слияния
        // в M2B: AwayCuFun возвращает null и при «away=false», и при «нет изменения», а апдейт
        // применяет только non-null), поэтому ведём состояние сами по флагу IsAway из нотификаций.
        private static readonly Dictionary<ClientId, bool> awayClients = new();

        public static bool IsClientAway(ClientId id) {
            lock (awayClients)
                return awayClients.TryGetValue(id, out var away) && away;
        }

        private static void SetClientAway(ClientId id, bool away) {
            lock (awayClients)
                awayClients[id] = away;
        }

        // Кто сейчас говорит. Событие OnClientIsTalkingChanged мгновенно (за кадр), но при пересборке
        // дерева (NotifyDataSetChanged) новые ClientItem стартуют с IsTalking=false и зелёная подсветка
        // сбрасывалась бы до следующего кадра — поэтому держим состояние здесь и сеем из него.
        private static readonly Dictionary<ClientId, bool> talkingClients = new();

        public static bool IsClientTalking(ClientId id) {
            lock (talkingClients)
                return talkingClients.TryGetValue(id, out var talking) && talking;
        }

        private static void SetClientTalking(ClientId id, bool talking) {
            lock (talkingClients)
                talkingClients[id] = talking;
        }

        private static Thread? clientThread;
        private static TsFullClient? client;
        private static ContextWrapper? context;
        private static DedicatedTaskScheduler? clientScheduler;

        // Весь аудиоконвейер клиента (захват + воспроизведение + управление активацией/заглушками).
        private static Audio? audio;

        public static void Init(ContextWrapper contextWrapper) {
            // Init вызывается и из MainActivity, и из ClientService — выполняем только один раз.
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

            // Новая сессия — снимаем флаг дисконнекта (предыдущая очистка к этому моменту завершена,
            // т.к. client/clientThread выше уже null).
            disconnecting = 0;
            currentAddress = address;
            // AFK — глобальная настройка: восстанавливаем тумблер из сохранённого состояния.
            awayActive = context != null && AppSettings.GetAfk(context);

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

            // Весь аудиоконвейер вынесен в Client.Audio. Режим активации, порог и задержку
            // деактивации берём из настроек.
            var localAudio = new Audio();
            localAudio.OnClientIsTalkingChanged += status => {
                SetClientTalking(status.Id, status.Active);
                OnClientIsTalkingChanged?.Invoke(status);
            };
            // Свой микрофон активен — подсвечиваем себя в дереве тем же событием, что и остальных.
            localAudio.OnLocalTalkingChanged += active => {
                var me = client?.ClientId ?? ClientId.Null;
                SetClientTalking(me, active);
                OnClientIsTalkingChanged?.Invoke(new VoiceActivationTrackerPipe.ClientVoiceStatus(me, active));
            };
            localAudio.Build(
                localClient,
                context != null ? AppSettings.GetActivationMode(context) : ActivationMode.Voice,
                context != null ? AppSettings.GetVoiceThreshold(context) : AppSettings.DefaultVoiceThreshold,
                context != null ? AppSettings.GetVoiceDeactivationDelay(context) : AppSettings.DefaultVoiceDeactivationDelayMs);
            audio = localAudio;

            // Восстанавливаем сохранённое (глобальное) состояние звука локально в конвейере сразу —
            // отправку на сервер делаем после успешного подключения (ниже).
            if (context != null) {
                localAudio.SetMicMuted(AppSettings.GetMicMuted(context));
                localAudio.SetSoundMuted(AppSettings.GetSoundMuted(context));
            }

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
            // Изменения свойств клиента (мьют микрофона/звука, AFK, иконка) — тоже пересобираем дерево,
            // иначе иконки статусов не перерисовываются.
            localClient.OnEachClientUpdated += RaiseBookChanged;
            // Отслеживаем AFK сами: IsAway приходит в апдейте только при смене статуса (иначе null).
            localClient.OnEachClientUpdated += (sender, e) => {
                if (e.IsAway is bool away)
                    SetClientAway(e.ClientId, away);
            };

            // При входе клиента: применяем сохранённые по UID громкость и мьют + начальный AFK.
            localClient.OnEachClientEnterView += (sender, e) => {
                SetClientAway(e.ClientId, e.IsAway);
                ApplySavedClientSettings(localClient, e.ClientId);
            };

            // Чат текущего канала: входящие сообщения и очистка истории при переезде.
            HookChat(localClient);

            // Имя сервера берём из initserver напрямую (ServerName), т.к. в Book оно затирается ником.
            localClient.OnEachInitServer += (sender, e) => {
                serverName = e.ServerName;
                OnBookChanged?.Invoke();
                // Иконка сервера доступна после initserver — качаем и кэшируем на диск.
                _ = DownloadServerIcon();
            };

            OnInstanceReady?.Invoke(localClient);

            bool connected;
            try {
                // Connect не бросает при обычном отказе (неверный адрес/таймаут/пароль) — он
                // возвращает ошибку в результате. Бросает лишь на некорректных аргументах.
                connected = (await localClient.Connect(conData)).Ok;
            }
            catch {
                connected = false;
            }

            // Сообщаем серверу восстановленное состояние звука/AFK (мы на потоке планировщика —
            // вызываем клиент напрямую), чтобы остальные участники сразу видели наши иконки.
            if (connected && context != null) {
                bool micMuted = AppSettings.GetMicMuted(context);
                bool soundMuted = AppSettings.GetSoundMuted(context);
                bool afk = AppSettings.GetAfk(context);
                string? afkMsg = AppSettings.GetAfkMessage(context);
                try {
                    await localClient.SendVoid(new TsCommand("clientupdate") {
                        { "client_input_muted", micMuted },
                        { "client_output_muted", soundMuted },
                        { "client_away", afk },
                        { "client_away_message", afk ? (afkMsg ?? "") : "" },
                    });
                }
                catch {
                    // не критично — локальное состояние уже применено
                }

                // Подгружаем историю чата канала, в котором оказались после подключения.
                RefreshCurrentChannel(localClient);
            }

            // При неудаче освобождаем ресурсы и сбрасываем статические поля тем же путём, что и
            // обычный дисконнект. Иначе client/clientThread остаются заняты, и повторное подключение
            // зависает: Connect выходит рано, а статус соединения уже никогда не сменится.
            if (!connected)
                await Disconnect();
        }

        /// <summary>
        /// Снимок дерева сервера из <see cref="TsFullClient.Book"/>. Полный клиент не отвечает на
        /// server-query команды (channellist/clientlist) — состояние строится из нотификаций в Book,
        /// читать который нужно на потоке планировщика.
        /// </summary>
        public static Task<(string serverName, TSLib.Full.Book.Channel[] channels, TSLib.Full.Book.Client[] clients, ChannelId? ownChannel)> GetBookSnapshot() {
            var c = client;
            var scheduler = clientScheduler;
            if (c == null || scheduler == null)
                return Task.FromResult(("", Array.Empty<TSLib.Full.Book.Channel>(), Array.Empty<TSLib.Full.Book.Client>(), (ChannelId?)null));

            return scheduler.Invoke(() => (
                serverName ?? "",
                c.Book.Channels.Values.ToArray(),
                c.Book.Clients.Values.ToArray(),
                c.Book.Clients.TryGetValue(c.ClientId, out var self) ? self.Channel : (ChannelId?)null
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

        // ===================== Управление аудио (с UI-потока) =====================
        // Все методы — no-op, если соединения (и конвейера) ещё/уже нет.

        public static void SetActivationMode(ActivationMode mode) => audio?.SetActivationMode(mode);
        public static void SetVoiceThreshold(float threshold) => audio?.SetVoiceThreshold(threshold);
        public static void SetVoiceDeactivationDelay(int delayMs) => audio?.SetVoiceDeactivationDelay(delayMs);
        public static void SetPushToTalk(bool talking) => audio?.SetPushToTalk(talking);

        // Глушим микрофон локально (конвейер) и сообщаем серверу — чтобы остальные видели иконку.
        // Состояние запоминаем глобально, чтобы восстановить при следующем подключении.
        public static void SetMicMuted(bool muted) {
            audio?.SetMicMuted(muted);
            if (context != null)
                AppSettings.SetMicMuted(context, muted);
            _ = Invoke(c => c.SendVoid(new TsCommand("clientupdate") { { "client_input_muted", muted } }));
        }

        // Глушим воспроизведение локально и сообщаем серверу о выключенном звуке.
        public static void SetSoundMuted(bool muted) {
            audio?.SetSoundMuted(muted);
            if (context != null)
                AppSettings.SetSoundMuted(context, muted);
            _ = Invoke(c => c.SendVoid(new TsCommand("clientupdate") { { "client_output_muted", muted } }));
        }

        public static bool IsMicMuted => audio?.MicMuted ?? false;
        public static bool IsSoundMuted => audio?.SoundMuted ?? false;

        // ===================== AFK / Away =====================

        public static bool IsAway => awayActive;

        // Ручной AFK с необязательным сообщением. Отправляем серверу; локальный флаг — для тумблера.
        // Состояние и сообщение запоминаем глобально для восстановления при подключении.
        public static void SetAway(bool away, string? message) {
            awayActive = away;
            if (context != null) {
                AppSettings.SetAfk(context, away);
                AppSettings.SetAfkMessage(context, away ? message : null);
            }
            _ = Invoke(c => c.SendVoid(new TsCommand("clientupdate") {
                { "client_away", away },
                { "client_away_message", away ? (message ?? "") : "" },
            }));
        }

        // ===================== Переход между каналами =====================

        // Перемещает себя в указанный канал (с паролем, если канал защищён).
        public static Task<bool> MoveToChannel(ChannelId channelId, string? password)
            => Invoke(c => c.ClientMove(c.ClientId, channelId, string.IsNullOrEmpty(password) ? null : password));

        // ===================== Громкость и мьют отдельных клиентов =====================

        // Локальный мьют собеседника (заглушка в конвейере воспроизведения) + сохранение по UID.
        public static void SetClientMuted(ClientId id, bool muted) {
            audio?.MuteClient(id, muted);
            PersistClientSetting(id, uid => AppSettings.SetClientMuted(context!, uid, muted));
        }

        public static bool IsClientMuted(ClientId id) => audio?.IsClientMuted(id) ?? false;

        // Локальная громкость собеседника (0..1, нативный AudioTrack.SetVolume) + сохранение по UID.
        public static void SetClientVolume(ClientId id, float volume) {
            audio?.SetClientVolume(id, volume);
            PersistClientSetting(id, uid => AppSettings.SetClientVolume(context!, uid, volume));
        }

        public static float GetClientVolume(ClientId id) => audio?.GetClientVolume(id) ?? 1f;

        // Достаёт UID клиента из Book (на потоке планировщика) и применяет к нему сохранение настройки.
        private static void PersistClientSetting(ClientId id, Action<string> persist) {
            var c = client;
            var scheduler = clientScheduler;
            if (c == null || scheduler == null || context == null)
                return;
            _ = scheduler.Invoke(() => {
                if (c.Book.Clients.TryGetValue(id, out var cl) && cl.Uid is Uid uid && !string.IsNullOrEmpty(uid.Value))
                    persist(uid.Value);
            });
        }

        // Применяет сохранённые по UID громкость/мьют к вошедшему клиенту (вызывается на потоке планировщика).
        private static void ApplySavedClientSettings(TsFullClient c, ClientId id) {
            if (context == null)
                return;
            if (!c.Book.Clients.TryGetValue(id, out var cl) || cl.Uid is not Uid uid || string.IsNullOrEmpty(uid.Value))
                return;

            float volume = AppSettings.GetClientVolume(context, uid.Value);
            if (volume < 1f)
                audio?.SetClientVolume(id, volume);
            if (AppSettings.GetClientMuted(context, uid.Value))
                audio?.MuteClient(id, true);
        }

        /// <summary>
        /// Корректно завершает соединение: graceful-дисконнект, остановка микрофона,
        /// освобождение аудиотреков, завершение выделенного потока планировщика.
        /// </summary>
        public static async Task Disconnect() {
            // Выполняем очистку один раз, даже если Disconnect пришёл из нескольких источников.
            if (Interlocked.Exchange(ref disconnecting, 1) == 1)
                return;

            var c = client;
            var scheduler = clientScheduler;
            var localAudio = audio;

            // Сначала останавливаем поток захвата (Dispose делает Join таймер-потока), затем сразу
            // освобождаем микрофон — до (возможно долгого) дисконнекта клиента, иначе AudioRecord
            // остаётся захваченным и системный индикатор микрофона продолжает гореть.
            localAudio?.StopCapture();

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
            localAudio?.DisposePlayback();

            // Завершает цикл DoWork выделенного потока, созданного через FromCurrentThread.
            scheduler?.Dispose();

            client = null;
            clientScheduler = null;
            clientThread = null;
            serverName = null;
            currentAddress = null;
            awayActive = false;
            lock (awayClients)
                awayClients.Clear();
            lock (talkingClients)
                talkingClients.Clear();
            audio = null;
            ClearChat();
            OnInstanceReady = null;
        }

        private const int SampleRate = 48000;
        private const int FrameSize = 960;
    }
}
