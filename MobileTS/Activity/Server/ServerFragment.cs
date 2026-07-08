using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;
using MobileTS.Audio;
using TSLib.Shared;
using TSLib.Audio.Pipes;
using BookChannel = TSLib.Full.Book.Channel;
using BookClient = TSLib.Full.Book.Client;

namespace MobileTS.Activity.Server {
    // Дерево каналов/клиентов подключённого сервера. ActionBar и боковое меню предоставляет хост,
    // поэтому собственного Toolbar нет.
    public partial class ServerFragment : Android.App.Fragment {
        private View _rootView = null!;

        private RecyclerView _recycler = null!;
        private ServerTreeAdapter _adapter = null!;

        private readonly List<ChannelCardItem> _items = new();
        // Свёрнутость каналов (переживает NotifyDataSetChanged). По умолчанию канал развёрнут.
        private readonly Dictionary<ChannelId, bool> _collapsed = new();

        // Чат текущего канала.
        private View _chatContainer = null!;
        private RecyclerView _chatRecycler = null!;
        private ChatAdapter _chatAdapter = null!;
        private EditText _chatInput = null!;
        private readonly List<Client.ChatMessage> _chatMessages = new();

        // §7 — карточка текущего канала на экране чата (строится программно).
        private FrameLayout _chatCardHolder = null!;
        private LinearLayout? _chatCard;
        private TextView? _chatCardName;
        private ImageButton? _chatCardExpand;
        private MaxHeightScrollView? _chatCardScroll;
        private LinearLayout? _chatCardMembersContainer;
        private List<BookClient> _chatCardMemberList = new();
        private bool _chatCardCollapsed;

        // §6 — компактная нижняя панель.
        private Button _btnPrimary = null!;
        private ImageButton _btnViewToggle = null!;
        private ImageButton _btnMore = null!;
        private bool _pushToTalk;
        private bool _showingChannels = true;

        private string _title = "Server";

        private MainActivity Host => (MainActivity)Activity!;

        public static ServerFragment New(string title) {
            var fragment = new ServerFragment();
            var args = new Bundle();
            args.PutString("server_title", title);
            fragment.Arguments = args;
            return fragment;
        }

        public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) {
            // Пока Book не загружен, показываем адрес, переданный из списка серверов.
            _title = Arguments?.GetString("server_title") ?? "Server";

            var view = inflater.Inflate(Resource.Layout.fragment_server, container, false)!;
            _rootView = view;

            _recycler = view.FindViewById<RecyclerView>(Resource.Id.recycler)!;
            _recycler.SetLayoutManager(new LinearLayoutManager(Activity));
            _adapter = new ServerTreeAdapter(_items, OnChannelClicked, OnClientClicked, OnToggleCollapse);
            _recycler.SetAdapter(_adapter);

            // --- Чат текущего канала ---
            _chatContainer = view.FindViewById<View>(Resource.Id.chatContainer)!;
            _chatCardHolder = view.FindViewById<FrameLayout>(Resource.Id.chatChannelCardHolder)!;
            BuildChatCard();

            _chatRecycler = view.FindViewById<RecyclerView>(Resource.Id.chatRecycler)!;
            _chatRecycler.SetLayoutManager(new LinearLayoutManager(Activity));
            _chatMessages.Clear();
            _chatMessages.AddRange(Client.GetChatHistory());
            _chatAdapter = new ChatAdapter(_chatMessages);
            _chatRecycler.SetAdapter(_chatAdapter);

            _chatInput = view.FindViewById<EditText>(Resource.Id.txtChatInput)!;
            view.FindViewById<ImageButton>(Resource.Id.btnSend)!.Click += (_, _) => SendChat();

            // --- Нижняя панель ---
            _btnPrimary = view.FindViewById<Button>(Resource.Id.btnPrimary)!;
            _btnViewToggle = view.FindViewById<ImageButton>(Resource.Id.btnViewToggle)!;
            _btnMore = view.FindViewById<ImageButton>(Resource.Id.btnMore)!;

            // Широкая кнопка: в режиме «по кнопке» работает как зажми-говори (Touch), в режиме «по
            // голосу» — как переключатель микрофона (Click). Гейтим по текущему режиму.
            _btnPrimary.Touch += (_, e) => {
                if (!_pushToTalk) {
                    e.Handled = false;
                    return;
                }
                switch (e.Event!.ActionMasked) {
                    case MotionEventActions.Down:
                        Client.SetPushToTalk(true);
                        _btnPrimary.Pressed = true;
                        break;
                    case MotionEventActions.Up:
                    case MotionEventActions.Cancel:
                        Client.SetPushToTalk(false);
                        _btnPrimary.Pressed = false;
                        break;
                }
                e.Handled = true;
            };
            _btnPrimary.Click += (_, _) => {
                if (_pushToTalk)
                    return;
                Client.SetMicMuted(!Client.IsMicMuted);
                UpdatePrimaryButton();
            };

            _btnViewToggle.Click += (_, _) => ShowTab(channels: !_showingChannels);
            _btnMore.Click += (_, _) => ShowMoreMenu();

            SetHasOptionsMenu(true);

            Client.OnClientIsTalkingChanged += OnClientTalkingChanged;
            // Book наполняется нотификациями уже после статуса Connected — пересобираем дерево по событию.
            Client.OnBookChanged += OnBookChanged;
            Client.OnChatMessage += OnChatMessage;
            Client.OnChatCleared += OnChatCleared;
            Client.OnChatReloaded += OnChatReloaded;

            // §7.1 — авто-сворачивание карточки канала при открытии клавиатуры.
            _rootView.ViewTreeObserver!.GlobalLayout += OnGlobalLayout;

            ShowTab(channels: true);
            _ = RefreshTree();

            return view;
        }

        public override void OnResume() {
            base.OnResume();
            Activity!.Title = _title;

            // Режим активации мог измениться в «Настройках» — перечитываем каждый раз.
            _pushToTalk = AppSettings.GetActivationMode(Activity!) == ActivationMode.PushToTalk;
            UpdatePrimaryButton();

            // §2 — иконка сервера в ActionBar.
            Host.ShowServerActionBarIcon();
        }

        public override void OnPause() {
            base.OnPause();
            // Уходим с экрана с зажатой кнопкой — ACTION_UP может не прийти, снимаем передачу.
            Client.SetPushToTalk(false);
            _btnPrimary.Pressed = false;
            Host.HideServerActionBarIcon();
        }

        public override void OnDestroyView() {
            base.OnDestroyView();

            Client.OnClientIsTalkingChanged -= OnClientTalkingChanged;
            Client.OnBookChanged -= OnBookChanged;
            Client.OnChatMessage -= OnChatMessage;
            Client.OnChatCleared -= OnChatCleared;
            Client.OnChatReloaded -= OnChatReloaded;

            if (_rootView.ViewTreeObserver != null)
                _rootView.ViewTreeObserver.GlobalLayout -= OnGlobalLayout;
        }

        public override void OnCreateOptionsMenu(IMenu? menu, MenuInflater? inflater) {
            inflater!.Inflate(Resource.Menu.menu_server, menu);
        }

        public override bool OnOptionsItemSelected(IMenuItem item) {
            if (item.ItemId == Resource.Id.action_disconnect) {
                Host.DisconnectCurrent();
                return true;
            }

            return base.OnOptionsItemSelected(item);
        }

        // ===================== Нижняя панель =====================

        private void UpdatePrimaryButton() {
            _btnPrimary.Text = _pushToTalk
                ? "Говорить"
                : (Client.IsMicMuted ? "Микрофон: выкл" : "Микрофон: вкл");
        }

        // Выпадающий список: AFK и звук (а в режиме «по кнопке» — ещё и микрофон).
        private void ShowMoreMenu() {
            var popup = new PopupMenu(Activity!, _btnMore);
            const int idMic = 1, idAfk = 2, idSound = 3;

            if (_pushToTalk)
                popup.Menu!.Add(0, idMic, 0, Client.IsMicMuted ? "Включить микрофон" : "Выключить микрофон");
            popup.Menu!.Add(0, idAfk, 1, Client.IsAway ? "Вернуться" : "Отойти");
            popup.Menu!.Add(0, idSound, 2, Client.IsSoundMuted ? "Включить звук" : "Выключить звук");

            popup.MenuItemClick += (_, e) => {
                switch (e.Item!.ItemId) {
                    case idMic:
                        Client.SetMicMuted(!Client.IsMicMuted);
                        UpdatePrimaryButton();
                        break;
                    case idAfk:
                        OnAfkClicked();
                        break;
                    case idSound:
                        Client.SetSoundMuted(!Client.IsSoundMuted);
                        break;
                }
            };
            popup.Show();
        }

        // ===================== Переключение Каналы / Чат =====================

        private void ShowTab(bool channels) {
            _showingChannels = channels;
            _recycler.Visibility = channels ? ViewStates.Visible : ViewStates.Gone;
            _chatContainer.Visibility = channels ? ViewStates.Gone : ViewStates.Visible;
            // Иконка «сменяющаяся»: на дереве — чат (тап ведёт в чат), на чате — каналы.
            _btnViewToggle.SetImageResource(channels ? Resource.Drawable.ic_chat : Resource.Drawable.ic_channels);
            if (!channels)
                ScrollChatToEnd();
        }

        // ===================== Дерево каналов =====================

        private void OnBookChanged() => _ = RefreshTree();

        private async Task RefreshTree() {
            var (serverName, channels, clients, ownChannel) = await Client.GetBookSnapshot();

            Activity?.RunOnUiThread(() => {
                if (!string.IsNullOrEmpty(serverName)) {
                    _title = serverName;
                    Activity!.Title = serverName;
                    Host.UpdateConnectedTitle(serverName);
                }

                _items.Clear();
                foreach (var channel in channels.OrderBy(c => c.Order.Value)) {
                    var members = clients.Where(c => c.Channel.Equals(channel.Id)).ToList();
                    bool collapsed = _collapsed.TryGetValue(channel.Id, out var col) && col;
                    _items.Add(new ChannelCardItem(channel, members, collapsed));
                }
                _adapter.NotifyDataSetChanged();

                UpdateChatChannelCard(channels, clients, ownChannel);
            });
        }

        private void OnToggleCollapse(ChannelCardItem item) {
            item.Collapsed = !item.Collapsed;
            _collapsed[item.Channel.Id] = item.Collapsed;
            var index = _items.IndexOf(item);
            if (index >= 0)
                _adapter.NotifyItemChanged(index);
        }

        private void OnClientTalkingChanged(TalkingTrackerPipe.ClientVoiceStatus status) {
            Activity?.RunOnUiThread(() => {
                _adapter.RecolorClient(status.Id);
                RecolorChatCard(status.Id);
            });
        }

        // ===================== Переход между каналами =====================

        private void OnChannelClicked(ChannelCardItem item) {
            var channel = item.Channel;

            if (channel.HasPassword == true) {
                var input = new EditText(Activity) {
                    InputType = InputTypes.ClassText | InputTypes.TextVariationPassword,
                    Hint = "Пароль канала",
                };
                new AlertDialog.Builder(Activity)
                    .SetTitle($"Канал «{channel.Name}»")!
                    .SetView(input)!
                    .SetPositiveButton("Войти", (EventHandler<DialogClickEventArgs>)((_, _) =>
                        _ = Client.MoveToChannel(channel.Id, input.Text)))!
                    .SetNegativeButton("Отмена", (EventHandler<DialogClickEventArgs>?)null)!
                    .Show();
            }
            else {
                _ = Client.MoveToChannel(channel.Id, null);
            }
        }

        // ===================== Громкость / мьют собеседника =====================

        private void OnClientClicked(BookClient client) {
            var id = client.Id;

            var dialogView = LayoutInflater.From(Activity)!.Inflate(Resource.Layout.dialog_client_volume, null)!;
            var seek = dialogView.FindViewById<SeekBar>(Resource.Id.seekVolume)!;
            var mute = dialogView.FindViewById<CheckBox>(Resource.Id.chkMute)!;

            seek.Max = 100;
            seek.Progress = (int)(Client.GetClientVolume(id) * 100);
            mute.Checked = Client.IsClientMuted(id);

            seek.ProgressChanged += (_, e) => {
                if (e.FromUser)
                    Client.SetClientVolume(id, e.Progress / 100f);
            };
            mute.CheckedChange += (_, e) => Client.SetClientMuted(id, e.IsChecked);

            new AlertDialog.Builder(Activity)
                .SetTitle(client.Name)!
                .SetView(dialogView)!
                .SetPositiveButton("Готово", (EventHandler<DialogClickEventArgs>?)null)!
                .Show();
        }

        // ===================== AFK =====================

        private void OnAfkClicked() {
            if (Client.IsAway) {
                Client.SetAway(false, null);
                return;
            }

            var input = new EditText(Activity) { Hint = "Сообщение (необязательно)" };
            new AlertDialog.Builder(Activity)
                .SetTitle("Отойти (AFK)")!
                .SetView(input)!
                .SetPositiveButton("Отойти", (EventHandler<DialogClickEventArgs>)((_, _) =>
                    Client.SetAway(true, input.Text)))!
                .SetNegativeButton("Отмена", (EventHandler<DialogClickEventArgs>?)null)!
                .Show();
        }

        // ===================== Карточка канала на экране чата (§7) =====================

        private void BuildChatCard() {
            var ctx = Activity!;
            int half = Resources!.DisplayMetrics!.HeightPixels / 2;

            _chatCard = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
            var cardLp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            cardLp.SetMargins(Dp(8), Dp(6), Dp(8), Dp(6));
            _chatCard.LayoutParameters = cardLp;
            _chatCard.SetBackgroundResource(Resource.Drawable.bg_card);
            _chatCard.SetPadding(Dp(4), Dp(4), Dp(4), Dp(4));

            var header = new LinearLayout(ctx) { Orientation = Orientation.Horizontal };
            header.SetGravity(GravityFlags.CenterVertical);

            _chatCardName = new TextView(ctx) { TextSize = 16 };
            _chatCardName.SetTypeface(null, TypefaceStyle.Bold);
            _chatCardName.SetTextColor(Color.ParseColor("#202020"));
            _chatCardName.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
            _chatCardName.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
            header.AddView(_chatCardName);

            _chatCardExpand = new ImageButton(ctx);
            _chatCardExpand.LayoutParameters = new LinearLayout.LayoutParams(Dp(44), Dp(44));
            _chatCardExpand.SetImageResource(Resource.Drawable.ic_expand);
            var tv = new Android.Util.TypedValue();
            ctx.Theme!.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackgroundBorderless, tv, true);
            _chatCardExpand.SetBackgroundResource(tv.ResourceId);
            header.AddView(_chatCardExpand);
            _chatCard.AddView(header);

            _chatCardScroll = new MaxHeightScrollView(ctx) { MaxHeightPx = half };
            _chatCardScroll.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            _chatCardMembersContainer = new LinearLayout(ctx) { Orientation = Orientation.Vertical };
            _chatCardMembersContainer.LayoutParameters = new ViewGroup.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            _chatCardScroll.AddView(_chatCardMembersContainer);
            _chatCard.AddView(_chatCardScroll);

            void Toggle() {
                _chatCardCollapsed = !_chatCardCollapsed;
                ApplyChatCardCollapsed();
            }
            _chatCardExpand.Click += (_, _) => Toggle();
            _chatCardName.Click += (_, _) => Toggle();

            _chatCardHolder.AddView(_chatCard);
        }

        private void ApplyChatCardCollapsed() {
            if (_chatCardScroll == null || _chatCardExpand == null)
                return;
            _chatCardScroll.Visibility = _chatCardCollapsed ? ViewStates.Gone : ViewStates.Visible;
            _chatCardExpand.Rotation = _chatCardCollapsed ? 0f : 180f;
        }

        private void UpdateChatChannelCard(BookChannel[] channels, BookClient[] clients, ChannelId? ownChannel) {
            if (_chatCard == null)
                return;

            BookChannel? current = ownChannel is ChannelId own
                ? channels.FirstOrDefault(c => c.Id.Equals(own))
                : null;

            if (current == null) {
                _chatCard.Visibility = ViewStates.Gone;
                return;
            }

            _chatCard.Visibility = ViewStates.Visible;
            _chatCardName!.Text = current.Name;

            _chatCardMemberList = clients.Where(c => c.Channel.Equals(current.Id)).ToList();
            _chatCardMembersContainer!.RemoveAllViews();
            var inflater = LayoutInflater.From(Activity)!;
            foreach (var member in _chatCardMemberList) {
                var row = inflater.Inflate(Resource.Layout.item_client, _chatCardMembersContainer, false)!;
                BindClientRow(row, member);
                var captured = member;
                row.Click += (_, _) => OnClientClicked(captured);
                _chatCardMembersContainer.AddView(row);
            }

            _chatCardExpand!.Visibility = _chatCardMemberList.Count > 0 ? ViewStates.Visible : ViewStates.Gone;
            ApplyChatCardCollapsed();
        }

        // Перекраска строк участников карточки канала без переинфляции (частые смены «говорит»).
        private void RecolorChatCard(ClientId id) {
            if (_chatCardMembersContainer == null || !_chatCardMemberList.Any(m => m.Id.Equals(id)))
                return;
            int count = Math.Min(_chatCardMembersContainer.ChildCount, _chatCardMemberList.Count);
            for (int j = 0; j < count; j++) {
                var child = _chatCardMembersContainer.GetChildAt(j);
                if (child != null)
                    BindClientRow(child, _chatCardMemberList[j]);
            }
        }

        // §7.1 — при открытии клавиатуры карточку канала сворачиваем (освобождаем место под чат).
        private void OnGlobalLayout(object? sender, EventArgs e) {
            var rect = new Rect();
            _rootView.GetWindowVisibleDisplayFrame(rect);
            int screenHeight = _rootView.RootView?.Height ?? 0;
            if (screenHeight == 0)
                return;
            bool keyboardOpen = (screenHeight - rect.Bottom) > screenHeight * 0.15;
            if (keyboardOpen && !_showingChannels && !_chatCardCollapsed) {
                _chatCardCollapsed = true;
                ApplyChatCardCollapsed();
            }
        }

        // ===================== Чат =====================

        private void SendChat() {
            var text = _chatInput.Text?.Trim();
            if (string.IsNullOrEmpty(text))
                return;
            Client.SendChannelChat(text);
            _chatInput.Text = "";
        }

        private void OnChatMessage(Client.ChatMessage msg) {
            Activity?.RunOnUiThread(() => {
                _chatMessages.Add(msg);
                _chatAdapter.NotifyItemInserted(_chatMessages.Count - 1);
                ScrollChatToEnd();
            });
        }

        private void OnChatCleared() {
            Activity?.RunOnUiThread(() => {
                _chatMessages.Clear();
                _chatAdapter.NotifyDataSetChanged();
            });
        }

        // История перезагружена (смена канала) — перечитываем целиком.
        private void OnChatReloaded() {
            Activity?.RunOnUiThread(() => {
                _chatMessages.Clear();
                _chatMessages.AddRange(Client.GetChatHistory());
                _chatAdapter.NotifyDataSetChanged();
                ScrollChatToEnd();
            });
        }

        private void ScrollChatToEnd() {
            if (_chatMessages.Count > 0)
                _chatRecycler.ScrollToPosition(_chatMessages.Count - 1);
        }

        private int Dp(int value) => (int)(value * Resources!.DisplayMetrics!.Density);
    }
}
