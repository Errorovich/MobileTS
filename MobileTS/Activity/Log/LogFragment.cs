using Android.Views;

namespace MobileTS.Activity.Log {
    // Заглушка раздела «Журнал». Наполнить позже.
    public class LogFragment : Android.App.Fragment {
        public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
            => inflater.Inflate(Resource.Layout.fragment_log, container, false)!;

        public override void OnResume() {
            base.OnResume();
            Activity!.Title = "Журнал";
        }
    }
}
