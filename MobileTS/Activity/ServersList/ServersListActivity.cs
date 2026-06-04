using Android.Content;
using AndroidX.RecyclerView.Widget;
using System.Text.Json;
using MobileTS.Activity.Server;

namespace MobileTS.Activity.ServersList {
    [Activity(Label = "Серверы", MainLauncher = true)]
    public partial class ServersListActivity : Android.App.Activity {
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
