using Android.App;
using Android.OS;
using Android.Runtime;
using MobileTS.Logging;
using AndroidActivity = Android.App.Activity;

namespace MobileTS {
    // Класс приложения: создаётся раньше любой Activity/Service. Здесь поднимаем журнал и
    // глобальный обработчик краша, чтобы они работали с самого старта, и отслеживаем текущую
    // Activity — её использует крашдиалог. (Alias AndroidActivity: имя Activity занято
    // дочерним namespace MobileTS.Activity.)
    [Application]
    public class MainApplication : Application, Application.IActivityLifecycleCallbacks {
        public static AndroidActivity? CurrentActivity { get; private set; }

        public MainApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

        public override void OnCreate() {
            base.OnCreate();
            AppLog.Init(this);
            CrashHandler.Install(this);
            RegisterActivityLifecycleCallbacks(this);
            AppLog.I("App", "Приложение запущено");
        }

        public void OnActivityResumed(AndroidActivity activity) => CurrentActivity = activity;

        public void OnActivityPaused(AndroidActivity activity) {
            if (CurrentActivity == activity)
                CurrentActivity = null;
        }

        public void OnActivityCreated(AndroidActivity activity, Bundle? savedInstanceState) { }
        public void OnActivityStarted(AndroidActivity activity) { }
        public void OnActivityStopped(AndroidActivity activity) { }
        public void OnActivitySaveInstanceState(AndroidActivity activity, Bundle outState) { }
        public void OnActivityDestroyed(AndroidActivity activity) { }
    }
}
