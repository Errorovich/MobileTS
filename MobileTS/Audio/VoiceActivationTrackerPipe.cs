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
            if (!isTalking.TryGetValue(sender, out bool lastActive)) {
                isTalking.Add(sender, active);
            }
            else if (lastActive != active)
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
