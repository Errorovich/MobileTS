using Android.Content;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using System.Text.Json;
using static TSLib.Full.TsFullClient;

namespace MobileTS {
    [Activity(Label = "Серверы", MainLauncher = true)]
    public class ServersActivity : Activity {
        private const string PrefsName = "servers_storage";
        private const string ServersKey = "servers";

        private readonly List<ServerInfo> _servers = new();
        private ServerAdapter? _adapter;

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

        protected override void OnDestroy() {
            base.OnDestroy();
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

                vh.ItemView.Click += (_, _) => {
                    // Передаем весь объект в сервис, пароли остаются зашифрованными
                    Intent clientServiceIntent = new Intent(_activity, typeof(ClientService));
                    clientServiceIntent.PutExtra("server_info", JsonSerializer.Serialize(server));
                    _activity.StartService(clientServiceIntent);

                    // Показываем ProgressDialog
                    var progress = new ProgressDialog(_activity);
                    progress.SetMessage("Подключение...");
                    progress.SetCancelable(false);
                    progress.Show();

                    // Подписка на статус через SubscribeInstance
                    Client.SubscribeInstance(c => {
                        void StatusChanged(object? sender, TsClientStatus status) {
                            _activity.RunOnUiThread(() => {
                                if (status == TsClientStatus.Connected) {
                                    progress.Dismiss();
                                    _activity.StartActivity(new Intent(_activity, typeof(ServerActivity)));
                                }
                                else if (status == TsClientStatus.Disconnected) {
                                    progress.Dismiss();
                                }
                            });
                        }

                        c.OnStatusChangedEvent += StatusChanged;
                    });
                };

                vh.Edit.Click += (_, _) => _activity.ShowServerDialog(server);

                vh.Delete.Click += (_, _) => {
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
    }
}