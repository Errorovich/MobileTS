using Android.Content;
using Android.Graphics;
using Android.Util;
using Android.Views;

namespace MobileTS.Activity.Settings {
    // Полоска громкости в стиле Discord: заполнение по текущей громкости микрофона (Level),
    // вертикальная метка выбранного порога (Threshold) и состояние «передача идёт» (IsActive,
    // приходит из самого VAD — поэтому отражает и задержку деактивации: остаётся «активной» ещё
    // некоторое время после падения громкости ниже порога). Создаётся из кода и кладётся в контейнер.
    public sealed class VolumeMeterView : View {
        private readonly Paint _bgPaint = new();
        private readonly Paint _fillPaint = new();
        private readonly Paint _thresholdPaint = new() { Color = Color.ParseColor("#C2185B"), StrokeWidth = 5 };

        private static readonly Color BgIdle = Color.ParseColor("#E0E0E0");
        private static readonly Color BgActive = Color.ParseColor("#C8E6C9");   // фон при активной передаче
        private static readonly Color FillIdle = Color.ParseColor("#90A4AE");   // громкость без передачи
        private static readonly Color FillActive = Color.ParseColor("#43A047"); // громкость при передаче

        private float _level;     // 0..1
        private float _threshold; // 0..1
        private bool _active;

        public VolumeMeterView(Context context) : base(context) { }
        public VolumeMeterView(Context context, IAttributeSet? attrs) : base(context, attrs) { }

        public float Level {
            get => _level;
            set { _level = Clamp01(value); PostInvalidate(); }
        }

        public float Threshold {
            get => _threshold;
            set { _threshold = Clamp01(value); PostInvalidate(); }
        }

        // Идёт ли сейчас передача (VAD «открыт»). Фон/заполнение зеленеют, пока активно.
        public bool IsActive {
            get => _active;
            set { _active = value; PostInvalidate(); }
        }

        protected override void OnDraw(Canvas canvas) {
            base.OnDraw(canvas);

            float w = Width;
            float h = Height;

            _bgPaint.Color = _active ? BgActive : BgIdle;
            canvas.DrawRect(0, 0, w, h, _bgPaint);

            _fillPaint.Color = _active ? FillActive : FillIdle;
            canvas.DrawRect(0, 0, w * _level, h, _fillPaint);

            float x = w * _threshold;
            canvas.DrawLine(x, 0, x, h, _thresholdPaint);
        }

        private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
    }
}
