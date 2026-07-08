using System.IO;
using System.Text;
using Android.Content;
using TSLib.ClientBase;
using TSLib.Full;
using TSLib.Shared;

namespace MobileTS {
    // Иконки сервера. Скачиваются через file transfer TeamSpeak (нужно активное подключение) после
    // initserver и кэшируются на диск по адресу сервера. Список сохранённых серверов и шапка активных
    // подключений читают кэш по адресу — иконка появляется после первого успешного подключения.
    internal static partial class Client {
        // Иконка сервера скачана и сохранена (передаётся адрес сервера) — UI может обновить картинку.
        public static event Action<string>? OnServerIconReady;

        // Путь к файлу кэша иконки для адреса (существование не гарантируется).
        public static string GetServerIconFile(Context ctx, string address) {
            string dir = Path.Combine(ctx.FilesDir!.AbsolutePath, "server_icons");
            return Path.Combine(dir, SanitizeAddress(address) + ".img");
        }

        // Путь к кэшу иконки, если файл уже существует, иначе null (для адаптеров списка).
        public static string? GetServerIconPathIfExists(string address) {
            var ctx = context;
            if (ctx == null || string.IsNullOrEmpty(address))
                return null;
            string file = GetServerIconFile(ctx, address);
            return File.Exists(file) ? file : null;
        }

        // Качает иконку сервера (по Server.IconId из Book) и сохраняет в кэш. Fire-and-forget из
        // обработчика initserver.
        private static async Task DownloadServerIcon() {
            var c = client;
            var ctx = context;
            var address = currentAddress;
            if (c == null || ctx == null || string.IsNullOrEmpty(address))
                return;

            byte[]? bytes;
            try {
                // Icon (CRC) читаем из Book на потоке клиента; сам file transfer потокобезопасен.
                int iconId = await c.Invoke(() => c.Book.Server.Icon);
                if (iconId == 0)
                    return;
                using var ms = new MemoryStream();
                // Путь иконки — беззнаковый CRC; иконки лежат в служебном «канале 0».
                var r = await c.DownloadFile(ms, new ChannelId(0), "/icon_" + unchecked((uint)iconId), "", closeStream: false);
                if (!r.GetOk(out var token) || token.Status != TransferStatus.Done)
                    return;
                bytes = ms.ToArray();
            }
            catch {
                // сеть/гонка с дисконнектом — иконка не критична
                return;
            }

            if (bytes == null || bytes.Length == 0)
                return;

            try {
                string file = GetServerIconFile(ctx, address);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes);
                OnServerIconReady?.Invoke(address);
            }
            catch {
                // кэш не критичен — игнорируем ошибки записи
            }
        }

        private static string SanitizeAddress(string address) {
            var sb = new StringBuilder(address.Length);
            foreach (char ch in address)
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            return sb.ToString();
        }
    }
}
