using Android.Content;
using AndroidX.Core.Content;

namespace MobileTS.Logging {
    /// <summary>Шаринг файла лога/крашрепорта через системную «шторку» (ACTION_SEND).</summary>
    public static class LogShare {
        // Должно совпадать с authority провайдера в AndroidManifest.xml.
        public const string FileProviderAuthority = "com.companyname.MobileTS.fileprovider";

        public static void ShareFile(Context context, string filePath) {
            var file = new Java.IO.File(filePath);
            // file:// нельзя отдавать другим приложениям (FileUriExposedException) — только content:// через FileProvider.
            var uri = FileProvider.GetUriForFile(context, FileProviderAuthority, file);

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
