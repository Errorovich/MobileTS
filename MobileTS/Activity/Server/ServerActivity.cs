using Android.Content;
using AndroidX.RecyclerView.Widget;
using MobileTS.Audio;
using MobileTS.Services;

namespace MobileTS.Activity.Server {
    [Activity(Label = "Server")]
    public partial class ServerActivity : Android.App.Activity {
        private RecyclerView _recycler = null!;
        private ServerTreeAdapter _adapter = null!;

        private readonly List<ListItem> _items = new();

        protected override void OnCreate(Bundle? savedInstanceState) {
            base.OnCreate(savedInstanceState);

            SetContentView(Resource.Layout.activity_server);

            _recycler = FindViewById<RecyclerView>(Resource.Id.recycler)!;
            _recycler.SetLayoutManager(new LinearLayoutManager(this));

            _adapter = new ServerTreeAdapter(_items);
            _recycler.SetAdapter(_adapter);

            Client.OnClientIsTalkingChanged += OnClientTalkingChanged;

            _ = LoadDataAsync();
        }

        private async Task LoadDataAsync() {
            var (okChannels, channels) = await Client.Invoke(c => c.ChannelList());
            var (okClients, clients) = await Client.Invoke(c => c.ClientList());

            if (!okChannels || !okClients)
                return;

            RunOnUiThread(() =>
            {
                _items.Clear();

                foreach (var channel in channels.OrderBy(c => c.Order)) {
                    _items.Add(new ChannelItem(channel));

                    foreach (var client in clients.Where(c => c.ChannelId.Equals(channel.ChannelId))) {
                        _items.Add(new ClientItem(client));
                    }
                }

                _adapter.NotifyDataSetChanged();
            });
        }

        private void OnClientTalkingChanged(VoiceActivationTrackerPipe.ClientVoiceStatus status) {
            RunOnUiThread(() =>
            {
                var item = _items
                    .OfType<ClientItem>()
                    .FirstOrDefault(c => c.Client.ClientId.Equals(status.Id));

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

            // Соединение принадлежит ClientService и должно переживать поворот экрана/пересоздание
            // активити. Останавливаем сервис (а с ним и соединение) только при реальном выходе.
            if (IsFinishing)
                StopService(new Intent(this, typeof(ClientService)));
        }
    }
}
