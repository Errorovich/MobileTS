using Android.Content;
using Android.Text.Method;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;

namespace MobileTS.Activity.ServersList {
    public partial class ServersFragment {
        private class ServerAdapter : RecyclerView.Adapter {
            private readonly List<ServerInfo> _items;
            private readonly ServersFragment _fragment;

            public ServerAdapter(List<ServerInfo> items, ServersFragment fragment) {
                _items = items;
                _fragment = fragment;
            }

            public override int ItemCount => _items.Count;

            public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) {
                var view = LayoutInflater.From(parent.Context)!
                    .Inflate(Resource.Layout.item_server, parent, false)!;
                var vh = new ServerViewHolder(view);

                // Обработчики навешиваем один раз на холдер; актуальный элемент берём по позиции
                // во время клика, иначе при переиспользовании холдера подписки бы копились.

                // Тап по названию — подключение к серверу (как раньше тап по строке).
                vh.Title.Click += (_, _) => {
                    var server = ItemAt(vh);
                    if (server != null)
                        _fragment.ConnectToServer(server);
                };

                // Треугольник — раскрытие/сворачивание карточки.
                vh.Expand.Click += (_, _) => {
                    var pos = vh.BindingAdapterPosition;
                    if (pos == RecyclerView.NoPosition)
                        return;
                    var server = _items[pos];
                    server.IsExpanded = !server.IsExpanded;
                    if (!server.IsExpanded)
                        server.IsEditing = false; // свернули — выходим из режима правки
                    NotifyItemChanged(pos);
                };

                vh.ToggleServerPassword.Click += (_, _) => TogglePassword(vh.ServerPassword, vh.ToggleServerPassword);
                vh.ToggleChannelPassword.Click += (_, _) => TogglePassword(vh.ChannelPassword, vh.ToggleChannelPassword);

                // Карандаш ⇄ галочка: вход в правку и сохранение введённых данных.
                vh.Edit.Click += (_, _) => {
                    var pos = vh.BindingAdapterPosition;
                    if (pos == RecyclerView.NoPosition)
                        return;
                    var server = _items[pos];

                    if (!server.IsEditing) {
                        server.IsEditing = true;
                        NotifyItemChanged(pos);
                        return;
                    }

                    // Адрес обязателен — без него сервер бесполезен; остаёмся в режиме правки.
                    if (string.IsNullOrWhiteSpace(vh.Address.Text)) {
                        Toast.MakeText(_fragment.Activity, "Укажите адрес сервера", ToastLength.Short)?.Show();
                        return;
                    }

                    // Пароли храним в открытом виде (как и identity) — ради переносимости при бэкапе.
                    server.Alias = NullIfEmpty(vh.Alias.Text);
                    server.Address = vh.Address.Text ?? "";
                    server.Nickname = NullIfEmpty(vh.Nickname.Text);
                    server.ServerPassword = NullIfEmpty(vh.ServerPassword.Text);
                    server.DefaultChannel = NullIfEmpty(vh.Channel.Text);
                    server.DefaultChannelPassword = NullIfEmpty(vh.ChannelPassword.Text);
                    server.IsEditing = false;

                    _fragment.SaveServers();
                    NotifyItemChanged(pos);
                };

                // Корзина — удаление с подтверждением.
                vh.Delete.Click += (_, _) => {
                    var server = ItemAt(vh);
                    if (server == null)
                        return;

                    new AlertDialog.Builder(_fragment.Activity)
                        .SetTitle("Удалить сервер")!
                        .SetMessage($"Удалить «{DisplayTitle(server)}»?")!
                        .SetPositiveButton("Удалить", (EventHandler<DialogClickEventArgs>)((_, _) => {
                            var pos = vh.BindingAdapterPosition;
                            if (pos == RecyclerView.NoPosition)
                                return;
                            _items.RemoveAt(pos);
                            NotifyItemRemoved(pos);
                            _fragment.SaveServers();
                        }))!
                        .SetNegativeButton("Отмена", (EventHandler<DialogClickEventArgs>?)null)!
                        .Show();
                };

                return vh;
            }

            public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) {
                var vh = (ServerViewHolder)holder;
                var server = _items[position];

                vh.Title.Text = DisplayTitle(server);
                vh.Expand.Rotation = server.IsExpanded ? 180f : 0f;
                vh.CardBody.Visibility = server.IsExpanded ? ViewStates.Visible : ViewStates.Gone;

                LoadServerIcon(vh, server);

                vh.Alias.Text = server.Alias;
                vh.Address.Text = server.Address;
                vh.Nickname.Text = server.Nickname;
                vh.ServerPassword.Text = server.ServerPassword;
                vh.Channel.Text = server.DefaultChannel;
                vh.ChannelPassword.Text = server.DefaultChannelPassword;

                // Пароли при привязке всегда замаскированы (иконка «глаз»).
                MaskPassword(vh.ServerPassword, vh.ToggleServerPassword);
                MaskPassword(vh.ChannelPassword, vh.ToggleChannelPassword);

                ApplyMode(vh, server);
            }

            // Иконка сервера из кэша (скачивается при подключении). Нет файла — прячем ImageView.
            private static void LoadServerIcon(ServerViewHolder vh, ServerInfo server) {
                var path = Client.GetServerIconPathIfExists(server.Address);
                var bitmap = path == null ? null : Android.Graphics.BitmapFactory.DecodeFile(path);
                if (bitmap != null) {
                    vh.Icon.SetImageBitmap(bitmap);
                    vh.Icon.Visibility = ViewStates.Visible;
                }
                else {
                    vh.Icon.SetImageBitmap(null);
                    vh.Icon.Visibility = ViewStates.Gone;
                }
            }

            // Просмотр vs правка: переключаем редактируемость полей, иконку и видимость строк.
            private static void ApplyMode(ServerViewHolder vh, ServerInfo server) {
                bool editing = server.IsEditing;

                SetEditable(vh.Alias, editing);
                SetEditable(vh.Address, editing);
                SetEditable(vh.Nickname, editing);
                SetEditable(vh.ServerPassword, editing);
                SetEditable(vh.Channel, editing);
                SetEditable(vh.ChannelPassword, editing);

                vh.Edit.SetImageResource(editing ? Resource.Drawable.ic_check : Resource.Drawable.ic_edit);
                vh.Edit.ContentDescription = editing ? "Сохранить" : "Редактировать";

                // В режиме просмотра незаполненные поля скрыты; в режиме правки видны все.
                SetRowVisible(vh.RowAlias, editing || !IsEmpty(server.Alias));
                SetRowVisible(vh.RowAddress, editing || !IsEmpty(server.Address));
                SetRowVisible(vh.RowNickname, editing || !IsEmpty(server.Nickname));
                SetRowVisible(vh.RowServerPassword, editing || !IsEmpty(server.ServerPassword));
                SetRowVisible(vh.RowChannel, editing || !IsEmpty(server.DefaultChannel));
                SetRowVisible(vh.RowChannelPassword, editing || !IsEmpty(server.DefaultChannelPassword));
            }

            private static void SetEditable(EditText edit, bool editable) {
                edit.Enabled = editable;
                edit.Focusable = editable;
                edit.FocusableInTouchMode = editable;
                edit.Clickable = editable;
                edit.LongClickable = editable;
            }

            private static void SetRowVisible(View row, bool visible) =>
                row.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;

            private static void MaskPassword(EditText edit, ImageButton toggle) {
                edit.TransformationMethod = PasswordTransformationMethod.Instance;
                toggle.SetImageResource(Resource.Drawable.ic_visibility);
            }

            // «Глаз»: показать/скрыть пароль (работает и в режиме просмотра, и в режиме правки).
            private static void TogglePassword(EditText edit, ImageButton toggle) {
                bool masked = edit.TransformationMethod != null;
                if (masked) {
                    edit.TransformationMethod = null;
                    toggle.SetImageResource(Resource.Drawable.ic_visibility_off);
                }
                else {
                    edit.TransformationMethod = PasswordTransformationMethod.Instance;
                    toggle.SetImageResource(Resource.Drawable.ic_visibility);
                }
                if (edit.IsFocused)
                    edit.SetSelection(edit.Text?.Length ?? 0);
            }

            // Пустая новая карточка ещё без адреса/псевдонима — показываем подпись-заглушку.
            private static string DisplayTitle(ServerInfo s) =>
                string.IsNullOrWhiteSpace(s.DisplayName) ? "Новый сервер" : s.DisplayName;

            private static bool IsEmpty(string? s) => string.IsNullOrWhiteSpace(s);
            private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

            private ServerInfo? ItemAt(RecyclerView.ViewHolder holder) {
                var pos = holder.BindingAdapterPosition;
                return pos == RecyclerView.NoPosition ? null : _items[pos];
            }
        }
    }
}
