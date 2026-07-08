using MobileTS.Audio;
using TSLib.Audio;
using TSLib.Audio.Pipes;
using TSLib.Full;
using TSLib.Helper;
using TSLib.Shared;

namespace MobileTS {
    internal static partial class Client {
        // Весь аудиоконвейер клиента, вынесенный из Client. Владеет всеми сегментами и их
        // жизненным циклом, а наружу даёт простое управление: режим активации, порог, push-to-talk,
        // заглушки микрофона и звука.
        //
        // Захват:        AudioRecord → тайминг → [mute mic] → [VAD] → [PTT] → энкодер → клиент
        // Воспроизведение: клиент → декодер → трекер «кто говорит» → [mute sound] → AudioTrack
        internal sealed class Audio {
            // Захват
            private AudioRecordPipe? recordPipe;
            private PreciseTimedPipe? timedPipe;
            private MutePipe? micMutePipe;
            private VoiceActivationPipe? voicePipe;
            private PushToTalkPipe? pushToTalkPipe;
            private EncoderPipe? encoderPipe;
            // Воспроизведение
            private TalkingTrackerPipe? trackerPipe;
            private MutePipe? soundMutePipe;
            private AudioTrackPipe? trackPipe;

            // Пробрасываем «кто говорит» наружу (для подсветки клиентов в ServerFragment).
            public event Action<TalkingTrackerPipe.ClientVoiceStatus>? OnClientIsTalkingChanged;

            // Срабатывает, когда МОЙ микрофон начинает/прекращает передавать (VAD открыт либо
            // зажата кнопка PTT, и при этом микрофон не заглушён) — для подсветки своего пользователя.
            public event Action<bool>? OnLocalTalkingChanged;

            private ActivationMode mode;
            private bool localTalking;

            public bool MicMuted => micMutePipe?.Muted ?? false;
            public bool SoundMuted => soundMutePipe?.Muted ?? false;

            public void Build(TsFullClient client, ActivationMode mode, float threshold, int deactivationDelayMs) {
                // Захват: микрофон → тайминг → mute → VAD → PTT → энкодер → клиент.
                recordPipe = new AudioRecordPipe();
                timedPipe = recordPipe.Into(new PreciseTimedPipe(new SampleInfo(SampleRate, 1, 16), Id.Null));
                micMutePipe = timedPipe.Chain(new MutePipe());
                voicePipe = micMutePipe.Chain(new VoiceActivationPipe { Threshold = threshold, DeactivationDelayMs = deactivationDelayMs });
                pushToTalkPipe = voicePipe.Chain(new PushToTalkPipe());
                encoderPipe = pushToTalkPipe.Chain(new EncoderPipe(Codec.OpusVoice));
                encoderPipe.Chain(client);

                // VAD сообщает о смене состояния «открыто» — пересчитываем подсветку своего микрофона.
                voicePipe.OnOpenChanged += _ => UpdateLocalTalking();

                // Воспроизведение: клиент → декодер → трекер → mute → динамик.
                DecoderPipe decoderPipe = client.Chain(new DecoderPipe());
                trackerPipe = decoderPipe.Chain(new TalkingTrackerPipe());
                soundMutePipe = trackerPipe.Chain(new MutePipe());
                trackPipe = soundMutePipe.Chain(new AudioTrackPipe());

                trackerPipe.OnClientIsTalkingChanged += status => OnClientIsTalkingChanged?.Invoke(status);

                SetActivationMode(mode);

                timedPipe.ReadBufferSize = FrameSize * 2;
                timedPipe.Paused = false;
            }

            // Ровно один активационный пайп «активен» (фильтрует), второй — прозрачен (пропускает).
            public void SetActivationMode(ActivationMode mode) {
                this.mode = mode;
                if (voicePipe != null)
                    voicePipe.Enabled = mode == ActivationMode.Voice;
                if (pushToTalkPipe != null)
                    pushToTalkPipe.Enabled = mode == ActivationMode.PushToTalk;
                UpdateLocalTalking();
            }

            public void SetVoiceThreshold(float threshold) {
                if (voicePipe != null)
                    voicePipe.Threshold = threshold;
            }

            public void SetVoiceDeactivationDelay(int delayMs) {
                if (voicePipe != null)
                    voicePipe.DeactivationDelayMs = delayMs;
            }

            public void SetPushToTalk(bool talking) {
                if (pushToTalkPipe != null)
                    pushToTalkPipe.Talking = talking;
                UpdateLocalTalking();
            }

            public void SetMicMuted(bool muted) {
                if (micMutePipe != null)
                    micMutePipe.Muted = muted;
                UpdateLocalTalking();
            }

            // Свой микрофон «активен», если он не заглушён и текущий режим разрешает передачу:
            // по голосу — пока VAD открыт; по кнопке — пока кнопка зажата.
            private void UpdateLocalTalking() {
                bool talking = !MicMuted && (mode == ActivationMode.Voice
                    ? (voicePipe?.IsOpen ?? false)
                    : (pushToTalkPipe?.Talking ?? false));

                if (talking == localTalking)
                    return;
                localTalking = talking;
                OnLocalTalkingChanged?.Invoke(talking);
            }

            public void SetSoundMuted(bool muted) {
                if (soundMutePipe != null)
                    soundMutePipe.Muted = muted;
            }

            public void MuteClient(ClientId id, bool muted) => soundMutePipe?.MuteClient(id, muted);
            public bool IsClientMuted(ClientId id) => soundMutePipe?.IsClientMuted(id) ?? false;

            // Громкость конкретного собеседника — нативный AudioTrack на его ClientId.
            public void SetClientVolume(ClientId id, float volume) => trackPipe?.SetClientVolume(id, volume);
            public float GetClientVolume(ClientId id) => trackPipe?.GetClientVolume(id) ?? 1f;

            // Останавливаем захват и отпускаем микрофон ДО (возможно долгого) дисконнекта клиента.
            public void StopCapture() {
                timedPipe?.Dispose();
                recordPipe?.Dispose();
            }

            // Освобождаем воспроизведение ПОСЛЕ дисконнекта — клиент мог дописывать звук в конвейер.
            public void DisposePlayback() {
                trackPipe?.Dispose();
                encoderPipe?.Dispose();
            }
        }
    }
}
