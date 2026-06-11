using System;
using System.Collections.Generic;
using TSLib;
using TSLib.Audio;

namespace MobileTS.Audio {
    // Заглушка звука в конвейере. Поддерживает глобальное отключение (Muted) и отключение
    // отдельных клиентов по ClientId (нужно для playback — мьют конкретного собеседника, понадобится
    // позже). Когда заглушено — данные дальше по конвейеру не идут.
    public sealed class MutePipe : IAudioPipe {
        public bool Active => OutStream?.Active ?? false;
        public IAudioPassiveConsumer? OutStream { get; set; }

        public bool Muted { get; set; }

        private readonly HashSet<ClientId> mutedClients = new();
        private readonly object sync = new();

        public void MuteClient(ClientId id, bool muted) {
            lock (sync) {
                if (muted)
                    mutedClients.Add(id);
                else
                    mutedClients.Remove(id);
            }
        }

        public bool IsClientMuted(ClientId id) {
            lock (sync)
                return mutedClients.Contains(id);
        }

        public void Write(Span<byte> data, Meta? meta) {
            if (OutStream is null)
                return;

            if (Muted)
                return;

            if (meta != null) {
                lock (sync) {
                    if (mutedClients.Count != 0 && mutedClients.Contains(meta.In.Sender))
                        return;
                }
            }

            OutStream.Write(data, meta);
        }
    }
}
