using System;
using System.Collections.Generic;
using TSLib;
using TSLib.Audio;

namespace MobileTS.Audio
{
    internal class VoiceActivationTrackerPipe : IAudioPipe
    {
        public bool Active => OutStream?.Active ?? false;

        public IAudioPassiveConsumer? OutStream { get; set; }

        public event Action<ClientVoiceStatus>? OnClientIsTalkingChanged;

        private readonly Dictionary<ClientId, bool> isTalking = new();

        public void Write(Span<byte> data, Meta? meta)
        {
            if (meta is null)
                return;

            var sender = meta.In.Sender;
            bool active = data.Length != 0;
            // Неизвестного отправителя считаем «молчащим»: тогда первый же кадр от уже говорящего
            // клиента переведёт состояние false→true и поднимет событие сразу (раньше при первом
            // кадре делался только Add без вызова события, и клиент не подсвечивался до паузы/слова).
            if (!isTalking.TryGetValue(sender, out bool lastActive))
                lastActive = false;
            if (lastActive != active)
            {
                isTalking[sender] = active;
                OnClientIsTalkingChanged?.Invoke(new ClientVoiceStatus(sender, active));
            }

            OutStream?.Write(data, meta);
        }

        public class ClientVoiceStatus
        {
            public ClientId Id { get; set; }
            public bool Active { get; set; }

            public ClientVoiceStatus(ClientId id, bool active)
            {
                Id = id;
                Active = active;
            }
        }
    }
}
