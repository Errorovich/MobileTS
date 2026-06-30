using Android.Content;
using TSLib.Logging;

namespace MobileTS {
    // Режим активации микрофона.
    public enum ActivationMode {
        Voice = 0,      // по голосу (VAD)
        PushToTalk = 1, // по кнопке
    }

    // Глобальные пользовательские настройки приложения (SharedPreferences):
    // имя пользователя по умолчанию, режим активации микрофона и порог активации по голосу.
    public static class AppSettings {
        private const string PrefsName = "app_settings";
        private const string UsernameKey = "username";
        private const string ActivationModeKey = "activation_mode";
        private const string VoiceThresholdKey = "voice_threshold";
        private const string VoiceDeactivationDelayKey = "voice_deactivation_delay";

        // Состояние звука/AFK — глобальное (одно на все серверы), восстанавливается при подключении.
        private const string MicMutedKey = "mic_muted";
        private const string SoundMutedKey = "sound_muted";
        private const string AfkKey = "afk";
        private const string AfkMessageKey = "afk_message";

        // Минимальный уровень логов библиотеки/приложения, попадающих в «Журнал» и файл.
        private const string LogLevelKey = "log_level";

        public const float DefaultVoiceThreshold = 0.05f;
        public const int DefaultVoiceDeactivationDelayMs = 300;
        public const LogLevel DefaultLogLevel = LogLevel.Debug;

        private static ISharedPreferences? Prefs(Context context) =>
            context.GetSharedPreferences(PrefsName, FileCreationMode.Private);

        public static string? GetUsername(Context context) =>
            Prefs(context)?.GetString(UsernameKey, null);

        public static void SetUsername(Context context, string? value) =>
            Prefs(context)!.Edit()!.PutString(UsernameKey, value)!.Apply();

        public static ActivationMode GetActivationMode(Context context) =>
            (ActivationMode)(Prefs(context)?.GetInt(ActivationModeKey, (int)ActivationMode.Voice) ?? (int)ActivationMode.Voice);

        public static void SetActivationMode(Context context, ActivationMode mode) =>
            Prefs(context)!.Edit()!.PutInt(ActivationModeKey, (int)mode)!.Apply();

        public static float GetVoiceThreshold(Context context) =>
            Prefs(context)?.GetFloat(VoiceThresholdKey, DefaultVoiceThreshold) ?? DefaultVoiceThreshold;

        public static void SetVoiceThreshold(Context context, float value) =>
            Prefs(context)!.Edit()!.PutFloat(VoiceThresholdKey, value)!.Apply();

        public static int GetVoiceDeactivationDelay(Context context) =>
            Prefs(context)?.GetInt(VoiceDeactivationDelayKey, DefaultVoiceDeactivationDelayMs) ?? DefaultVoiceDeactivationDelayMs;

        public static void SetVoiceDeactivationDelay(Context context, int valueMs) =>
            Prefs(context)!.Edit()!.PutInt(VoiceDeactivationDelayKey, valueMs)!.Apply();

        public static LogLevel GetLogLevel(Context context) =>
            (LogLevel)(Prefs(context)?.GetInt(LogLevelKey, (int)DefaultLogLevel) ?? (int)DefaultLogLevel);

        public static void SetLogLevel(Context context, LogLevel value) =>
            Prefs(context)!.Edit()!.PutInt(LogLevelKey, (int)value)!.Apply();

        // ===================== Состояние звука/AFK (глобальное) =====================

        public static bool GetMicMuted(Context context) =>
            Prefs(context)?.GetBoolean(MicMutedKey, false) ?? false;

        public static void SetMicMuted(Context context, bool value) =>
            Prefs(context)!.Edit()!.PutBoolean(MicMutedKey, value)!.Apply();

        public static bool GetSoundMuted(Context context) =>
            Prefs(context)?.GetBoolean(SoundMutedKey, false) ?? false;

        public static void SetSoundMuted(Context context, bool value) =>
            Prefs(context)!.Edit()!.PutBoolean(SoundMutedKey, value)!.Apply();

        public static bool GetAfk(Context context) =>
            Prefs(context)?.GetBoolean(AfkKey, false) ?? false;

        public static void SetAfk(Context context, bool value) =>
            Prefs(context)!.Edit()!.PutBoolean(AfkKey, value)!.Apply();

        public static string? GetAfkMessage(Context context) =>
            Prefs(context)?.GetString(AfkMessageKey, null);

        public static void SetAfkMessage(Context context, string? value) =>
            Prefs(context)!.Edit()!.PutString(AfkMessageKey, value)!.Apply();

        // ===================== Громкость / мьют по пользователю (UID) =====================
        // Хранятся отдельно от настроек выше — переживают переподключения, ключ — UID клиента.

        public static float GetClientVolume(Context context, string uid) =>
            Prefs(context)?.GetFloat("vol_" + uid, 1f) ?? 1f;

        public static void SetClientVolume(Context context, string uid, float value) =>
            Prefs(context)!.Edit()!.PutFloat("vol_" + uid, value)!.Apply();

        public static bool GetClientMuted(Context context, string uid) =>
            Prefs(context)?.GetBoolean("mute_" + uid, false) ?? false;

        public static void SetClientMuted(Context context, string uid, bool value) =>
            Prefs(context)!.Edit()!.PutBoolean("mute_" + uid, value)!.Apply();
    }
}
