using Android.Content;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using MobileTS.Logging;

namespace MobileTS.Activity.Log {
    // Раздел «Журнал»: живой лог приложения и библиотеки, плюс кнопки для работы с файлами логов.
    public class LogFragment : Android.App.Fragment {
        private RecyclerView _recycler = null!;
        private LogEntryAdapter _adapter = null!;
        private readonly List<LogEntry> _items = new();

        public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) {
            var view = inflater.Inflate(Resource.Layout.fragment_log, container, false)!;

            _recycler = view.FindViewById<RecyclerView>(Resource.Id.logRecycler)!;
            // StackFromEnd — новые записи внизу, список «прилипает» к низу как в терминале.
            _recycler.SetLayoutManager(new LinearLayoutManager(Activity) { StackFromEnd = true });

            _items.Clear();
            _items.AddRange(AppLog.Snapshot());
            _adapter = new LogEntryAdapter(_items);
            _recycler.SetAdapter(_adapter);
            ScrollToBottom();

            view.FindViewById<Button>(Resource.Id.btnLogFiles)!.Click += (_, _) => ShowLogFiles();
            view.FindViewById<Button>(Resource.Id.btnShareCurrent)!.Click += (_, _) => ShareCurrent();
            view.FindViewById<Button>(Resource.Id.btnClearLog)!.Click += (_, _) => AppLog.Clear();

            return view;
        }

        public override void OnResume() {
            base.OnResume();
            Activity!.Title = "Журнал";
            // Перечитываем снимок: пока экран был закрыт, записи копились в буфере.
            ReloadFromBuffer();
            AppLog.OnEntry += OnEntry;
            AppLog.OnCleared += OnCleared;
        }

        public override void OnPause() {
            base.OnPause();
            AppLog.OnEntry -= OnEntry;
            AppLog.OnCleared -= OnCleared;
        }

        private void OnEntry(LogEntry entry) {
            Activity?.RunOnUiThread(() => {
                _items.Add(entry);
                _adapter.NotifyItemInserted(_items.Count - 1);
                ScrollToBottom();
            });
        }

        private void OnCleared() => Activity?.RunOnUiThread(ReloadFromBuffer);

        private void ReloadFromBuffer() {
            _items.Clear();
            _items.AddRange(AppLog.Snapshot());
            _adapter.NotifyDataSetChanged();
            ScrollToBottom();
        }

        private void ScrollToBottom() {
            if (_items.Count > 0)
                _recycler.ScrollToPosition(_items.Count - 1);
        }

        // Системная «шторка»: список всех файлов логов/крашрепортов, тап — поделиться выбранным.
        private void ShowLogFiles() {
            var files = AppLog.GetLogFiles();
            if (files.Length == 0) {
                Toast.MakeText(Activity, "Нет файлов логов", ToastLength.Short)?.Show();
                return;
            }

            var names = files
                .Select(f => f.Name + "  (" + Math.Max(1, f.Length / 1024) + " КБ)")
                .ToArray();

            new AlertDialog.Builder(Activity!)
                .SetTitle("Файлы логов")!
                .SetItems(names, (_, e) => Share(files[e.Which].FullName))!
                .SetNegativeButton("Закрыть", (EventHandler<DialogClickEventArgs>?)null)!
                .Show();
        }

        private void ShareCurrent() {
            var path = AppLog.CurrentFilePath;
            if (path == null) {
                Toast.MakeText(Activity, "Текущий файл лога недоступен", ToastLength.Short)?.Show();
                return;
            }
            Share(path);
        }

        private void Share(string path) {
            try {
                LogShare.ShareFile(Activity!, path);
            }
            catch (Exception ex) {
                AppLog.W("Log", "Не удалось поделиться файлом", ex);
                Toast.MakeText(Activity, "Не удалось поделиться: " + ex.Message, ToastLength.Long)?.Show();
            }
        }
    }
}
