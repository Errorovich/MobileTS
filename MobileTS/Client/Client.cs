using Android;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.Media.TV;
using Android.Provider;
using Android.Runtime;
using MobileTS.Audio;
using MobileTS.Logging;
using TSLib;
using TSLib.Audio;
using TSLib.Audio.Opus;
using TSLib.Commands;
using TSLib.Full;

namespace MobileTS {
    internal static partial class Client {
        public static TsFullClient? Instance => client;
        public static event Action<TsFullClient>? OnInstanceReady;

        /// <summary>
        /// Срабатывает, когда клиент начинает/прекращает говорить. Подписка живёт между
        /// переподключениями: каждый новый конвейер пробрасывает события сюда.
        /// </summary>
        public static event Action<TalkingTrackerPipe.ClientVoiceStatus>? OnClientIsTalkingChanged;

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

        private static TsFullClient? client;
        private static ContextWrapper? context;

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
            if (client != null) {
                AppLog.W("Client", "Connect проигнорирован: соединение уже активно");
                return;
            }

            if (identity == null) {
                AppLog.E("Client", "Connect невозможен: identity не инициализирована");
                return;
            }

            AppLog.I("Client", "Подключение к " + address + " (ник: " + (nickname ?? "Guest") + ")");

            // Новая сессия — снимаем флаг дисконнекта (предыдущая очистка к этому моменту завершена,
            // т.к. client выше уже null).
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

            // Клиент сам владеет выделенным потоком планировщика; его API потокобезопасно,
            // поэтому подключение — обычный async-метод с любого потока.
            var localClient = new TsFullClient();
            client = localClient;
            _ = ConnectAsync(localClient, conData);
        }

        private static async Task ConnectAsync(TsFullClient localClient, ConnectionDataFull conData) {

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
                OnClientIsTalkingChanged?.Invoke(new TalkingTrackerPipe.ClientVoiceStatus(me, active));
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

            // Имя сервера берём из initserver напрямую (virtualserver_name -> Name).
            localClient.OnEachInitServer += (sender, e) => {
                serverName = e.Name;
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
            catch (Exception ex) {
                AppLog.E("Client", "Исключение при подключении", ex);
                connected = false;
            }

            AppLog.I("Client", connected
                ? "Подключение установлено: " + (currentAddress ?? "")
                : "Подключение не удалось: " + (currentAddress ?? ""));

            // Сообщаем серверу восстановленное состояние звука/AFK (команды потокобезопасны —
            // шлём напрямую), чтобы остальные участники сразу видели наши иконки.
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
                catch (Exception ex) {
                    // не критично — локальное состояние уже применено
                    AppLog.W("Client", "Не удалось отправить начальное состояние звука/AFK", ex);
                }

                // Подгружаем историю чата канала, в котором оказались после подключения.
                // Book читается только на потоке клиента.
                try {
                    await localClient.Invoke(() => RefreshCurrentChannel(localClient));
                }
                catch (ObjectDisposedException) {
                    // параллельный дисконнект уже закрыл клиента — история не нужна
                }
            }

            // При неудаче освобождаем ресурсы и сбрасываем статические поля тем же путём, что и
            // обычный дисконнект. Иначе client остаётся занят, и повторное подключение
            // зависает: Connect выходит рано, а статус соединения уже никогда не сменится.
            if (!connected)
                await Disconnect();
        }

        /// <summary>
        /// Снимок дерева сервера из <see cref="TsFullClient.Book"/>. Полный клиент не отвечает на
        /// server-query команды (channellist/clientlist) — состояние строится из нотификаций в Book,
        /// читать который можно только на потоке клиента (через <see cref="TsFullClient.Invoke{T}"/>).
        /// </summary>
        public static Task<(string serverName, TSLib.Full.Book.Channel[] channels, TSLib.Full.Book.Client[] clients, ChannelId? ownChannel)> GetBookSnapshot() {
            var empty = ("", Array.Empty<TSLib.Full.Book.Channel>(), Array.Empty<TSLib.Full.Book.Client>(), (ChannelId?)null);
            var c = client;
            if (c == null)
                return Task.FromResult(empty);

            try {
                return c.Invoke(() => (
                    serverName ?? "",
                    c.Book.Channels.Values.ToArray(),
                    c.Book.Clients.Values.ToArray(),
                    c.Book.Clients.TryGetValue(c.ClientId, out var self) ? self.Channel : (ChannelId?)null
                ));
            }
            catch (ObjectDisposedException) {
                // Гонка с дисконнектом: клиент уже освобождён — отдаём пустое дерево.
                return Task.FromResult(empty);
            }
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
            _ = client?.SendVoid(new TsCommand("clientupdate") { { "client_input_muted", muted } });
        }

        // Глушим воспроизведение локально и сообщаем серверу о выключенном звуке.
        public static void SetSoundMuted(bool muted) {
            audio?.SetSoundMuted(muted);
            if (context != null)
                AppSettings.SetSoundMuted(context, muted);
            _ = client?.SendVoid(new TsCommand("clientupdate") { { "client_output_muted", muted } });
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
            _ = client?.SendVoid(new TsCommand("clientupdate") {
                { "client_away", away },
                { "client_away_message", away ? (message ?? "") : "" },
            });
        }

        // ===================== Переход между каналами =====================

        // Перемещает себя в указанный канал (с паролем, если канал защищён).
        public static async Task<bool> MoveToChannel(ChannelId channelId, string? password) {
            var c = client;
            if (c == null)
                return false;
            return (await c.ClientMove(c.ClientId, channelId, string.IsNullOrEmpty(password) ? null : password)).Ok;
        }

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

        // Достаёт UID клиента из Book (на потоке клиента) и применяет к нему сохранение настройки.
        private static void PersistClientSetting(ClientId id, Action<string> persist) {
            var c = client;
            if (c == null || context == null)
                return;
            try {
                _ = c.Invoke(() => {
                    if (c.Book.Clients.TryGetValue(id, out var cl) && cl.Uid is Uid uid && !string.IsNullOrEmpty(uid.Value))
                        persist(uid.Value);
                });
            }
            catch (ObjectDisposedException) {
                // гонка с дисконнектом — настройку сохраним в следующий раз
            }
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

            AppLog.I("Client", "Отключение и очистка ресурсов: " + (currentAddress ?? ""));

            var c = client;
            var localAudio = audio;

            // Сначала останавливаем поток захвата (Dispose делает Join таймер-потока), затем сразу
            // освобождаем микрофон — до (возможно долгого) дисконнекта клиента, иначе AudioRecord
            // остаётся захваченным и системный индикатор микрофона продолжает гореть.
            localAudio?.StopCapture();

            if (c != null) {
                try {
                    await c.Disconnect();
                }
                catch (Exception ex) {
                    // игнорируем — всё равно освобождаем ресурсы ниже
                    AppLog.W("Client", "Исключение при graceful-дисконнекте", ex);
                }
                // Сначала прячем клиента от новых вызовов, затем освобождаем (Dispose гасит и
                // его собственный поток планировщика).
                client = null;
                c.Dispose();
            }

            // Трек воспроизведения освобождаем после дисконнекта: клиент мог дописывать
            // декодированный звук в конвейер вплоть до завершения соединения.
            localAudio?.DisposePlayback();

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
