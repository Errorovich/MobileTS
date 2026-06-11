using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Text.Json;
using static TSLib.Full.TsFullClient;

namespace MobileTS.Services {
    [Service(ForegroundServiceType = ForegroundService.TypeMicrophone)]
    public class ClientService : Service {
        public override void OnCreate() {
            base.OnCreate();
            Client.Init(this);
            StartForeground(1, BuildNotification());
        }

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId) {
            // intent == null при пересоздании сервиса системой (Sticky) — подключаться нечем.
            var json = intent?.GetStringExtra("server_info");
            if (json != null) {
                var server = JsonSerializer.Deserialize<ServerInfo>(json)!;

                // Пароли хранятся и передаются в открытом виде (без Crypto).
                var serverPassword = server.ServerPassword;
                var channelPassword = server.DefaultChannelPassword;

                // Ник: у сервера он необязателен — если не задан, берём имя пользователя из
                // настроек; если и там пусто — подключаемся как «Guest».
                var nickname = server.Nickname;
                if (string.IsNullOrWhiteSpace(nickname))
                    nickname = AppSettings.GetUsername(this);
                if (string.IsNullOrWhiteSpace(nickname))
                    nickname = "Guest";

                // Подключение
                Client.Connect(
                    server.Address,
                    nickname,
                    serverPassword,
                    server.DefaultChannel,
                    channelPassword
                );

                StopWhenDisconnected();
            }

            return StartCommandResult.Sticky;
        }

        public override void OnDestroy() {
            // Снимаем foreground-состояние (и его уведомление/индикатор микрофона) сразу,
            // не дожидаясь завершения асинхронного Disconnect.
            StopForeground(StopForegroundFlags.Remove);
            _ = Client.Disconnect();
            base.OnDestroy();
        }

        // Соединение может оборваться само (сервер кикнул, потеря сети, неудачное подключение) —
        // тогда статус становится Disconnected, но foreground-сервис продолжает жить и держать
        // микрофон, зря расходуя батарею. Останавливаем сервис на терминальном статусе.
        private void StopWhenDisconnected() {
            Client.SubscribeInstance(c => {
                void StatusChanged(object? sender, TsClientStatus status) {
                    if (status != TsClientStatus.Disconnected)
                        return;
                    c.OnStatusChangedEvent -= StatusChanged;
                    StopSelf();
                }
                c.OnStatusChangedEvent += StatusChanged;
            });
        }

        public override IBinder? OnBind(Intent? intent) => null;

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
