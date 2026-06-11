using Android.Content;
using Android.Graphics.Drawables;
using Android.Views;
using Android.Widget;
using AndroidX.Core.View;
using AndroidX.DrawerLayout.Widget;
using MobileTS.Activity.Log;
using MobileTS.Activity.Server;
using MobileTS.Activity.ServersList;
using MobileTS.Activity.Settings;

namespace MobileTS {
    // Единственная «настоящая» Activity приложения. Держит боковое меню (DrawerLayout) и ActionBar,
    // а экраны показывает фрагментами в content_frame — так меню не пересоздаётся и не моргает при
    // переходах между разделами.
    [Activity(Label = "@string/app_name", Theme = "@style/AppTheme", MainLauncher = true)]
    public class MainActivity : Android.App.Activity {
        private const string StateConnectedTitle = "connected_title";
        private const string StateConnectedAddress = "connected_address";

        private DrawerLayout _drawer = null!;
        private LinearLayout _connectedContainer = null!;

        // Заголовок текущего подключённого сервера (пока один; в будущем — список).
        // Пустая строка — нет активного подключения.
        private string _connectedTitle = "";

        // Адрес подключённого сервера — ключ кэша иконки (заголовок становится именем из Book).
        private string _connectedAddress = "";

        // Показываем ли сейчас иконку сервера в ActionBar (экран сервера). Нужно, чтобы докачка иконки
        // обновляла ActionBar только когда он действительно показывает сервер.
        private bool _showServerIcon;

        protected override void OnCreate(Bundle? savedInstanceState) {
            base.OnCreate(savedInstanceState);

            Client.Init(this);

            SetContentView(Resource.Layout.activity_main);

            // Соединение живёт в ClientService и переживает поворот экрана — восстанавливаем шапку,
            // чтобы она не сбрасывалась в «нет подключений» при пересоздании активити.
            _connectedTitle = savedInstanceState?.GetString(StateConnectedTitle) ?? "";
            _connectedAddress = savedInstanceState?.GetString(StateConnectedAddress) ?? "";

            // Иконка сервера докачивается асинхронно — перестраиваем шапку, когда она готова.
            Client.OnServerIconReady += OnServerIconReady;

            _drawer = FindViewById<DrawerLayout>(Resource.Id.drawer_layout)!;
            _connectedContainer = FindViewById<LinearLayout>(Resource.Id.connectedServersContainer)!;

            // «Три полоски» в ActionBar открывают боковое меню.
            ActionBar?.SetDisplayHomeAsUpEnabled(true);
            ActionBar?.SetHomeAsUpIndicator(Resource.Drawable.ic_menu);

            FindViewById<View>(Resource.Id.nav_servers)!.Click += (_, _) => { CloseDrawer(); ShowServersList(); };
            FindViewById<View>(Resource.Id.nav_settings)!.Click += (_, _) => { CloseDrawer(); Push(new SettingsFragment()); };
            FindViewById<View>(Resource.Id.nav_log)!.Click += (_, _) => { CloseDrawer(); Push(new LogFragment()); };

            RebuildConnectedServers();

            // Стартовый экран ставим только при первом создании: при пересоздании (поворот) фрагмент
            // восстановит FragmentManager сам.
            if (savedInstanceState == null)
                ShowRoot(new ServersFragment());
        }

        // ===================== Навигация =====================

        // Корневой экран: чистим back stack, чтобы «Назад» с него выходил из приложения.
        // CommitAllowingStateLoss — переход может прийти после onSaveInstanceState (например,
        // подключение завершилось, пока активити в фоне), и обычный Commit бросил бы исключение.
        private void ShowRoot(Android.App.Fragment fragment) {
            FragmentManager.PopBackStackImmediate(null, Android.App.PopBackStackFlags.Inclusive);
            FragmentManager.BeginTransaction()
                .Replace(Resource.Id.content_frame, fragment)
                .CommitAllowingStateLoss();
        }

        private void Push(Android.App.Fragment fragment) {
            FragmentManager.BeginTransaction()
                .Replace(Resource.Id.content_frame, fragment)
                .AddToBackStack(null)
                .CommitAllowingStateLoss();
        }

        public void ShowServersList() => ShowRoot(new ServersFragment());

        // Вызывается из ServersFragment после успешного подключения.
        public void ShowServer(ServerInfo server) {
            _connectedTitle = server.Address;
            _connectedAddress = server.Address;
            RebuildConnectedServers();
            Push(ServerFragment.New(_connectedTitle));
        }

        // ServerFragment сообщает настоящее имя сервера из Book.
        public void UpdateConnectedTitle(string title) {
            if (string.IsNullOrEmpty(_connectedTitle) || string.IsNullOrEmpty(title))
                return;
            _connectedTitle = title;
            RebuildConnectedServers();
        }

        // Явное отключение (пункт «Отключиться» на экране сервера).
        public void DisconnectCurrent() {
            StopService(new Intent(this, typeof(Services.ClientService)));
            _connectedTitle = "";
            _connectedAddress = "";
            RebuildConnectedServers();
            ShowServersList();
        }

        protected override void OnSaveInstanceState(Bundle outState) {
            base.OnSaveInstanceState(outState);
            outState.PutString(StateConnectedTitle, _connectedTitle);
            outState.PutString(StateConnectedAddress, _connectedAddress);
        }

        protected override void OnDestroy() {
            Client.OnServerIconReady -= OnServerIconReady;
            base.OnDestroy();
        }

        private void OnServerIconReady(string address) {
            RunOnUiThread(() => {
                if (address != _connectedAddress)
                    return;
                RebuildConnectedServers();
                if (_showServerIcon)
                    ShowServerActionBarIcon();
            });
        }

        // ===================== Иконка сервера в ActionBar (§2) =====================

        // Показывает иконку текущего сервера (или заглушку) логотипом ActionBar. Вызывается с экрана
        // сервера; на остальных экранах логотип скрывается через HideServerActionBarIcon.
        public void ShowServerActionBarIcon() {
            _showServerIcon = true;

            var iconPath = string.IsNullOrEmpty(_connectedAddress) ? null : Client.GetServerIconPathIfExists(_connectedAddress);
            var bitmap = iconPath == null ? null : Android.Graphics.BitmapFactory.DecodeFile(iconPath);

            Drawable? icon = bitmap != null
                ? new BitmapDrawable(Resources, bitmap)
                : Resources!.GetDrawable(Resource.Drawable.ic_server_placeholder, Theme);

            if (icon != null)
                ActionBar?.SetLogo(icon);
            ActionBar?.SetDisplayUseLogoEnabled(true);
            ActionBar?.SetDisplayShowHomeEnabled(true);
        }

        public void HideServerActionBarIcon() {
            _showServerIcon = false;
            ActionBar?.SetDisplayUseLogoEnabled(false);
        }

        // ===================== Боковое меню =====================

        public void OpenDrawer() => _drawer.OpenDrawer(GravityCompat.Start);
        private void CloseDrawer() => _drawer.CloseDrawer(GravityCompat.Start);

        public override bool OnOptionsItemSelected(IMenuItem item) {
            if (item.ItemId == Android.Resource.Id.Home) {
                OpenDrawer();
                return true;
            }
            return base.OnOptionsItemSelected(item);
        }

        public override void OnBackPressed() {
            if (_drawer.IsDrawerOpen(GravityCompat.Start)) {
                CloseDrawer();
                return;
            }
            base.OnBackPressed();
        }

        // Перестраивает список подключённых серверов в шапке меню. Строка выровнена так же, как пункты
        // меню (nav_*): отступ строки 20dp, иконка 24dp, текст с marginLeft 28dp. Нет иконки — заглушка.
        private void RebuildConnectedServers() {
            _connectedContainer.RemoveAllViews();

            if (string.IsNullOrEmpty(_connectedTitle)) {
                _connectedContainer.AddView(BuildHeaderText("Нет активных подключений", "#8A8A8A", clickable: false, null));
                return;
            }

            var iconPath = string.IsNullOrEmpty(_connectedAddress) ? null : Client.GetServerIconPathIfExists(_connectedAddress);
            var bitmap = iconPath == null ? null : Android.Graphics.BitmapFactory.DecodeFile(iconPath);

            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            row.SetGravity(GravityFlags.CenterVertical);
            row.SetMinimumHeight(Dp(52));
            row.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
            row.Clickable = true;
            row.Click += (_, _) => {
                CloseDrawer();
                Push(ServerFragment.New(_connectedTitle));
            };
            var outValue = new Android.Util.TypedValue();
            Theme!.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, outValue, true);
            row.SetBackgroundResource(outValue.ResourceId);

            var img = new ImageView(this);
            img.LayoutParameters = new LinearLayout.LayoutParams(Dp(24), Dp(24));
            if (bitmap != null)
                img.SetImageBitmap(bitmap);
            else
                img.SetImageResource(Resource.Drawable.ic_server_placeholder);
            row.AddView(img);

            var text = new TextView(this) { Text = _connectedTitle, TextSize = 16 };
            text.SetTextColor(Android.Graphics.Color.ParseColor("#FFFFFF"));
            var textLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            textLp.SetMargins(Dp(28), 0, 0, 0);
            text.LayoutParameters = textLp;
            row.AddView(text);

            _connectedContainer.AddView(row);
        }

        private TextView BuildHeaderText(string text, string color, bool clickable, Action? onClick) {
            var tv = new TextView(this) {
                Text = text,
                TextSize = 16,
            };
            tv.SetTextColor(Android.Graphics.Color.ParseColor(color));
            tv.SetPadding(Dp(20), Dp(12), Dp(20), Dp(12));
            if (clickable && onClick != null) {
                tv.Clickable = true;
                tv.Click += (_, _) => onClick();
                var outValue = new Android.Util.TypedValue();
                Theme!.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, outValue, true);
                tv.SetBackgroundResource(outValue.ResourceId);
            }
            return tv;
        }

        private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);
    }
}
