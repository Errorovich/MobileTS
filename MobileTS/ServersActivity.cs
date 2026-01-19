using Android;
using Android.Content;
using Android.Content.PM;
using Android.Security.Keystore;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Text;
using System.Text.Json;
using TSLib.Full;
using TSLib.Messages;
using static TSLib.Full.TsFullClient;

namespace MobileTS {
    [Activity(Label = "Серверы", MainLauncher = true)]
    public class ServersActivity : Activity {
        private const string PrefsName = "servers_storage";
        private const string ServersKey = "servers";
        private const string KeyAlias = "server_password_key";

        private readonly List<ServerInfo> _servers = new();
        private ServerAdapter? _adapter;

        protected override void OnCreate(Bundle? savedInstanceState) {
            base.OnCreate(savedInstanceState);

            Client.Init(this);
            SetContentView(Resource.Layout.activity_servers);

            EnsureKey();

            var recycler = FindViewById<RecyclerView>(Resource.Id.recycler)!;
            recycler.SetLayoutManager(new LinearLayoutManager(this));

            _adapter = new ServerAdapter(_servers, this);
            recycler.SetAdapter(_adapter);

            LoadServers();
            _adapter.NotifyDataSetChanged();

            FindViewById<Button>(Resource.Id.btnAdd)!.Click += (_, _) => ShowServerDialog();
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
            if (string.IsNullOrEmpty(json))
                return;

            var list = JsonSerializer.Deserialize<List<ServerInfo>>(json);
            if (list != null) {
                _servers.Clear();
                _servers.AddRange(list);
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

                // Пароли НЕ подставляем
            }

            dialog.FindViewById<Button>(Resource.Id.btnSave)!.Click += (_, _) =>
            {
                var encryptedServerPass =
                    string.IsNullOrEmpty(txtPassword.Text)
                        ? server?.ServerPassword
                        : Encrypt(txtPassword.Text!);

                var encryptedChannelPass =
                    string.IsNullOrEmpty(txtChannelPassword.Text)
                        ? server?.DefaultChannelPassword
                        : Encrypt(txtChannelPassword.Text!);

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

        // ================= ADAPTER =================

        private class ServerAdapter : RecyclerView.Adapter {
            private readonly List<ServerInfo> _items;
            private readonly ServersActivity _activity;

            public ServerAdapter(List<ServerInfo> items, ServersActivity activity) {
                _items = items;
                _activity = activity;
            }

            public override int ItemCount => _items.Count;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) {
                var view = LayoutInflater.From(parent.Context)!
                    .Inflate(Resource.Layout.item_server, parent, false);
                return new ServerViewHolder(view);
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) {
                var vh = (ServerViewHolder)holder;
                var server = _items[position];

                vh.Address.Text = server.Address;
                vh.User.Text = $"User: {server.Nickname}";
                vh.Channel.Text = $"Channel: {server.DefaultChannel}";

                vh.ItemView.Click += (_, _) =>
                {
                    var nickname = string.IsNullOrWhiteSpace(server.Nickname) ? "Guest" : server.Nickname;
                    var serverPassword = _activity.Decrypt(server.ServerPassword) ?? "";
                    var channelPassword = _activity.Decrypt(server.DefaultChannelPassword) ?? "";

                    // Показываем ProgressDialog
                    var progress = new ProgressDialog(_activity);
                    progress.SetMessage("Подключение...");
                    progress.SetCancelable(false);
                    progress.Show();

                    // Метод обработки изменения статуса
                    void StatusChanged(object? sender, TsClientStatus status) {
                        _activity.RunOnUiThread(() =>
                        {
                            if (status == TsClientStatus.Connected) {
                                progress.Dismiss();

                                Client.Instance!.OnStatusChangedEvent -= StatusChanged;

                                _activity.StartActivity(new Intent(_activity, typeof(ServerActivity)));
                            }
                            else if (status == TsClientStatus.Disconnected) {
                                progress.Dismiss();

                                Client.Instance!.OnStatusChangedEvent -= StatusChanged;

                                // Возврат к списку серверов
                                // Если мы на ServerActivity, она должна вызвать Finish()
                            }
                        });
                    }

                    // Подписка на событие статуса с учётом того, что Instance может быть ещё null
                    if (Client.Instance != null) {
                        Client.Instance.OnStatusChangedEvent += StatusChanged;
                    }
                    else {
                        // Подписка на событие появления Instance
                        void InstanceReadyHandler(TsFullClient instance) {
                            instance.OnStatusChangedEvent += StatusChanged;
                            Client.OnInstanceReady -= InstanceReadyHandler; // отписываемся, чтобы не вызвать повторно
                        }

                        Client.OnInstanceReady += InstanceReadyHandler;
                    }

                    // Запуск подключения
                    Client.Connect(
                        server.Address,
                        nickname,
                        serverPassword,
                        server.DefaultChannel,
                        channelPassword
                    );
                };

                vh.Edit.Click += (_, _) => _activity.ShowServerDialog(server);

                vh.Delete.Click += (_, _) =>
                {
                    _items.RemoveAt(position);
                    NotifyItemRemoved(position);
                    _activity.SaveServers();
                };
            }
        }

        private class ServerViewHolder : RecyclerView.ViewHolder {
            public TextView Address { get; }
            public TextView User { get; }
            public TextView Channel { get; }
            public Button Edit { get; }
            public Button Delete { get; }

            public ServerViewHolder(View itemView) : base(itemView) {
                Address = itemView.FindViewById<TextView>(Resource.Id.txtAddress)!;
                User = itemView.FindViewById<TextView>(Resource.Id.txtUser)!;
                Channel = itemView.FindViewById<TextView>(Resource.Id.txtChannel)!;
                Edit = itemView.FindViewById<Button>(Resource.Id.btnEdit)!;
                Delete = itemView.FindViewById<Button>(Resource.Id.btnDelete)!;
            }
        }

        // ================= CRYPTO =================

        private void EnsureKey() {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);

            if (ks.ContainsAlias(KeyAlias))
                return;

            var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            var spec = new KeyGenParameterSpec.Builder(
                    KeyAlias,
                    KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .Build();

            keyGenerator.Init(spec);
            keyGenerator.GenerateKey();
        }

        private string? Encrypt(string? plainText) {
            if (string.IsNullOrEmpty(plainText))
                return null;

            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            var key = GetKey();

            cipher.Init(CipherMode.EncryptMode, key);

            var iv = cipher.GetIV();
            var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(plainText));

            var result = new byte[iv.Length + encrypted.Length];
            Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
            Buffer.BlockCopy(encrypted, 0, result, iv.Length, encrypted.Length);

            return Convert.ToBase64String(result);
        }

        private string? Decrypt(string? encryptedText) {
            if (string.IsNullOrEmpty(encryptedText))
                return null;

            var data = Convert.FromBase64String(encryptedText);
            var iv = data.Take(12).ToArray();
            var cipherText = data.Skip(12).ToArray();

            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            var key = GetKey();
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));

            return Encoding.UTF8.GetString(cipher.DoFinal(cipherText));
        }

        private IKey GetKey() {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);

            var entry = ks.GetEntry(KeyAlias, null) as KeyStore.SecretKeyEntry;
            if (entry == null)
                throw new InvalidOperationException("Keystore entry not found or invalid");

            return entry.SecretKey;
        }
    }
}