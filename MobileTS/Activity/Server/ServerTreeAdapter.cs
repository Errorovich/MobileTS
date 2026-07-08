using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using BookClient = TSLib.Full.Book.Client;

namespace MobileTS.Activity.Server {
    public partial class ServerFragment {
        public class ServerTreeAdapter : RecyclerView.Adapter {
            // Маркер «лёгкого» обновления (только перекраска участников, без переинфляции/анимации).
            private static readonly Java.Lang.Object RecolorPayload = new Java.Lang.String("recolor");

            private readonly List<ChannelCardItem> _items;
            private readonly Action<ChannelCardItem> _onChannelClick;
            private readonly Action<BookClient> _onClientClick;
            private readonly Action<ChannelCardItem> _onToggleCollapse;

            public ServerTreeAdapter(
                List<ChannelCardItem> items,
                Action<ChannelCardItem> onChannelClick,
                Action<BookClient> onClientClick,
                Action<ChannelCardItem> onToggleCollapse) {
                _items = items;
                _onChannelClick = onChannelClick;
                _onClientClick = onClientClick;
                _onToggleCollapse = onToggleCollapse;
            }

            public override int ItemCount => _items.Count;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) {
                var inflater = LayoutInflater.From(parent.Context)!;
                var view = inflater.Inflate(Resource.Layout.item_channel_card, parent, false)!;
                var vh = new ChannelCardViewHolder(view);

                // Клик-обработчики навешиваем один раз; актуальный элемент берём по позиции.
                vh.Name.Click += (_, _) => {
                    if (ItemAt(vh) is ChannelCardItem ci)
                        _onChannelClick(ci);
                };
                vh.Expand.Click += (_, _) => {
                    if (ItemAt(vh) is ChannelCardItem ci)
                        _onToggleCollapse(ci);
                };
                return vh;
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) {
                var vh = (ChannelCardViewHolder)holder;
                var item = _items[position];

                vh.Name.Text = item.Channel.Name;

                // 4.1 — у пустого канала кнопки сворачивания нет.
                bool hasMembers = item.Members.Count > 0;
                vh.Expand.Visibility = hasMembers ? ViewStates.Visible : ViewStates.Gone;

                bool expanded = hasMembers && !item.Collapsed;
                vh.Expand.Rotation = expanded ? 180f : 0f;
                vh.MembersContainer.Visibility = expanded ? ViewStates.Visible : ViewStates.Gone;

                vh.MembersContainer.RemoveAllViews();
                if (!expanded)
                    return;

                var inflater = LayoutInflater.From(vh.ItemView!.Context)!;
                foreach (var member in item.Members) {
                    var row = inflater.Inflate(Resource.Layout.item_client, vh.MembersContainer, false)!;
                    BindClientRow(row, member);
                    var captured = member;
                    row.Click += (_, _) => _onClientClick(captured);
                    vh.MembersContainer.AddView(row);
                }
            }

            // Лёгкое обновление: перекрашиваем уже построенные строки участников без переинфляции,
            // чтобы частые смены «говорит/молчит» не мигали и не пересобирали карточку.
            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position, IList<Java.Lang.Object> payloads) {
                if (payloads.Count == 0) {
                    OnBindViewHolder(holder, position);
                    return;
                }

                var vh = (ChannelCardViewHolder)holder;
                var item = _items[position];
                int count = Math.Min(vh.MembersContainer.ChildCount, item.Members.Count);
                for (int j = 0; j < count; j++) {
                    var child = vh.MembersContainer.GetChildAt(j);
                    if (child != null)
                        BindClientRow(child, item.Members[j]);
                }
            }

            // Перекрасить (без анимации) карточку канала, содержащую клиента.
            public void RecolorClient(TSLib.Shared.ClientId id) {
                for (int i = 0; i < _items.Count; i++) {
                    if (_items[i].Members.Any(m => m.Id.Equals(id))) {
                        NotifyItemChanged(i, RecolorPayload);
                        return;
                    }
                }
            }

            private ChannelCardItem? ItemAt(RecyclerView.ViewHolder holder) {
                var pos = holder.BindingAdapterPosition;
                return pos == RecyclerView.NoPosition ? null : _items[pos];
            }

            private sealed class ChannelCardViewHolder : RecyclerView.ViewHolder {
                public TextView Name { get; }
                public ImageButton Expand { get; }
                public LinearLayout MembersContainer { get; }

                public ChannelCardViewHolder(View itemView) : base(itemView) {
                    Name = itemView.FindViewById<TextView>(Resource.Id.txtChannelName)!;
                    Expand = itemView.FindViewById<ImageButton>(Resource.Id.btnExpandChannel)!;
                    MembersContainer = itemView.FindViewById<LinearLayout>(Resource.Id.membersContainer)!;
                }
            }
        }
    }
}
