using TSLib.Messages;

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
            public ChannelListResponse Channel { get; }

            public ChannelItem(ChannelListResponse channel) {
                Channel = channel;
            }

            public override ItemViewType ViewType => ItemViewType.Channel;
        }

        public sealed class ClientItem : ListItem {
            public ClientList Client { get; }
            public bool IsTalking { get; set; }

            public ClientItem(ClientList client) {
                Client = client;
            }

            public override ItemViewType ViewType => ItemViewType.Client;
        }
    }
}
