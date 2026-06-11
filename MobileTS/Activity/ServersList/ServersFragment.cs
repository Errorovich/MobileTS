using Android.Content;
using Android.Content.PM;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using System.Text.Json;
using MobileTS.Services;
using TSLib;
using static TSLib.Full.TsFullClient;

namespace MobileTS.Activity.ServersList {
    // Список сохранённых серверов и запуск подключения. Раньше — ServersListActivity; теперь фрагмент
    // в MainActivity, чтобы боковое меню оставалось общим для всех экранов.
    public partial class ServersFragment : Fragment {
        private const string PrefsName = "servers_storage";
        private const string ServersKey = "servers";
        private const int RecordAudioRequestCode = 1;

        private readonly List<ServerInfo> _servers = new();
        private ServerAdapter? _adapter;
        private RecyclerView? _recycler;

        // Сервер, к которому подключаемся, как только пользователь выдаст RECORD_AUDIO.
        private ServerInfo? _pendingConnectServer;

        private MainActivity Host => (MainActivity)Activity!;

        public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) {
            var view = inflater.Inflate(Resource.Layout.fragment_servers, container, false)!;

            _recycler = view.FindViewById<RecyclerView>(Resource.Id.recycler)!;
            _recycler.SetLayoutManager(new LinearLayoutManager(Activity));

            _adapter = new ServerAdapter(_servers, this);
            _recycler.SetAdapter(_adapter);

            // §8 — сортировка серверов перетаскиванием по долгому удержанию.
            var dragCallback = new ServerDragCallback(_servers, _adapter, SaveServers);
            new ItemTouchHelper(dragCallback).AttachToRecyclerView(_recycler);

            LoadServers();
            _adapter.NotifyDataSetChanged();

            // Кнопка добавления вынесена в ActionBar (см. OnCreateOptionsMenu).
            SetHasOptionsMenu(true);

            return view;
        }

        public override void OnResume() {
            base.OnResume();
            Activity!.Title = "Серверa";
        }

        public override void OnCreateOptionsMenu(IMenu? menu, MenuInflater? inflater) {
            inflater!.Inflate(Resource.Menu.menu_servers, menu);
        }

        public override bool OnOptionsItemSelected(IMenuItem item) {
            if (item.ItemId == Resource.Id.action_add_server) {
                AddServer();
                return true;
            }

            return base.OnOptionsItemSelected(item);
        }

        // ================= CONNECT =================

        // Подключение запускает FGS типа microphone, что требует выданного RECORD_AUDIO.
        // Если разрешения ещё нет — запоминаем сервер и запрашиваем; продолжаем в колбэке.
        public void ConnectToServer(ServerInfo server) {
            if (Activity!.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Permission.Granted) {
                _pendingConnectServer = server;
                RequestPermissions([Android.Manifest.Permission.RecordAudio], RecordAudioRequestCode);
                return;
            }

            StartConnection(server);
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults) {
            if (requestCode != RecordAudioRequestCode)
                return;

            var server = _pendingConnectServer;
            _pendingConnectServer = null;

            if (server != null && grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                StartConnection(server);
            else
                Toast.MakeText(Activity, "Нет доступа к микрофону — подключение невозможно", ToastLength.Long)?.Show();
        }

        private void StartConnection(ServerInfo server) {
            // Передаем весь объект в сервис, пароли остаются зашифрованными
            var clientServiceIntent = new Intent(Activity, typeof(ClientService));
            clientServiceIntent.PutExtra("server_info", JsonSerializer.Serialize(server));
            Activity!.StartService(clientServiceIntent);

            // Подключение можно прервать кнопкой «Назад»: диалог отменяемый, по отмене останавливаем
            // сервис — его OnDestroy вызывает Client.Disconnect, который прерывает подключение.
            bool canceled = false;

            var progress = new ProgressDialog(Activity);
            progress.SetMessage("Подключение...");
            progress.SetCancelable(true);
            progress.CancelEvent += (_, _) => {
                canceled = true;
                Activity!.StopService(new Intent(Activity, typeof(ClientService)));
                Toast.MakeText(Activity, "Подключение отменено", ToastLength.Short)?.Show();
            };
            progress.Show();

            Client.SubscribeInstance(c => {
                // OnDisconnected приходит перед терминальным OnStatusChangedEvent и несёт причину
                // обрыва — запоминаем её, чтобы показать пользователю при неудачном подключении.
                DisconnectEventArgs? disconnectInfo = null;

                void Disconnected(object? sender, DisconnectEventArgs e) => disconnectInfo = e;

                void StatusChanged(object? sender, TsClientStatus status) {
                    if (status != TsClientStatus.Connected && status != TsClientStatus.Disconnected)
                        return;

                    // Терминальный статус — снимаем подписки, иначе обработчики копятся.
                    c.OnStatusChangedEvent -= StatusChanged;
                    c.OnDisconnected -= Disconnected;

                    Activity?.RunOnUiThread(() => {
                        progress.Dismiss();

                        // Пользователь сам прервал подключение — без диалога ошибки.
                        if (canceled)
                            return;

                        if (status == TsClientStatus.Connected)
                            Host.ShowServer(server);
                        else
                            ShowConnectionError(disconnectInfo);
                    });
                }

                c.OnDisconnected += Disconnected;
                c.OnStatusChangedEvent += StatusChanged;
            });
        }

        // Показывает причину неудачного подключения, чтобы окно "Подключение..." не закрывалось молча.
        private void ShowConnectionError(DisconnectEventArgs? info) {
            string reason = DescribeDisconnect(info);

            new AlertDialog.Builder(Activity)
                .SetTitle("Не удалось подключиться")!
                .SetMessage(reason)!
                .SetPositiveButton("OK", (EventHandler<DialogClickEventArgs>?)null)!
                .Show();
        }

        private static string DescribeDisconnect(DisconnectEventArgs? info) {
            // Сообщение сервера/ошибки приоритетнее обобщённой причины обрыва.
            var error = info?.Error;
            if (error != null && !string.IsNullOrEmpty(error.Message) && error.Message != "Connection closed")
                return error.Message!;

            return info?.ExitReason switch {
                Reason.Timeout => "Превышено время ожидания ответа сервера. Проверьте адрес и подключение к сети.",
                Reason.SocketError => "Не удалось установить соединение с сервером. Проверьте адрес и подключение к сети.",
                Reason.Banned => "Доступ к серверу заблокирован (бан).",
                Reason.KickedFromServer => "Вы были исключены с сервера.",
                Reason.ServerStopped or Reason.ServerShutdown => "Сервер недоступен (остановлен).",
                _ => "Сервер разорвал соединение. Проверьте адрес, пароль и параметры подключения.",
            };
        }

        // ================= STORAGE =================

        private void SaveServers() {
            var prefs = Activity!.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var json = JsonSerializer.Serialize(_servers);
            prefs!.Edit()!.PutString(ServersKey, json).Apply();
        }

        private void LoadServers() {
            var prefs = Activity!.GetSharedPreferences(PrefsName, FileCreationMode.Private);
            var json = prefs!.GetString(ServersKey, null);
            if (!string.IsNullOrEmpty(json)) {
                var list = JsonSerializer.Deserialize<List<ServerInfo>>(json);
                if (list != null) {
                    _servers.Clear();
                    _servers.AddRange(list);
                }
            }
        }

        // ================= ADD =================

        // Добавление нового сервера: создаём пустую карточку сразу в режиме редактирования внизу
        // списка и прокручиваем к ней. В хранилище сервер попадёт, когда пользователь нажмёт
        // «галочку» (сохранение в ServerAdapter). Незаполненную карточку можно убрать «корзиной».
        private void AddServer() {
            var server = new ServerInfo { IsExpanded = true, IsEditing = true };
            _servers.Add(server);

            int position = _servers.Count - 1;
            _adapter!.NotifyItemInserted(position);
            _recycler?.ScrollToPosition(position);
        }

        // ================= DRAG (§8) =================

        // Перетаскивание серверов по долгому удержанию (long-press включён по умолчанию у
        // ItemTouchHelper). Свайпы выключены. Порядок = порядок списка _servers, сохраняем при отпускании.
        private sealed class ServerDragCallback : ItemTouchHelper.SimpleCallback {
            private readonly List<ServerInfo> _items;
            private readonly RecyclerView.Adapter _adapter;
            private readonly Action _save;
            private bool _moved;

            public ServerDragCallback(List<ServerInfo> items, RecyclerView.Adapter adapter, Action save)
                : base(ItemTouchHelper.Up | ItemTouchHelper.Down, 0) {
                _items = items;
                _adapter = adapter;
                _save = save;
            }

            public override bool OnMove(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder, RecyclerView.ViewHolder target) {
                int from = viewHolder.BindingAdapterPosition;
                int to = target.BindingAdapterPosition;
                if (from == RecyclerView.NoPosition || to == RecyclerView.NoPosition)
                    return false;

                var item = _items[from];
                _items.RemoveAt(from);
                _items.Insert(to, item);
                _adapter.NotifyItemMoved(from, to);
                _moved = true;
                return true;
            }

            public override void OnSwiped(RecyclerView.ViewHolder viewHolder, int direction) { }

            public override void ClearView(RecyclerView recyclerView, RecyclerView.ViewHolder viewHolder) {
                base.ClearView(recyclerView, viewHolder);
                if (_moved) {
                    _moved = false;
                    _save();
                }
            }
        }
    }
}
