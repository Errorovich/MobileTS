using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Java.Lang;
using TSLib;
using TSLib.Audio;

namespace MobileTS.Audio
{
    internal class VoiceActivationTrackerPipe : IAudioPipe
    {
        public bool Active => OutStream?.Active ?? false;

        public IAudioPassiveConsumer? OutStream { get; set; }

        public event Action<ClientVoiceStatus>? OnClientIsTalkingChanged;

        private Dictionary<ClientId, bool> isTalking = new();

        public void Write(Span<byte> data, Meta? meta)
        {
            bool active = data != Span<byte>.Empty;
            if (!isTalking.TryGetValue(meta.In.Sender, out bool lastActive)) {
                isTalking.Add(meta.In.Sender, active);
            }
            else if(lastActive != active)
            {
                isTalking[meta.In.Sender] = active;
                OnClientIsTalkingChanged?.Invoke(new ClientVoiceStatus(meta.In.Sender, active));
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
