using Android.Graphics;
using Android.Views;
using AndroidX.RecyclerView.Widget;

namespace MobileTS.Activity.Server {
    public partial class ServerActivity {
        public class ServerTreeAdapter : RecyclerView.Adapter {
            private readonly List<ListItem> _items;

            public ServerTreeAdapter(List<ListItem> items) {
                _items = items;
            }

            public override int ItemCount => _items.Count;

            public override int GetItemViewType(int position)
                => (int)_items[position].ViewType;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) {
                var inflater = LayoutInflater.From(parent.Context)!;

                if ((ItemViewType)viewType == ItemViewType.Channel) {
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
                            : Color.Black
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
