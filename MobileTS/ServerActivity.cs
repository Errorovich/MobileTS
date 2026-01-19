using Android.Graphics;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using MobileTS.Audio;
using TSLib.Messages;

namespace MobileTS {
    [Activity(Label = "Server")]
    public class ServerActivity : Activity {
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

            _ = Client.Disconnect();
        }

        public abstract class ListItem {
            public abstract int ViewType { get; }
        }

        public sealed class ChannelItem : ListItem {
            public ChannelListResponse Channel { get; }

            public ChannelItem(ChannelListResponse channel) {
                Channel = channel;
            }

            public override int ViewType => 0;
        }

        public sealed class ClientItem : ListItem {
            public ClientList Client { get; }
            public bool IsTalking { get; set; }

            public ClientItem(ClientList client) {
                Client = client;
            }

            public override int ViewType => 1;
        }

        public class ServerTreeAdapter : RecyclerView.Adapter {
            private const int ViewTypeChannel = 0;
            private const int ViewTypeClient = 1;

            private readonly List<ListItem> _items;

            public ServerTreeAdapter(List<ListItem> items) {
                _items = items;
            }

            public override int ItemCount => _items.Count;

            public override int GetItemViewType(int position)
                => _items[position].ViewType;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) {
                var inflater = LayoutInflater.From(parent.Context)!;

                if (viewType == ViewTypeChannel) {
                    var view = inflater.Inflate(Resource.Layout.item_channel, parent, false);
                    return new ChannelViewHolder(view);
                }
                else {
                    var view = inflater.Inflate(Resource.Layout.item_client, parent, false);
                    return new ClientViewHolder(view);
                }
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) {
                var item = _items[position];

                if (item is ChannelItem channelItem) {
                    var vh = (ChannelViewHolder)holder;
                    vh.Name.Text = channelItem.Channel.Name;
                }
                else if (item is ClientItem clientItem) {
                    var vh = (ClientViewHolder)holder;
                    vh.Name.Text = clientItem.Client.Name;

                    vh.Name.SetTextColor(
                        clientItem.IsTalking
                            ? Color.Rgb(0, 160, 0)
                            : Color.White
                    );
                }
            }

            private sealed class ChannelViewHolder : RecyclerView.ViewHolder {
                public TextView Name { get; }

                public ChannelViewHolder(View itemView) : base(itemView) {
                    Name = itemView.FindViewById<TextView>(Resource.Id.txtChannelName)!;
                }
            }

            private sealed class ClientViewHolder : RecyclerView.ViewHolder {
                public TextView Name { get; }

                public ClientViewHolder(View itemView) : base(itemView) {
                    Name = itemView.FindViewById<TextView>(Resource.Id.txtClientName)!;
                }
            }
        }
    }
}
