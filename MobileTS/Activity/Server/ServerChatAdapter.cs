using Android.Graphics;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;

namespace MobileTS.Activity.Server {
    public partial class ServerFragment {
        // Список сообщений чата текущего канала. Данные живут в _chatMessages фрагмента.
        // internal — параметр конструктора использует internal-тип Client.ChatMessage.
        internal class ChatAdapter : RecyclerView.Adapter {
            private readonly List<Client.ChatMessage> _messages;

            internal ChatAdapter(List<Client.ChatMessage> messages) {
                _messages = messages;
            }

            public override int ItemCount => _messages.Count;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) {
                var view = LayoutInflater.From(parent.Context)!
                    .Inflate(Resource.Layout.item_chat_message, parent, false)!;
                return new ChatViewHolder(view);
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) {
                var vh = (ChatViewHolder)holder;
                var msg = _messages[position];

                vh.Sender.Text = $"{msg.Sender} · {msg.Time:HH:mm}";
                // Свои сообщения подсвечиваем зелёным заголовком, чужие — серо-синим.
                vh.Sender.SetTextColor(msg.IsOwn ? Color.Rgb(0, 130, 0) : Color.Rgb(96, 125, 139));
                vh.Text.Text = msg.Text;
            }

            private sealed class ChatViewHolder : RecyclerView.ViewHolder {
                public TextView Sender { get; }
                public TextView Text { get; }

                public ChatViewHolder(View itemView) : base(itemView) {
                    Sender = itemView.FindViewById<TextView>(Resource.Id.txtChatSender)!;
                    Text = itemView.FindViewById<TextView>(Resource.Id.txtChatText)!;
                }
            }
        }
    }
}
