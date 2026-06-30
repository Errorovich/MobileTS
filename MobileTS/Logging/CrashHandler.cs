using System.Text;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidActivity = Android.App.Activity;

namespace MobileTS.Logging {
    /// <summary>
    /// Глобальный обработчик необработанных исключений. Вместо «тихого» падения сохраняет
    /// крашрепорт и текущий журнал на диск и показывает диалог с возможностью поделиться
    /// файлом, после чего завершает приложение. Нефатальные исключения в Task гасятся.
    /// </summary>
    public static class CrashHandler {
        private const string Tag = "Crash";
        public const string CrashExtra = "crash_report_path";

        private static Context _appContext = null!;
        private static int _handling; // не даём двум фатальным обработчикам перебивать друг друга

        public static void Install(Context appContext) {
            _appContext = appContext;

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Handle(e.ExceptionObject as Exception, "AppDomain", terminating: e.IsTerminating);

            AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
                Handle(e.Exception, "AndroidEnvironment", terminating: true);

            TaskScheduler.UnobservedTaskException += (_, e) => {
                // Нефатально: логируем и помечаем обработанным, чтобы приложение не падало.
                AppLog.E(Tag, "Необработанное исключение в Task", e.Exception);
                AppLog.Flush();
                e.SetObserved();
            };
        }

        private static void Handle(Exception? ex, string source, bool terminating) {
            if (Interlocked.Exchange(ref _handling, 1) == 1)
                return;

            string? crashPath = null;
            try {
                AppLog.E(Tag, "Необработанное исключение (" + source + ")", ex);
                AppLog.Flush();
                crashPath = SaveReport(ex, source);
            }
            catch { }

            if (!terminating) {
                // Редкий нефатальный путь — даём приложению продолжить.
                Interlocked.Exchange(ref _handling, 0);
                return;
            }

            ShowDialogAndExit(crashPath);
        }

        private static void ShowDialogAndExit(string? crashPath) {
            var activity = MainApplication.CurrentActivity;
            bool faultOnMain = Looper.MainLooper == Looper.MyLooper();

            // Лучший случай: упал фоновый поток и есть активный экран — показываем диалог
            // на месте и блокируем сбойный поток, пока пользователь не закроет (Kill завершит процесс).
            if (activity != null && !faultOnMain) {
                try {
                    activity.RunOnUiThread(() => ShowCrashDialog(activity, crashPath));
                    new ManualResetEventSlim(false).Wait(TimeSpan.FromMinutes(5)); // подстраховка
                }
                catch { }
                Kill();
                return;
            }

            // Иначе (упал UI-поток / есть экран): перезапускаем MainActivity с чистым UI-потоком,
            // она покажет тот же диалог по extra. Текущий процесс завершаем.
            if (activity != null) {
                try {
                    var intent = new Intent(_appContext, typeof(MainActivity));
                    intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTask);
                    intent.PutExtra(CrashExtra, crashPath);
                    _appContext.StartActivity(intent);
                }
                catch { }
            }
            // Нет экрана вовсе — отчёт уже сохранён, просто выходим.
            Kill();
        }

        /// <summary>Показывает крашдиалог. Вызывается с UI-потока (in-place или из MainActivity).</summary>
        public static void ShowCrashDialog(AndroidActivity activity, string? crashPath) {
            try {
                var builder = new AlertDialog.Builder(activity)
                    .SetTitle("Приложение упало")!
                    .SetMessage(crashPath != null
                        ? "Произошла ошибка. Лог сохранён:\n" + crashPath
                        : "Произошла ошибка.")!
                    .SetCancelable(false)!
                    .SetPositiveButton("Закрыть", (_, _) => Kill())!;
                if (crashPath != null)
                    builder.SetNeutralButton("Поделиться", (_, _) => { }); // переопределим ниже, чтобы не закрывать диалог

                var dialog = builder.Create()!;
                dialog.Show();

                // «Поделиться» не должна закрывать диалог: процесс нужен живым для FileProvider,
                // а закрыть приложение пользователь сможет кнопкой «Закрыть» после шаринга.
                if (crashPath != null) {
                    var shareBtn = dialog.GetButton((int)DialogButtonType.Neutral);
                    shareBtn?.SetOnClickListener(new ActionClickListener(() => {
                        try { LogShare.ShareFile(activity, crashPath); } catch { }
                    }));
                }
            }
            catch {
                Kill();
            }
        }

        private static string? SaveReport(Exception? ex, string source) {
            var sb = new StringBuilder();
            sb.AppendLine("==== MobileTS crash report ====");
            sb.AppendLine("Время:      " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Источник:   " + source);
            sb.AppendLine("Устройство: " + Build.Manufacturer + " " + Build.Model);
            sb.AppendLine("Android:    " + Build.VERSION.Release + " (API " + (int)Build.VERSION.SdkInt + ")");
            sb.AppendLine("Версия:     " + AppVersion());
            sb.AppendLine();
            sb.AppendLine("==== Exception ====");
            sb.AppendLine(ex?.ToString() ?? "(null)");
            sb.AppendLine();
            sb.AppendLine("==== Полный лог сессии ====");
            // Копируем весь текущий файл лога в крашрепорт — он переживёт перезапись при следующем старте.
            sb.Append(AppLog.CurrentLogContent() ?? AppLog.DumpRecent(1000));

            return AppLog.WriteFile("crash-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", sb.ToString());
        }

        private static string AppVersion() {
            try {
                var pi = _appContext.PackageManager!.GetPackageInfo(_appContext.PackageName!, 0);
                return pi!.VersionName ?? "?";
            }
            catch {
                return "?";
            }
        }

        private static void Kill() {
            AppLog.Flush();
            Process.KillProcess(Process.MyPid());
        }

        // Лёгкий IOnClickListener, чтобы повесить действие на кнопку без закрытия диалога.
        private sealed class ActionClickListener : Java.Lang.Object, View.IOnClickListener {
            private readonly Action _action;
            public ActionClickListener(Action action) => _action = action;
            public void OnClick(View? v) => _action();
        }
    }
}
