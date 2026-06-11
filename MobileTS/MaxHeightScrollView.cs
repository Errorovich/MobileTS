using Android.Content;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace MobileTS {
    // ScrollView с ограничением высоты (у штатного ScrollView нет maxHeight). Нужен для карточки
    // текущего канала на экране чата — она не должна занимать больше половины экрана, а лишние
    // участники должны прокручиваться внутри.
    public class MaxHeightScrollView : ScrollView {
        // Максимальная высота в пикселях (0 — без ограничения).
        public int MaxHeightPx { get; set; }

        public MaxHeightScrollView(Context context) : base(context) { }
        public MaxHeightScrollView(Context context, IAttributeSet? attrs) : base(context, attrs) { }
        public MaxHeightScrollView(Context context, IAttributeSet? attrs, int defStyle) : base(context, attrs, defStyle) { }
        protected MaxHeightScrollView(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

        protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec) {
            if (MaxHeightPx > 0)
                heightMeasureSpec = MeasureSpec.MakeMeasureSpec(MaxHeightPx, MeasureSpecMode.AtMost);
            base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
        }
    }
}
