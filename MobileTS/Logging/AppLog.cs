using System.Text;
using Android.Content;
using TSLib.Logging;

namespace MobileTS.Logging {
    /// <summary>Одна готовая строка журнала для UI и для файла.</summary>
    public sealed class LogEntry {
        public DateTime Time { get; init; }
        public LogLevel Level { get; init; }
        public string Tag { get; init; } = "";
        public string Message { get; init; } = "";
        /// <summary>Готовая отформатированная строка (то, что пишется в файл и в журнал).</summary>
        public string Line { get; init; } = "";
    }

    /// <summary>
    /// Ядро логирования приложения. Подписывается на логи библиотеки
    /// (<see cref="TSLib.Logging.LogSink"/>), пишет всё в файл сессии и в кольцевой буфер
    /// (для вкладки «Журнал» и для крашрепорта). Сами строки приложения тоже идут сюда
    /// через <see cref="D"/>/<see cref="I"/>/<see cref="W"/>/<see cref="E"/>.
    /// Потокобезопасно: вызывается с потока планировщика TSLib, UI и сервиса.
    /// </summary>
    public static class AppLog {
        private const int BufferCap = 2000;     // сколько записей держим в памяти для UI/крашрепорта
        private const int MaxLogFiles = 10;     // сколько файлов сессий (log-*.txt) храним
        private const int MaxCrashFiles = 10;   // сколько крашрепортов (crash-*.txt) храним

        private static readonly object Sync = new();
        private static readonly List<LogEntry> Buffer = new(BufferCap);
        private static StreamWriter? _writer;
        private static string? _currentFile;
        private static string? _logDir;
        private static bool _initialized;

        /// <summary>Новая запись добавлена (для живого обновления журнала). Может прийти с любого потока.</summary>
        public static event Action<LogEntry>? OnEntry;
        /// <summary>Буфер очищен (журнал должен перечитать снимок).</summary>
        public static event Action? OnCleared;

        public static string? CurrentFilePath => _currentFile;
        public static string? LogDir => _logDir;

        public static void Init(Context context) {
            lock (Sync) {
                if (_initialized)
                    return;
                _initialized = true;

                try {
                    // Внешний app-scoped каталог: виден системным «Файлы»/по USB без разрешений.
                    var baseDir = context.GetExternalFilesDir(null)?.AbsolutePath
                                  ?? context.FilesDir!.AbsolutePath;
                    _logDir = Path.Combine(baseDir, "logs");
                    Directory.CreateDirectory(_logDir);

                    // Новый файл на каждую сессию; храним последние MaxLogFiles, чтобы логи не копились бесконечно.
                    _currentFile = Path.Combine(_logDir, "log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                    // FileShare.ReadWrite — чтобы файл можно было копировать/шарить, пока он открыт.
                    _writer = new StreamWriter(
                        new FileStream(_currentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) {
                        AutoFlush = true // переживаем краш — каждая строка сразу на диск
                    };

                    PruneOldSessionFiles();
                    PruneOldCrashFiles();
                }
                catch {
                    // Файл недоступен — продолжаем только с журналом в памяти.
                    _writer = null;
                }

                // Уровень из настроек + подписка на логи библиотеки.
                try { LogSink.MinLevel = AppSettings.GetLogLevel(context); } catch { }
                LogSink.OnLog -= OnLibraryLog;
                LogSink.OnLog += OnLibraryLog;
            }

            I("AppLog", "Журнал инициализирован: " + (_currentFile ?? "(только память)"));
        }

        private static void OnLibraryLog(LogEvent e)
            => Write(e.Level, e.Logger, e.Message, e.Exception, e.ContextId);

        public static void Write(LogLevel level, string tag, string message, Exception? ex = null, string? contextId = null) {
            var now = DateTime.Now;
            var sb = new StringBuilder(64);
            sb.Append(now.ToString("HH:mm:ss.fff")).Append(" [").Append(LevelName(level)).Append("] ");
            if (!string.IsNullOrEmpty(contextId))
                sb.Append('{').Append(contextId).Append("} ");
            sb.Append(tag).Append(": ").Append(message);
            if (ex != null)
                sb.Append('\n').Append(ex);
            var line = sb.ToString();

            var entry = new LogEntry { Time = now, Level = level, Tag = tag, Message = message, Line = line };

            lock (Sync) {
                Buffer.Add(entry);
                if (Buffer.Count > BufferCap)
                    Buffer.RemoveRange(0, Buffer.Count - BufferCap);
                try { _writer?.WriteLine(line); } catch { }
            }

            try { OnEntry?.Invoke(entry); } catch { }
        }

        // ===================== Удобные хелперы для кода приложения =====================
        public static void T(string tag, string msg) => Write(LogLevel.Trace, tag, msg);
        public static void D(string tag, string msg) => Write(LogLevel.Debug, tag, msg);
        public static void I(string tag, string msg) => Write(LogLevel.Info, tag, msg);
        public static void W(string tag, string msg, Exception? ex = null) => Write(LogLevel.Warn, tag, msg, ex);
        public static void E(string tag, string msg, Exception? ex = null) => Write(LogLevel.Error, tag, msg, ex);

        /// <summary>Снимок буфера для первичного наполнения журнала.</summary>
        public static LogEntry[] Snapshot() {
            lock (Sync)
                return Buffer.ToArray();
        }

        /// <summary>Последние записи как текст — для вложения в крашрепорт.</summary>
        public static string DumpRecent(int max = 500) {
            lock (Sync) {
                int start = Math.Max(0, Buffer.Count - max);
                var sb = new StringBuilder();
                for (int i = start; i < Buffer.Count; i++)
                    sb.AppendLine(Buffer[i].Line);
                return sb.ToString();
            }
        }

        public static void Clear() {
            lock (Sync)
                Buffer.Clear();
            try { OnCleared?.Invoke(); } catch { }
            I("AppLog", "Журнал очищен (память)");
        }

        public static void Flush() {
            lock (Sync) {
                try { _writer?.Flush(); } catch { }
            }
        }

        /// <summary>Текущий файл лога и все крашрепорты (новые сверху).</summary>
        public static FileInfo[] GetLogFiles() {
            try {
                if (_logDir == null || !Directory.Exists(_logDir))
                    return Array.Empty<FileInfo>();
                return new DirectoryInfo(_logDir).GetFiles()
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToArray();
            }
            catch {
                return Array.Empty<FileInfo>();
            }
        }

        /// <summary>Полный текст текущего файла лога — для копирования в крашрепорт.</summary>
        public static string? CurrentLogContent() {
            lock (Sync) {
                try { _writer?.Flush(); } catch { }
            }
            try {
                if (_currentFile == null || !File.Exists(_currentFile))
                    return null;
                using var fs = new FileStream(_currentFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                return sr.ReadToEnd();
            }
            catch {
                return null;
            }
        }

        /// <summary>Записать отдельный файл (например, крашрепорт) в каталог логов. Возвращает путь.</summary>
        public static string? WriteFile(string name, string content) {
            try {
                if (_logDir == null)
                    return null;
                var path = Path.Combine(_logDir, name);
                File.WriteAllText(path, content);
                return path;
            }
            catch {
                return null;
            }
        }

        // Оставляем только последние MaxLogFiles файлов сессий (log-*.txt).
        private static void PruneOldSessionFiles() {
            try {
                if (_logDir == null)
                    return;
                var sessions = new DirectoryInfo(_logDir).GetFiles("log-*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToArray();
                for (int i = MaxLogFiles; i < sessions.Length; i++) {
                    try { sessions[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        // Оставляем только последние MaxCrashFiles крашрепортов (crash-*.txt).
        private static void PruneOldCrashFiles() {
            try {
                if (_logDir == null)
                    return;
                var crashes = new DirectoryInfo(_logDir).GetFiles("crash-*.txt")
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToArray();
                for (int i = MaxCrashFiles; i < crashes.Length; i++) {
                    try { crashes[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        private static string LevelName(LogLevel l) => l switch {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Info => "INF",
            LogLevel.Warn => "WRN",
            LogLevel.Error => "ERR",
            _ => "?",
        };
    }
}
