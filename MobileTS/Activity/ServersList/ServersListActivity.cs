using Android.Content;
using Android.Content.PM;
using AndroidX.RecyclerView.Widget;
using System.Text.Json;
using MobileTS.Activity.Server;
using MobileTS.Services;
using static TSLib.Full.TsFullClient;

namespace MobileTS.Activity.ServersList {
    [Activity(Label = "Серверы", MainLauncher = true)]
    public partial class ServersListActivity : Android.App.Activity {
        private const string PrefsName = "servers_storage";
        private const string ServersKey = "servers";
        private const int RecordAudioRequestCode = 1;

        private readonly List<ServerInfo> _servers = new();
        private ServerAdapter? _adapter;

        // Сервер, к которому подключаемся, как только пользователь выдаст RECORD_AUDIO.
        private ServerInfo? _pendingConnectServer;

        protected override void OnCreate(Bundle? savedInstanceState) {
            base.OnCreate(savedInstanceState);

            Client.Init(this);
            SetContentView(Resource.Layout.activity_servers);

            Crypto.EnsureKey();

            var recycler = FindViewById<RecyclerView>(Resource.Id.recycler)!;
            recycler.SetLayoutManager(new LinearLayoutManager(this));

            _adapter = new ServerAdapter(_servers, this);
            recycler.SetAdapter(_adapter);

            LoadServers();
            _adapter.NotifyDataSetChanged();

            FindViewById<Button>(Resource.Id.btnAdd)!.Click += (_, _) => ShowServerDialog();
        }

        // ================= CONNECT =================

        // Подключение запускает FGS типа microphone, что требует выданного RECORD_AUDIO.
        // Если разрешения ещё нет — запоминаем сервер и запрашиваем; продолжаем в колбэке.
        public void ConnectToServer(ServerInfo server) {
            if (CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted) {
                _pendingConnectServer = server;
                RequestPermissions([Android.Manifest.Permission.RecordAudio], RecordAudioRequestCode);
                return;
            }

            StartConnection(server);
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults) {
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

            if (requestCode != RecordAudioRequestCode)
                return;

            var server = _pendingConnectServer;
            _pendingConnectServer = null;

            if (server != null && grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                StartConnection(server);
            else
                Toast.MakeText(this, "Нет доступа к микрофону — подключение невозможно", ToastLength.Long)?.Show();
        }

        private void StartConnection(ServerInfo server) {
            // Передаем весь объект в сервис, пароли остаются зашифрованными
            var clientServiceIntent = new Intent(this, typeof(ClientService));
            clientServiceIntent.PutExtra("server_info", JsonSerializer.Serialize(server));
            StartService(clientServiceIntent);

            var progress = new ProgressDialog(this);
            progress.SetMessage("Подключение...");
            progress.SetCancelable(false);
            progress.Show();

            Client.SubscribeInstance(c => {
                void StatusChanged(object? sender, TsClientStatus status) {
                    if (status != TsClientStatus.Connected && status != TsClientStatus.Disconnected)
                        return;

                    // Терминальный статус — снимаем подписку, иначе обработчики копятся.
                    c.OnStatusChangedEvent -= StatusChanged;

                    RunOnUiThread(() => {
                        progress.Dismiss();
                        if (status == TsClientStatus.Connected) {
                            var intent = new Intent(this, typeof(ServerActivity));
                            // Имя для заголовка ActionBar, пока не загрузится настоящее из Book.
                            intent.PutExtra("server_title", server.Address);
                            StartActivity(intent);
                        }
                    });
                }

                c.OnStatusChangedEvent += StatusChanged;
            });
        }

        // ================= STORAGE =================

        private void SaveServers() {
            var prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var json = JsonSerializer.Serialize(_servers);
            prefs.Edit()!.PutString(ServersKey, json).Apply();
        }

        private void LoadServers() {
            var prefs = GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var json = prefs.GetString(ServersKey, null);
            if (!string.IsNullOrEmpty(json)) {
                var list = JsonSerializer.Deserialize<List<ServerInfo>>(json);
                if (list != null) {
                    _servers.Clear();
                    _servers.AddRange(list);
                }
            }
        }

        // ================= DIALOG =================

        private void ShowServerDialog(ServerInfo? server = null) {
            var dialog = new Dialog(this);
            dialog.SetContentView(Resource.Layout.dialog_server);

            var txtAddress = dialog.FindViewById<EditText>(Resource.Id.txtAddress)!;
            var txtUser = dialog.FindViewById<EditText>(Resource.Id.txtUser)!;
            var txtChannel = dialog.FindViewById<EditText>(Resource.Id.txtChannel)!;
            var txtPassword = dialog.FindViewById<EditText>(Resource.Id.txtPassword)!;
            var txtChannelPassword = dialog.FindViewById<EditText>(Resource.Id.txtChannelPassword)!;

            if (server != null) {
                txtAddress.Text = server.Address;
                txtUser.Text = server.Nickname;
                txtChannel.Text = server.DefaultChannel;
            }

            dialog.FindViewById<Button>(Resource.Id.btnSave)!.Click += (_, _) => {
                var encryptedServerPass =
                    string.IsNullOrEmpty(txtPassword.Text)
                        ? server?.ServerPassword
                        : Crypto.Encrypt(txtPassword.Text!);

                var encryptedChannelPass =
                    string.IsNullOrEmpty(txtChannelPassword.Text)
                        ? server?.DefaultChannelPassword
                        : Crypto.Encrypt(txtChannelPassword.Text!);

                if (server == null) {
                    _servers.Add(new ServerInfo {
                        Address = txtAddress.Text!,
                        Nickname = txtUser.Text,
                        DefaultChannel = txtChannel.Text,
                        ServerPassword = encryptedServerPass,
                        DefaultChannelPassword = encryptedChannelPass
                    });
                }
                else {
                    server.Address = txtAddress.Text!;
                    server.Nickname = txtUser.Text;
                    server.DefaultChannel = txtChannel.Text;
                    server.ServerPassword = encryptedServerPass;
                    server.DefaultChannelPassword = encryptedChannelPass;
                }

                SaveServers();
                _adapter!.NotifyDataSetChanged();
                dialog.Dismiss();
            };

            dialog.Show();
        }
    }
}
