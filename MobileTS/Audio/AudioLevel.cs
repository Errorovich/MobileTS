using System;
using System.Runtime.InteropServices;

namespace MobileTS.Audio {
    // Измерение громкости PCM16-кадра. Один и тот же расчёт используют активация по голосу
    // (VoiceActivationPipe) и индикатор в настройках — чтобы порог совпадал с тем, что видно на полоске.
    public static class AudioLevel {
        // Пиковая амплитуда кадра, нормированная в диапазон 0..1.
        public static float Peak(ReadOnlySpan<byte> pcm16) {
            var samples = MemoryMarshal.Cast<byte, short>(pcm16);
            int peak = 0;
            for (int i = 0; i < samples.Length; i++) {
                int v = samples[i];
                v = v < 0 ? -v : v; // -32768 при инверсии останется 32767 после клампа ниже
                if (v > peak)
                    peak = v;
            }
            return Math.Min(peak / (float)short.MaxValue, 1f);
        }
    }
}
