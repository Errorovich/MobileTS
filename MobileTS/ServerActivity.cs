using Android.Views;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MobileTS {
    [Activity(Label = "ServerActivity")]
    public class ServerActivity : Activity {
        protected override void OnCreate(Bundle? savedInstanceState) {
            base.OnCreate(savedInstanceState);

            var textView = new TextView(this) {
                Text = "Server screen",
                Gravity = GravityFlags.Center,
                TextSize = 24f
            };

            SetContentView(textView);
        }

        protected override void OnDestroy() {
            base.OnDestroy();

            // Асинхронно вызываем Disconnect, не блокируя UI
            Task.Run(Client.Disconnect);
        }
    }
}
