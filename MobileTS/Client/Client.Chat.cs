using System.Text.Json;
using System.Text.Json.Serialization;
using Android.Content;
using TSLib.Full;
using TSLib.Shared;

namespace MobileTS {
    // Чат канала. История теперь сохраняется на диск по ключу «адрес сервера + ID канала»
    // (SharedPreferences), поэтому переживает смену канала, переподключение и перезапуск приложения.
    // При входе в канал история этого канала подгружается с диска. Серверные/приватные сообщения не
    // используем — только TextMessageTargetMode.Channel.
    internal static partial class Client {
        public sealed class ChatMessage {
            public string Sender { get; }
            public string Text { get; }
            public bool IsOwn { get; }
            public DateTime Time { get; }

            public ChatMessage(string sender, string text, bool isOwn)
                : this(sender, text, isOwn, DateTime.Now) { }

            public ChatMessage(string sender, string text, bool isOwn, DateTime time) {
                Sender = sender;
                Text = text;
                IsOwn = isOwn;
                Time = time;
            }
        }

        // Для сериализации на диск (время — unix-мс, чтобы не зависеть от формата DateTime).
        private sealed class ChatMessageDto {
            public string Sender { get; set; } = "";
            public string Text { get; set; } = "";
            public bool IsOwn { get; set; }
            public long Time { get; set; }
        }

        // Сгенерированные контракты STJ для истории чата — без рефлексии, чтобы не падать под
        // TrimMode=full в Release (см. AppJsonContext). Вложенный контекст видит приватный
        // ChatMessageDto, поэтому DTO не нужно делать публичным.
        [JsonSerializable(typeof(List<ChatMessageDto>))]
        private partial class ChatJsonContext : JsonSerializerContext {
        }

        // Новое сообщение добавлено в историю (своё или входящее).
        public static event Action<ChatMessage>? OnChatMessage;
        // История очищена (отключение) — UI должен сбросить список.
        public static event Action? OnChatCleared;
        // История перезагружена целиком (смена канала) — UI должен перечитать GetChatHistory().
        public static event Action? OnChatReloaded;

        private const string ChatPrefsName = "chat_history";
        private const int ChatMaxMessages = 500;

        private static readonly List<ChatMessage> chatHistory = new();
        private static readonly object chatLock = new();

        // Канал, чью историю мы сейчас показываем (ключ записи входящих сообщений). null — канал ещё
        // не определён (до подключения).
        private static ChannelId? currentChatChannel;

        public static ChatMessage[] GetChatHistory() {
            lock (chatLock)
                return chatHistory.ToArray();
        }

        // Подписки чата. Вызывается из ClientThread на потоке планировщика.
        private static void HookChat(TsFullClient c) {
            c.OnEachTextMessage += (sender, e) => {
                if (e.Target != TextMessageTargetMode.Channel)
                    return;
                // Сервер присылает и наше собственное сообщение — отличаем по InvokerId.
                bool isOwn = e.InvokerId == c.ClientId;
                AddChat(new ChatMessage(e.InvokerName ?? "", e.Message ?? "", isOwn));
            };

            // Свой переезд в другой канал — подгружаем историю нового канала.
            c.OnEachClientMoved += (sender, e) => {
                if (e.ClientId == c.ClientId)
                    RefreshCurrentChannel(c);
            };

            // Первичное определение своего канала: после получения списка каналов/клиентов own-клиент
            // уже в Book со своим каналом (переезда-нотификации на старте может не быть).
            c.OnEachChannelListFinished += (sender, e) => RefreshCurrentChannel(c);
        }

        // Определяет текущий канал own-клиента из Book и, если он сменился, грузит его историю с диска.
        // Вызывается на потоке планировщика (после подключения и при своих переездах).
        private static void RefreshCurrentChannel(TsFullClient c) {
            if (!c.Book.Clients.TryGetValue(c.ClientId, out var self))
                return;
            var channel = self.Channel;
            if (currentChatChannel is ChannelId cur && cur.Equals(channel))
                return;
            currentChatChannel = channel;
            LoadChatFromDisk(channel);
        }

        private static void LoadChatFromDisk(ChannelId channel) {
            var ctx = context;
            var address = currentAddress;
            var loaded = new List<ChatMessage>();

            if (ctx != null && !string.IsNullOrEmpty(address)) {
                try {
                    var prefs = ctx.GetSharedPreferences(ChatPrefsName, FileCreationMode.Private);
                    var json = prefs?.GetString(ChatKey(address, channel), null);
                    if (!string.IsNullOrEmpty(json)) {
                        var dtos = JsonSerializer.Deserialize(json, ChatJsonContext.Default.ListChatMessageDto);
                        if (dtos != null)
                            foreach (var d in dtos)
                                loaded.Add(new ChatMessage(d.Sender, d.Text, d.IsOwn, FromUnixMs(d.Time)));
                    }
                }
                catch {
                    // повреждённый кэш игнорируем — начинаем с пустой истории
                }
            }

            lock (chatLock) {
                chatHistory.Clear();
                chatHistory.AddRange(loaded);
            }
            OnChatReloaded?.Invoke();
        }

        // Отправка в текущий канал. Сервер присылает наше сообщение обратно нотификацией
        // (OnEachTextMessage) — там оно и попадёт в историю, поэтому здесь только отправляем.
        public static void SendChannelChat(string text) {
            if (string.IsNullOrWhiteSpace(text))
                return;
            _ = client?.SendChannelMessage(text);
        }

        private static void AddChat(ChatMessage msg) {
            lock (chatLock)
                chatHistory.Add(msg);
            PersistChat();
            OnChatMessage?.Invoke(msg);
        }

        // Пишем на диск последние ChatMaxMessages сообщений текущего канала.
        private static void PersistChat() {
            var ctx = context;
            var address = currentAddress;
            if (ctx == null || string.IsNullOrEmpty(address) || currentChatChannel is not ChannelId channel)
                return;

            List<ChatMessageDto> dtos;
            lock (chatLock) {
                int skip = Math.Max(0, chatHistory.Count - ChatMaxMessages);
                dtos = chatHistory.Skip(skip).Select(m => new ChatMessageDto {
                    Sender = m.Sender,
                    Text = m.Text,
                    IsOwn = m.IsOwn,
                    Time = ToUnixMs(m.Time),
                }).ToList();
            }

            try {
                var prefs = ctx.GetSharedPreferences(ChatPrefsName, FileCreationMode.Private);
                prefs?.Edit()?.PutString(ChatKey(address, channel), JsonSerializer.Serialize(dtos, ChatJsonContext.Default.ListChatMessageDto))?.Apply();
            }
            catch {
                // кэш не критичен — игнорируем ошибки записи
            }
        }

        // Очистка in-memory истории при отключении (на диске история остаётся).
        internal static void ClearChat() {
            lock (chatLock)
                chatHistory.Clear();
            currentChatChannel = null;
            OnChatCleared?.Invoke();
        }

        private static string ChatKey(string address, ChannelId channel) =>
            "chat_" + SanitizeAddress(address) + "_" + channel.Value;

        private static long ToUnixMs(DateTime t) =>
            new DateTimeOffset(t.ToUniversalTime()).ToUnixTimeMilliseconds();

        private static DateTime FromUnixMs(long ms) =>
            DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
    }
}
