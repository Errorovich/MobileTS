using System;
using TSLib.Audio;

namespace MobileTS.Audio {
    // Активация передачи по кнопке (push-to-talk). Когда Enabled=false — пропускает звук прозрачно
    // (выбран другой режим). Когда Enabled=true — пропускает только пока кнопка зажата (Talking).
    public sealed class PushToTalkPipe : IAudioPipe {
        public bool Active => OutStream?.Active ?? false;
        public IAudioPassiveConsumer? OutStream { get; set; }

        public bool Enabled { get; set; }
        public bool Talking { get; set; }

        public void Write(Span<byte> data, Meta? meta) {
            if (OutStream is null)
                return;

            if (Enabled && !Talking)
                return;

            OutStream.Write(data, meta);
        }
    }
}
