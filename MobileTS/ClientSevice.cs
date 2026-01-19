using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Text.Json;
using System.Threading.Tasks;

namespace MobileTS {
    [Service(ForegroundServiceType = ForegroundService.TypeMicrophone)]
    public class ClientService : Service {
        public override void OnCreate() {
            base.OnCreate();
            Client.Init(this);
            StartForeground(1, BuildNotification());
        }

        public override StartCommandResult OnStartCommand(Intent intent, StartCommandFlags flags, int startId) {
            var json = intent.GetStringExtra("server_info");
            if (json != null) {
                var server = JsonSerializer.Deserialize<ServerInfo>(json)!;

                // Расшифровка паролей внутри сервиса
                var serverPassword = Crypto.Decrypt(server.ServerPassword);
                var channelPassword = Crypto.Decrypt(server.DefaultChannelPassword);

                // Подключение
                Client.Connect(
                    server.Address,
                    string.IsNullOrWhiteSpace(server.Nickname) ? "Guest" : server.Nickname,
                    serverPassword,
                    server.DefaultChannel,
                    channelPassword
                );
            }

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy() {
            _ = Client.Disconnect();
            base.OnDestroy();
        }

        public override IBinder? OnBind(Intent intent) => null;

        private Notification BuildNotification() {
            const string channelId = "ts_service_channel";
            const string channelName = "TeamSpeak Service";

            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) {
                var channel = new NotificationChannel(
                    channelId,
                    channelName,
                    NotificationImportance.Low
                ) {
                    Description = "TeamSpeak voice service"
                };

                var manager = (NotificationManager)GetSystemService(NotificationService)!;
                manager.CreateNotificationChannel(channel);
            }

            var notification = new Notification.Builder(this, channelId)
                .SetContentTitle("TeamSpeak")
                .SetContentText("Подключено к серверу")
                .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
                .SetOngoing(true)
                .Build();

            return notification;
        }
    }
}