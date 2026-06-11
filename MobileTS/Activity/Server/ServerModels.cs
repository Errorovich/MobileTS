using Android.Graphics;
using Android.Views;
using Android.Widget;
using BookChannel = TSLib.Full.Book.Channel;
using BookClient = TSLib.Full.Book.Client;

namespace MobileTS.Activity.Server {
    public partial class ServerFragment {
        // Карточка канала со списком участников (как карточка сервера в ServersFragment).
        public sealed class ChannelCardItem {
            public BookChannel Channel { get; }
            public List<BookClient> Members { get; }
            public bool Collapsed { get; set; }

            public ChannelCardItem(BookChannel channel, List<BookClient> members, bool collapsed) {
                Channel = channel;
                Members = members;
                Collapsed = collapsed;
            }
        }

        // Цвета статусов участника (приоритет: говорит > звук выкл > AFK).
        private static readonly Color TalkingColor = Color.Rgb(0, 160, 0);     // зелёный
        private static readonly Color SoundOffColor = Color.Rgb(0xD3, 0x2F, 0x2F); // красный
        private static readonly Color AwayColor = Color.Rgb(0xF5, 0x7C, 0x00);  // оранжевый

        // Привязывает строку участника (разметка item_client) к клиенту: имя, цвет статуса, иконки.
        // Используется и деревом каналов, и карточкой канала на экране чата.
        internal static void BindClientRow(View row, BookClient client) {
            var name = row.FindViewById<TextView>(Resource.Id.txtClientName)!;
            var micOff = row.FindViewById<ImageView>(Resource.Id.imgMicOff)!;
            var speakerOff = row.FindViewById<ImageView>(Resource.Id.imgSpeakerOff)!;
            var afk = row.FindViewById<ImageView>(Resource.Id.imgAfk)!;
            var afkMessage = row.FindViewById<TextView>(Resource.Id.txtAfkMessage)!;

            name.Text = client.Name;

            bool talking = MobileTS.Client.IsClientTalking(client.Id);
            bool soundOff = client.OutputMuted;
            bool away = MobileTS.Client.IsClientAway(client.Id);

            Color color = talking ? TalkingColor
                : soundOff ? SoundOffColor
                : away ? AwayColor
                : Color.Black;
            name.SetTextColor(color);

            // Мьют читаем из Book; AFK — из Client (Book.AwayMessage не сбрасывается при снятии).
            micOff.Visibility = client.InputMuted ? ViewStates.Visible : ViewStates.Gone;
            speakerOff.Visibility = client.OutputMuted ? ViewStates.Visible : ViewStates.Gone;
            afk.Visibility = away ? ViewStates.Visible : ViewStates.Gone;

            // Сообщение AFK показываем слева от значка — только пока клиент действительно отошёл
            // (Book.AwayMessage может остаться от прошлого AFK, поэтому гейтим по away).
            string? awayMsg = away ? client.AwayMessage : null;
            if (!string.IsNullOrEmpty(awayMsg)) {
                afkMessage.Text = awayMsg;
                afkMessage.Visibility = ViewStates.Visible;
            }
            else {
                afkMessage.Visibility = ViewStates.Gone;
            }
        }
    }
}
