using Android.Views;
using AndroidX.RecyclerView.Widget;

namespace MobileTS.Activity.ServersList {
    public partial class ServersListActivity {
        private class ServerViewHolder : RecyclerView.ViewHolder {
            public TextView Address { get; }
            public TextView User { get; }
            public TextView Channel { get; }
            public Button Edit { get; }
            public Button Delete { get; }

            public ServerViewHolder(View itemView) : base(itemView) {
                Address = itemView.FindViewById<TextView>(Resource.Id.txtAddress)!;
                User = itemView.FindViewById<TextView>(Resource.Id.txtUser)!;
                Channel = itemView.FindViewById<TextView>(Resource.Id.txtChannel)!;
                Edit = itemView.FindViewById<Button>(Resource.Id.btnEdit)!;
                Delete = itemView.FindViewById<Button>(Resource.Id.btnDelete)!;
            }
        }
    }
}
