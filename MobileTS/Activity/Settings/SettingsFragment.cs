using System;
using System.Threading;
using Android;
using Android.Content.PM;
using Android.Media;
using Android.Views;
using MobileTS.Audio;
using TSLib.Audio;
using TSLib.Logging;

namespace MobileTS.Activity.Settings {
    // Раздел «Настройки»: имя пользователя по умолчанию (см. AppSettings) и режим активации
    // микрофона (по голосу с порогом + тест громкости, либо по кнопке).
    public class SettingsFragment : Android.App.Fragment {
        private const int RecordAudioRequestCode = 2;

        private EditText? _txtUsername;
        private RadioGroup? _activationGroup;
        private RadioButton? _modeVoice;
        private RadioButton? _modePush;
        private View? _voiceSection;
        private SeekBar? _thresholdSeek;
        private SeekBar? _delaySeek;
        private EditText? _delayValue;
        private Button? _btnTest;
        private VolumeMeterView? _meter;
        private Spinner? _logLevelSpinner;

        // Тест громкости: собственный AudioRecord, не связанный с подключением. Захваченные кадры
        // прогоняем через настоящий VoiceActivationPipe — тогда тест в точности повторяет работу VAD,
        // включая задержку деактивации.
        private volatile bool _testing;
        private Thread? _testThread;
        private AudioRecord? _testRecord;
        private VoiceActivationPipe? _testPipe;

        public override View OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState) {
            var view = inflater.Inflate(Resource.Layout.fragment_settings, container, false)!;

            // --- Имя пользователя ---
            _txtUsername = view.FindViewById<EditText>(Resource.Id.txtUsername)!;
            _txtUsername.Text = AppSettings.GetUsername(Activity!);
            _txtUsername.FocusChange += (_, e) => {
                if (!e.HasFocus)
                    SaveUsername();
            };

            // --- Активация микрофона ---
            _activationGroup = view.FindViewById<RadioGroup>(Resource.Id.activationGroup)!;
            _modeVoice = view.FindViewById<RadioButton>(Resource.Id.modeVoice)!;
            _modePush = view.FindViewById<RadioButton>(Resource.Id.modePush)!;
            _voiceSection = view.FindViewById<View>(Resource.Id.voiceSection)!;
            _thresholdSeek = view.FindViewById<SeekBar>(Resource.Id.thresholdSeek)!;
            _delaySeek = view.FindViewById<SeekBar>(Resource.Id.delaySeek)!;
            _delayValue = view.FindViewById<EditText>(Resource.Id.delayValue)!;
            _btnTest = view.FindViewById<Button>(Resource.Id.btnTest)!;

            _meter = new VolumeMeterView(Activity!);
            view.FindViewById<FrameLayout>(Resource.Id.meterContainer)!
                .AddView(_meter, new FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

            // Начальное состояние из настроек (до подписки на события, чтобы не сохранять лишний раз).
            var mode = AppSettings.GetActivationMode(Activity!);
            (mode == ActivationMode.PushToTalk ? _modePush : _modeVoice).Checked = true;
            _voiceSection.Visibility = mode == ActivationMode.Voice ? ViewStates.Visible : ViewStates.Gone;

            float threshold = AppSettings.GetVoiceThreshold(Activity!);
            _thresholdSeek.Progress = (int)(threshold * _thresholdSeek.Max);
            _meter.Threshold = threshold;

            int delay = AppSettings.GetVoiceDeactivationDelay(Activity!);
            _delaySeek.Progress = Math.Min(delay, _delaySeek.Max);
            _delayValue.Text = delay.ToString();

            _activationGroup.CheckedChange += (_, e) => {
                var selected = e.CheckedId == Resource.Id.modePush ? ActivationMode.PushToTalk : ActivationMode.Voice;
                AppSettings.SetActivationMode(Activity!, selected);
                Client.SetActivationMode(selected); // живое переключение, если уже подключены
                _voiceSection!.Visibility = selected == ActivationMode.Voice ? ViewStates.Visible : ViewStates.Gone;
                if (selected != ActivationMode.Voice)
                    StopTest();
            };

            _thresholdSeek.ProgressChanged += (_, e) => {
                float t = e.Progress / (float)_thresholdSeek!.Max;
                if (_meter != null)
                    _meter.Threshold = t;
                if (_testPipe != null)
                    _testPipe.Threshold = t; // тест должен учитывать новый порог сразу
                if (e.FromUser) {
                    AppSettings.SetVoiceThreshold(Activity!, t);
                    Client.SetVoiceThreshold(t); // живое обновление, если уже подключены
                }
            };

            // Слайдер задержки (0..1000) — источник истины при перетаскивании.
            _delaySeek.ProgressChanged += (_, e) => {
                if (e.FromUser)
                    ApplyDelay(e.Progress);
            };

            // Числовой ввод (0..10000) — фиксируем при потере фокуса и по «Готово».
            _delayValue.FocusChange += (_, e) => {
                if (!e.HasFocus)
                    CommitDelayText();
            };
            _delayValue.EditorAction += (_, e) => {
                if (e.ActionId == Android.Views.InputMethods.ImeAction.Done)
                    CommitDelayText();
                e.Handled = false;
            };

            _btnTest.Click += (_, _) => {
                if (_testing)
                    StopTest();
                else
                    StartTest();
            };

            // --- Уровень логов ---
            _logLevelSpinner = view.FindViewById<Spinner>(Resource.Id.logLevelSpinner)!;
            var levelNames = new[] { "Trace — всё", "Debug", "Info", "Warn", "Error — только ошибки" };
            var levelAdapter = new ArrayAdapter<string>(
                Activity!, Android.Resource.Layout.SimpleSpinnerItem, levelNames);
            levelAdapter.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
            _logLevelSpinner.Adapter = levelAdapter;
            _logLevelSpinner.SetSelection((int)AppSettings.GetLogLevel(Activity!));
            _logLevelSpinner.ItemSelected += (_, e) => {
                var level = (LogLevel)e.Position;
                AppSettings.SetLogLevel(Activity!, level);
                LogSink.MinLevel = level; // живое применение
            };

            return view;
        }

        private void CommitDelayText() {
            int value = int.TryParse(_delayValue!.Text, out int v) ? v : 0;
            ApplyDelay(value);
        }

        // Единая точка применения задержки деактивации: кламп в [0, 10000], синхронизация обоих
        // контролов (слайдер ограничен 1000 мс — выше показывает максимум), сохранение и живое
        // обновление активного конвейера.
        private void ApplyDelay(int value) {
            value = Math.Clamp(value, 0, 10000);

            int sliderPos = Math.Min(value, _delaySeek!.Max);
            if (_delaySeek.Progress != sliderPos)
                _delaySeek.Progress = sliderPos;

            string text = value.ToString();
            if (_delayValue!.Text != text)
                _delayValue.Text = text;

            if (_testPipe != null)
                _testPipe.DeactivationDelayMs = value; // тест должен учитывать новую задержку сразу

            AppSettings.SetVoiceDeactivationDelay(Activity!, value);
            Client.SetVoiceDeactivationDelay(value); // живое обновление, если уже подключены
        }

        public override void OnResume() {
            base.OnResume();
            Activity!.Title = "Настройки";
        }

        // Освобождаем микрофон и сохраняем имя при уходе с экрана (см. подробности в Save-логике ниже).
        public override void OnPause() {
            base.OnPause();
            StopTest();
            SaveUsername();
            if (_delayValue != null)
                CommitDelayText();
        }

        private void SaveUsername() {
            if (_txtUsername != null && Activity != null)
                AppSettings.SetUsername(Activity, _txtUsername.Text);
        }

        // ===================== Тест громкости микрофона =====================

        private void StartTest() {
            if (_testing)
                return;

            if (Activity!.CheckSelfPermission(Manifest.Permission.RecordAudio) != Permission.Granted) {
                RequestPermissions(new[] { Manifest.Permission.RecordAudio }, RecordAudioRequestCode);
                return;
            }

            _testRecord = new AudioRecord(
                AudioSource.VoiceCommunication, 48000, ChannelIn.Mono, Encoding.Pcm16bit, 1920);
            _testRecord.StartRecording();

            // Тот же пайп, что и в реальном захвате: порог + задержка деактивации.
            _testPipe = new VoiceActivationPipe {
                Enabled = true,
                OutStream = new NullSink(),
                Threshold = AppSettings.GetVoiceThreshold(Activity!),
                DeactivationDelayMs = AppSettings.GetVoiceDeactivationDelay(Activity!),
            };

            _testing = true;
            if (_btnTest != null)
                _btnTest.Text = "Стоп";

            _testThread = new Thread(TestLoop) { IsBackground = true, Name = "MicTest" };
            _testThread.Start();
        }

        private void TestLoop() {
            var buffer = new byte[1920];
            float display = 0f;
            var record = _testRecord;
            var pipe = _testPipe;

            while (_testing && record != null) {
                int read = record.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    continue;

                float peak = AudioLevel.Peak(buffer.AsSpan(0, read));
                // Плавное падение полоски, как в Discord: вверх резко, вниз — постепенно.
                display = Math.Max(peak, display * 0.8f);

                // Прогоняем кадр через настоящий VAD — IsOpen учитывает порог и задержку деактивации.
                bool active = false;
                if (pipe != null) {
                    pipe.Write(buffer.AsSpan(0, read), null);
                    active = pipe.IsOpen;
                }

                if (_meter != null) {
                    _meter.Level = display; // setter делает PostInvalidate (потокобезопасно)
                    _meter.IsActive = active;
                }
            }
        }

        private void StopTest() {
            if (!_testing)
                return;

            _testing = false;
            try { _testThread?.Join(500); } catch { /* игнорируем */ }
            _testThread = null;
            _testPipe = null;

            var record = _testRecord;
            _testRecord = null;
            if (record != null) {
                try { record.Stop(); } catch { /* игнорируем */ }
                record.Release();
                record.Dispose();
            }

            if (_btnTest != null)
                _btnTest.Text = "Тест";
            if (_meter != null) {
                _meter.Level = 0f;
                _meter.IsActive = false;
            }
        }

        public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults) {
            if (requestCode != RecordAudioRequestCode)
                return;

            if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
                StartTest();
            else
                Toast.MakeText(Activity, "Нет доступа к микрофону — тест недоступен", ToastLength.Short)?.Show();
        }

        // Заглушка-приёмник: VoiceActivationPipe пишет в OutStream, но в тесте сам звук нам не нужен —
        // важно только его состояние IsOpen.
        private sealed class NullSink : IAudioPassiveConsumer {
            public bool Active => true;
            public void Write(Span<byte> data, Meta? meta) { }
        }
    }
}
