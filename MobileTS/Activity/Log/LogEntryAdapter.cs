using Android.Graphics;
using Android.Util;
using Android.Views;
using AndroidX.RecyclerView.Widget;
using MobileTS.Logging;
using TSLib.Logging;

namespace MobileTS.Activity.Log {
    // Простой адаптер журнала: одна моноширинная строка на запись, цвет — по уровню.
    public class LogEntryAdapter : RecyclerView.Adapter {
        private readonly List<LogEntry> _items;

        public LogEntryAdapter(List<LogEntry> items) => _items = items;

        public override int ItemCount => _items.Count;

        public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType) {
            var tv = new TextView(parent.Context) {
                LayoutParameters = new RecyclerView.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent),
                Typeface = Typeface.Monospace,
            };
            tv.SetTextSize(ComplexUnitType.Sp, 12);
            tv.SetPadding(0, 2, 0, 2);
            return new VH(tv);
        }

        public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position) {
            var vh = (VH)holder;
            var entry = _items[position];
            vh.Text.Text = entry.Line;
            vh.Text.SetTextColor(ColorFor(entry.Level));
        }

        private static Color ColorFor(LogLevel level) => level switch {
            LogLevel.Trace => Color.ParseColor("#888888"),
            LogLevel.Debug => Color.ParseColor("#BBBBBB"),
            LogLevel.Info => Color.ParseColor("#E0E0E0"),
            LogLevel.Warn => Color.ParseColor("#E0A030"),
            LogLevel.Error => Color.ParseColor("#E05050"),
            _ => Color.ParseColor("#E0E0E0"),
        };

        private sealed class VH : RecyclerView.ViewHolder {
            public readonly TextView Text;
            public VH(TextView view) : base(view) => Text = view;
        }
    }
}
