using BookChannel = TSLib.Full.Book.Channel;
using BookClient = TSLib.Full.Book.Client;

namespace MobileTS.Activity.Server {
    public partial class ServerActivity {
        public enum ItemViewType {
            Channel = 0,
            Client = 1,
        }

        public abstract class ListItem {
            public abstract ItemViewType ViewType { get; }
        }

        public sealed class ChannelItem : ListItem {
            public BookChannel Channel { get; }

            public ChannelItem(BookChannel channel) {
                Channel = channel;
            }

            public override ItemViewType ViewType => ItemViewType.Channel;
        }

        public sealed class ClientItem : ListItem {
            public BookClient Client { get; }
            public bool IsTalking { get; set; }

            public ClientItem(BookClient client) {
                Client = client;
            }

            public override ItemViewType ViewType => ItemViewType.Client;
        }
    }
}
