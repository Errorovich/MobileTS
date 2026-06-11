using System;
using TSLib.Audio;

namespace MobileTS.Audio {
    // Активация передачи по голосу (VAD). Когда Enabled=false — пропускает звук прозрачно
    // (выбран другой режим). Когда Enabled=true — пропускает только пока громкость выше порога,
    // плюс настраиваемая задержка деактивации после падения громкости (чтобы не обрезать концы
    // слов). IsOpen отражает, идёт ли сейчас передача — по нему подсвечивается свой пользователь.
    public sealed class VoiceActivationPipe : IAudioPipe {
        public bool Active => OutStream?.Active ?? false;
        public IAudioPassiveConsumer? OutStream { get; set; }

        private bool enabled;
        public bool Enabled {
            get => enabled;
            set {
                enabled = value;
                if (!value)
                    SetOpen(false); // прозрачный режим — состояние «открыто» не отслеживаем
            }
        }

        // Порог срабатывания 0..1 (сопоставим с AudioLevel.Peak).
        public float Threshold { get; set; } = 0.05f;

        // Задержка деактивации в мс: сколько держать «открытым» после падения громкости ниже порога.
        public int DeactivationDelayMs { get; set; } = 300;

        public bool IsOpen { get; private set; }
        public event Action<bool>? OnOpenChanged;

        private long openUntil;

        public void Write(Span<byte> data, Meta? meta) {
            if (OutStream is null)
                return;

            if (!enabled) {
                OutStream.Write(data, meta);
                return;
            }

            long now = Environment.TickCount64;
            if (AudioLevel.Peak(data) >= Threshold)
                openUntil = now + DeactivationDelayMs;

            bool open = now <= openUntil;
            SetOpen(open);

            if (open)
                OutStream.Write(data, meta);
        }

        private void SetOpen(bool value) {
            if (IsOpen == value)
                return;
            IsOpen = value;
            OnOpenChanged?.Invoke(value);
        }
    }
}
