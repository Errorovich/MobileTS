using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;

namespace MobileTS.Activity.ServersList {
    public partial class ServersFragment {
        private class ServerViewHolder : RecyclerView.ViewHolder {
            public TextView Title { get; }
            public ImageView Icon { get; }
            public ImageButton Expand { get; }
            public View CardBody { get; }

            public View RowAlias { get; }
            public View RowAddress { get; }
            public View RowNickname { get; }
            public View RowServerPassword { get; }
            public View RowChannel { get; }
            public View RowChannelPassword { get; }

            public EditText Alias { get; }
            public EditText Address { get; }
            public EditText Nickname { get; }
            public EditText ServerPassword { get; }
            public EditText Channel { get; }
            public EditText ChannelPassword { get; }

            public ImageButton ToggleServerPassword { get; }
            public ImageButton ToggleChannelPassword { get; }

            public ImageButton Edit { get; }
            public ImageButton Delete { get; }

            public ServerViewHolder(View itemView) : base(itemView) {
                Title = itemView.FindViewById<TextView>(Resource.Id.txtTitle)!;
                Icon = itemView.FindViewById<ImageView>(Resource.Id.imgServerIcon)!;
                Expand = itemView.FindViewById<ImageButton>(Resource.Id.btnExpand)!;
                CardBody = itemView.FindViewById<View>(Resource.Id.cardBody)!;

                RowAlias = itemView.FindViewById<View>(Resource.Id.rowAlias)!;
                RowAddress = itemView.FindViewById<View>(Resource.Id.rowAddress)!;
                RowNickname = itemView.FindViewById<View>(Resource.Id.rowNickname)!;
                RowServerPassword = itemView.FindViewById<View>(Resource.Id.rowServerPassword)!;
                RowChannel = itemView.FindViewById<View>(Resource.Id.rowChannel)!;
                RowChannelPassword = itemView.FindViewById<View>(Resource.Id.rowChannelPassword)!;

                Alias = itemView.FindViewById<EditText>(Resource.Id.txtAlias)!;
                Address = itemView.FindViewById<EditText>(Resource.Id.txtAddress)!;
                Nickname = itemView.FindViewById<EditText>(Resource.Id.txtNickname)!;
                ServerPassword = itemView.FindViewById<EditText>(Resource.Id.txtServerPassword)!;
                Channel = itemView.FindViewById<EditText>(Resource.Id.txtChannel)!;
                ChannelPassword = itemView.FindViewById<EditText>(Resource.Id.txtChannelPassword)!;

                ToggleServerPassword = itemView.FindViewById<ImageButton>(Resource.Id.btnToggleServerPassword)!;
                ToggleChannelPassword = itemView.FindViewById<ImageButton>(Resource.Id.btnToggleChannelPassword)!;

                Edit = itemView.FindViewById<ImageButton>(Resource.Id.btnEdit)!;
                Delete = itemView.FindViewById<ImageButton>(Resource.Id.btnDelete)!;
            }
        }
    }
}
