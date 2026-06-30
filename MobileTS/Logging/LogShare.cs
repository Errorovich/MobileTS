using Android.Content;
using AndroidX.Core.Content;

namespace MobileTS.Logging {
    /// <summary>Шаринг файла лога/крашрепорта через системную «шторку» (ACTION_SEND).</summary>
    public static class LogShare {
        // Authority провайдера = "<applicationId>.fileprovider" (в манифесте — ${applicationId}.fileprovider).
        // Считаем из имени пакета в рантайме: у debug-сборки пакет с суффиксом «.debug», и authority
        // должно совпадать с тем, что подставил SDK, иначе FileProvider.GetUriForFile бросит исключение.
        public static string AuthorityFor(Context context) => context.PackageName + ".fileprovider";

        public static void ShareFile(Context context, string filePath) {
            var file = new Java.IO.File(filePath);
            // file:// нельзя отдавать другим приложениям (FileUriExposedException) — только content:// через FileProvider.
            var uri = FileProvider.GetUriForFile(context, AuthorityFor(context), file);

            var send = new Intent(Intent.ActionSend);
            send.SetType("text/plain");
            send.PutExtra(Intent.ExtraStream, (Android.OS.IParcelable)uri);
            send.AddFlags(ActivityFlags.GrantReadUriPermission);

            var chooser = Intent.CreateChooser(send, "Поделиться логом")!;
            // Нужно при запуске из не-Activity контекста (сервис/обработчик краша).
            chooser.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(chooser);
        }
    }
}
