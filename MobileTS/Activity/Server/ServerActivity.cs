using Android.Content;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using MobileTS.Audio;
using MobileTS.Services;

namespace MobileTS.Activity.Server {
    [Activity(Label = "Server", Theme = "@style/AppTheme.NoActionBar")]
    public partial class ServerActivity : Android.App.Activity {
        private RecyclerView _recycler = null!;
        private ServerTreeAdapter _adapter = null!;

        private readonly List<ListItem> _items = new();

        protected override void OnCreate(Bundle? savedInstanceState) {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_server);

            var toolbar = FindViewById<Toolbar>(Resource.Id.toolbar)!;
            SetActionBar(toolbar);
            // Пока Book не загружен, показываем адрес, переданный из списка серверов.
            Title = Intent?.GetStringExtra("server_title") ?? "Server";

            _recycler = FindViewById<RecyclerView>(Resource.Id.recycler)!;
            _recycler.SetLayoutManager(new LinearLayoutManager(this));

            _adapter = new ServerTreeAdapter(_items);
            _recycler.SetAdapter(_adapter);

            Client.OnClientIsTalkingChanged += OnClientTalkingChanged;
            // Book наполняется нотификациями уже после статуса Connected (channellist/клиенты
            // приходят позже), поэтому помимо первичной загрузки пересобираем дерево по событию.
            Client.OnBookChanged += OnBookChanged;

            _ = RefreshTree();
        }

        private void OnBookChanged() => _ = RefreshTree();

        private async Task RefreshTree() {
            // Полный клиент держит дерево сервера в Book; читаем снимок на потоке планировщика.
            var (serverName, channels, clients) = await Client.GetBookSnapshot();

            RunOnUiThread(() =>
            {
                if (!string.IsNullOrEmpty(serverName))
                    Title = serverName;

                _items.Clear();

                foreach (var channel in channels.OrderBy(c => c.Order.Value)) {
                    _items.Add(new ChannelItem(channel));

                    foreach (var client in clients.Where(c => c.Channel.Equals(channel.Id))) {
                        _items.Add(new ClientItem(client));
                    }
                }

                _adapter.NotifyDataSetChanged();
            });
        }

        public override bool OnCreateOptionsMenu(IMenu? menu) {
            MenuInflater.Inflate(Resource.Menu.menu_server, menu);
            return true;
        }

        public override bool OnOptionsItemSelected(IMenuItem item) {
            if (item.ItemId == Resource.Id.action_disconnect) {
                // Finish() ставит IsFinishing → OnDestroy остановит сервис и разорвёт соединение.
                Finish();
                return true;
            }

            return base.OnOptionsItemSelected(item);
        }

        private void OnClientTalkingChanged(VoiceActivationTrackerPipe.ClientVoiceStatus status) {
            RunOnUiThread(() =>
            {
                var item = _items
                    .OfType<ClientItem>()
                    .FirstOrDefault(c => c.Client.Id.Equals(status.Id));

                if (item == null)
                    return;

                item.IsTalking = status.Active;

                var index = _items.IndexOf(item);
                if (index >= 0)
                    _adapter.NotifyItemChanged(index);
            });
        }

        protected override void OnDestroy() {
            base.OnDestroy();

            Client.OnClientIsTalkingChanged -= OnClientTalkingChanged;
            Client.OnBookChanged -= OnBookChanged;

            // Соединение принадлежит ClientService и должно переживать поворот экрана/пересоздание
            // активити. Останавливаем сервис (а с ним и соединение) только при реальном выходе.
            if (IsFinishing)
                StopService(new Intent(this, typeof(ClientService)));
        }
    }
}
