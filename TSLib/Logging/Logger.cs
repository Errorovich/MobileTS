// Минималистичный логгер TSLib. Намеренно НЕ зависит от NLog (ни пакета, ни namespace):
// в исходниках TSLib остались закомментированные вызовы вида Log.Debug/Info/Warn/Error —
// этот логгер повторяет их сигнатуры, поэтому вызовы раскомментируются как есть, а
// правятся только строки-объявления (Logger Log = Logger.Create();).
//
// Все события уходят в статический LogSink.OnLog — на него подписывается приложение
// (MobileTS/Logging/AppLog.cs), которое пишет лог в файл, в кольцевой буфер и во
// вкладку «Журнал». Сам логгер ничего не знает про Android.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace TSLib.Logging {
	/// <summary>Уровень важности записи. Порядок важен: чем больше — тем важнее.</summary>
	public enum LogLevel {
		Trace = 0,
		Debug = 1,
		Info = 2,
		Warn = 3,
		Error = 4,
	}

	/// <summary>Одна запись лога, как она уходит подписчикам <see cref="LogSink"/>.</summary>
	public readonly struct LogEvent {
		public readonly LogLevel Level;
		public readonly string Logger;
		public readonly string Message;
		public readonly Exception? Exception;
		public readonly DateTime TimeUtc;
		public readonly string? ContextId;

		public LogEvent(LogLevel level, string logger, string message, Exception? exception, string? contextId) {
			Level = level;
			Logger = logger;
			Message = message;
			Exception = exception;
			TimeUtc = DateTime.UtcNow;
			ContextId = contextId;
		}
	}

	/// <summary>
	/// Единая точка выхода всех логов библиотеки. Приложение подписывается на
	/// <see cref="OnLog"/> и выставляет <see cref="MinLevel"/>.
	/// </summary>
	public static class LogSink {
		/// <summary>Записи ниже этого уровня не доходят до подписчиков (и не форматируются).</summary>
		public static volatile LogLevel MinLevel = LogLevel.Debug;

		public static event Action<LogEvent>? OnLog;

		public static bool IsEnabled(LogLevel level) => level >= MinLevel && OnLog != null;

		internal static void Emit(LogLevel level, string logger, string message, Exception? exception) {
			if (level < MinLevel)
				return;
			var handler = OnLog;
			if (handler == null)
				return;
			try {
				handler(new LogEvent(level, logger, message, exception, LogContext.Current));
			}
			catch {
				// Подписчик не должен ломать вызывающий код логированием.
			}
		}
	}

	/// <summary>
	/// Необязательный контекст (например, идентификатор сессии), который проставляется в
	/// каждую запись текущего асинхронного потока. Замена NLog MappedDiagnosticsLogicalContext.
	/// </summary>
	public static class LogContext {
		private static readonly AsyncLocal<string?> _id = new AsyncLocal<string?>();
		public static string? Current => _id.Value;
		public static void Set(string? id) => _id.Value = id;
	}

	/// <summary>
	/// Лёгкий логгер: один экземпляр на класс. Создаётся через <see cref="Create()"/>
	/// (имя берётся из имени файла на этапе компиляции — без рефлексии, дружелюбно к трим-режиму).
	/// </summary>
	public sealed class Logger {
		private static readonly ConcurrentDictionary<string, Logger> _named = new ConcurrentDictionary<string, Logger>();

		public string Name { get; }

		private Logger(string name) => Name = name;

		/// <summary>Логгер с именем по имени вызывающего файла (аналог GetCurrentClassLogger).</summary>
		public static Logger Create([CallerFilePath] string? file = null) {
			return new Logger(FileToName(file));
		}

		// CallerFilePath — путь машины сборки (Windows, '\'), а код исполняется на Android (Linux, '/'),
		// поэтому Path.GetFileName не отрезает каталог. Режем по обоим разделителям вручную.
		private static string FileToName(string? file) {
			if (string.IsNullOrEmpty(file))
				return "TSLib";
			int slash = file!.LastIndexOfAny(new[] { '/', '\\' });
			int start = slash + 1;
			int dot = file.LastIndexOf('.');
			int end = dot > start ? dot : file.Length;
			return file.Substring(start, end - start);
		}

		/// <summary>Логгер с явным именем (для именованных подсистем).</summary>
		public static Logger Get(string name) => _named.GetOrAdd(name, n => new Logger(n));

		public bool IsTraceEnabled => LogSink.IsEnabled(LogLevel.Trace);
		public bool IsDebugEnabled => LogSink.IsEnabled(LogLevel.Debug);
		public bool IsInfoEnabled => LogSink.IsEnabled(LogLevel.Info);
		public bool IsWarnEnabled => LogSink.IsEnabled(LogLevel.Warn);
		public bool IsErrorEnabled => LogSink.IsEnabled(LogLevel.Error);

		// ===================== Trace =====================
		public void Trace(string message, params object?[] args) => Write(LogLevel.Trace, null, message, args);
		public void Trace(Exception ex, string message, params object?[] args) => Write(LogLevel.Trace, ex, message, args);

		[Conditional("DEBUG")]
		public void ConditionalTrace(string message, params object?[] args) => Write(LogLevel.Trace, null, message, args);

		// ===================== Debug =====================
		public void Debug(string message, params object?[] args) => Write(LogLevel.Debug, null, message, args);
		public void Debug(Exception ex, string message, params object?[] args) => Write(LogLevel.Debug, ex, message, args);

		[Conditional("DEBUG")]
		public void ConditionalDebug(string message, params object?[] args) => Write(LogLevel.Debug, null, message, args);

		// ===================== Info =====================
		public void Info(string message, params object?[] args) => Write(LogLevel.Info, null, message, args);
		public void Info(Exception ex, string message, params object?[] args) => Write(LogLevel.Info, ex, message, args);

		// ===================== Warn =====================
		public void Warn(string message, params object?[] args) => Write(LogLevel.Warn, null, message, args);
		public void Warn(Exception ex, string message, params object?[] args) => Write(LogLevel.Warn, ex, message, args);

		// ===================== Error =====================
		public void Error(string message, params object?[] args) => Write(LogLevel.Error, null, message, args);
		public void Error(Exception ex, string message, params object?[] args) => Write(LogLevel.Error, ex, message, args);

		private void Write(LogLevel level, Exception? ex, string message, object?[] args) {
			// Ранний выход: ничего не форматируем, если запись всё равно будет отброшена.
			if (!LogSink.IsEnabled(level))
				return;
			LogSink.Emit(level, Name, Render(message, args), ex);
		}

		/// <summary>
		/// Рендер шаблона сообщения. Поддерживает позиционные «дырки» NLog/string.Format
		/// (<c>{0}</c>, <c>{0:F3}</c>) и именованные структурные (<c>{@token}</c>, <c>{$msg}</c>,
		/// <c>{name}</c>) — последние подставляются по порядку через ToString. Никогда не бросает.
		/// </summary>
		private static string Render(string template, object?[] args) {
			if (args == null || args.Length == 0 || string.IsNullOrEmpty(template))
				return template;
			try {
				if (!HasNamedHoles(template))
					return string.Format(CultureInfo.InvariantCulture, template, args);
				return RenderNamed(template, args);
			}
			catch {
				return template + " | " + string.Join(", ", args);
			}
		}

		// Есть ли хоть одна «дырка», начинающаяся не с цифры (т.е. именованная) — тогда
		// string.Format не подходит и нужна последовательная подстановка.
		private static bool HasNamedHoles(string template) {
			for (int i = 0; i < template.Length - 1; i++) {
				if (template[i] != '{')
					continue;
				char next = template[i + 1];
				if (next == '{') { i++; continue; }   // экранированная {{
				if (!char.IsDigit(next))
					return true;
			}
			return false;
		}

		private static string RenderNamed(string template, object?[] args) {
			var sb = new StringBuilder(template.Length + 16);
			int argIndex = 0;
			for (int i = 0; i < template.Length; i++) {
				char c = template[i];
				if (c == '{') {
					if (i + 1 < template.Length && template[i + 1] == '{') { sb.Append('{'); i++; continue; }
					int end = template.IndexOf('}', i + 1);
					if (end < 0) { sb.Append(c); continue; }
					object? val = argIndex < args.Length ? args[argIndex] : null;
					argIndex++;
					sb.Append(val?.ToString() ?? "(null)");
					i = end;
				}
				else if (c == '}') {
					if (i + 1 < template.Length && template[i + 1] == '}') { sb.Append('}'); i++; continue; }
					sb.Append(c);
				}
				else {
					sb.Append(c);
				}
			}
			return sb.ToString();
		}
	}
}
