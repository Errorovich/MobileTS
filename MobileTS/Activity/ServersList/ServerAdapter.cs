using Android.Views;
using AndroidX.RecyclerView.Widget;

namespace MobileTS.Activity.ServersList {
    public partial class ServersListActivity {
        private class ServerAdapter : RecyclerView.Adapter {
            private readonly List<ServerInfo> _items;
            private readonly ServersListActivity _activity;

            public ServerAdapter(List<ServerInfo> items, ServersListActivity activity) {
                _items = items;
                _activity = activity;
            }

            public override int ItemCount => _items.Count;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) {
                var view = LayoutInflater.From(parent.Context)!
                    .Inflate(Resource.Layout.item_server, parent, false);
                var vh = new ServerViewHolder(view);

                // Обработчики навешиваем один раз на холдер; актуальный элемент берём по позиции
                // во время клика, иначе при переиспользовании холдера подписки бы копились.
                vh.ItemView.Click += (_, _) => {
                    var server = ItemAt(vh);
                    if (server != null)
                        _activity.ConnectToServer(server);
                };
                vh.Edit.Click += (_, _) => {
                    var server = ItemAt(vh);
                    if (server != null)
                        _activity.ShowServerDialog(server);
                };
                vh.Delete.Click += (_, _) => {
                    var pos = vh.BindingAdapterPosition;
                    if (pos == RecyclerView.NoPosition)
                        return;
                    _items.RemoveAt(pos);
                    NotifyItemRemoved(pos);
                    _activity.SaveServers();
                };

                return vh;
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) {
                var vh = (ServerViewHolder)holder;
                var server = _items[position];

                vh.Address.Text = server.Address;
                vh.User.Text = $"User: {server.Nickname}";
                vh.Channel.Text = $"Channel: {server.DefaultChannel}";
            }

            private ServerInfo? ItemAt(RecyclerView.ViewHolder holder) {
                var pos = holder.BindingAdapterPosition;
                return pos == RecyclerView.NoPosition ? null : _items[pos];
            }
        }
    }
}
