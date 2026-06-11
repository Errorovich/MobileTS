# План: расширение функций голосового клиента TeamSpeak (MobileTS)

## Context

Сейчас экран сервера ([ServerFragment.cs](MobileTS/Activity/Server/ServerFragment.cs)) показывает плоское дерево каналов/клиентов с подсветкой «кто говорит» и локальными заглушками микрофона/звука (кнопки в нижней панели). Нужно довести клиент до практичного состояния: показывать статусы пользователей (мьют микрофона/звука, AFK), сообщать серверу о своём мьюте, дать переход между каналами, локальную регулировку громкости и мьют каждого собеседника, иконки серверов и чат текущего канала.

Вся нужная функциональность в TSLib уже есть и проверена:
- Свой мьют/AFK — команда `clientupdate` через публичный `TsFullClient.SendVoid(new TsCommand("clientupdate"){...})` (поля `client_input_muted`, `client_output_muted`, `client_away`, `client_away_message`; `TsCommand.Add` поддерживает `bool`/`string`).
- Переход в канал — публичный `ClientMove(ownClientId, channelId, password)`.
- Чат — публичные `SendChannelMessage(string)` и событие `OnEachTextMessage`.
- Иконки — публичный `DownloadFile(stream, channelId, "/icon_<crc>", ...)` (file transfer; требует активного подключения). `IconHash = System.Int32` (CRC, путь — беззнаковое представление).
- Статусы других в [Book.cs](TSLib/Generated/Book.cs): `Client.InputMuted`, `Client.OutputMuted`, `Client.AwayMessage` (null = не AFK), `Server.IconId`.
- Громкость/мьют собеседника — app-side: [MutePipe.cs](MobileTS/Audio/MutePipe.cs) уже умеет `MuteClient(ClientId,bool)`; [AudioTrackPipe.cs](MobileTS/Audio/AudioTrackPipe.cs) держит по одному `AudioTrack` на `ClientId` → громкость через нативный `AudioTrack.SetVolume`.

Все вызовы в клиент идут через `Client.Invoke(...)` (маршалинг на поток `DedicatedTaskScheduler`); UI читает дерево через `Client.GetBookSnapshot()` и подписан на `Client.OnBookChanged`/`Client.OnClientIsTalkingChanged`. TSLib не редактируем (CLAUDE.md) — всё делаем в MobileTS.

Решения по уточнениям пользователя:
- **AFK**: ручной тумблер с вводом сообщения (TSLib позволяет — `client_away` + `client_away_message`). Автоопределения нет.
- **Чат**: нижний переключатель вида **Каналы / Чат текущего канала**. Серверные сообщения не используем. История — только в памяти, в рамках одного подключения и одного канала; очищается при смене канала и при отключении.
- **Иконки сервера**: кэшировать после подключения (на диск), показывать кэш в списке серверов и в активных подключениях.
- **Громкость/мьют пользователя**: сохранять по UID клиента в `SharedPreferences`.

---

## Реализация

### 0. Бридж `Client`: новые серверные вызовы и события
Файлы: [MobileTS/Client/Client.cs](MobileTS/Client/Client.cs), [MobileTS/Client/Client.Audio.cs](MobileTS/Client/Client.Audio.cs), новый `MobileTS/Client/Client.Chat.cs`.

- В `ClientThread` добавить `localClient.OnEachClientUpdated += RaiseBookChanged;` — **критично**: сейчас изменения мьюта/AFK/иконок не обновляют дерево. Без этого иконки статусов не будут перерисовываться.
- Свой мьют микрофона/звука синхронизировать с сервером. Расширить существующие `SetMicMuted`/`SetSoundMuted`: помимо `audio?.Set...` слать `clientupdate`:
  ```csharp
  public static void SetMicMuted(bool muted) {
      audio?.SetMicMuted(muted);
      _ = Invoke(c => c.SendVoid(new TsCommand("clientupdate") { { "client_input_muted", muted } }));
  }
  ```
  Аналогично `SetSoundMuted` → `client_output_muted`.
- AFK: `SetAway(bool away, string? message)` → `clientupdate { client_away, client_away_message }`. Хранить локальный флаг для тумблера.
- Переход в канал: `MoveToChannel(ChannelId id, string? password)` → `Invoke(c => c.ClientMove(c.ClientId, id, password))`.
- Чат (в `Client.Chat.cs`): подписка на `OnEachTextMessage` в `ClientThread`; буфер `List<ChatMessage>` (sender, текст, время) только для `TextMessageTargetMode.Channel`. Очистка буфера, когда **свой** клиент переехал (в `OnEachClientMoved`, если `e.ClientId == c.ClientId`) и при `Disconnect`. События `OnChatMessage`/`OnChatCleared`, метод `SendChannelChat(string)` → `SendChannelMessage`, `GetChatHistory()`.
- Громкость/мьют по UID: `SetClientVolume(ClientId, float)`, `SetClientMuted(ClientId, bool)` — применяют к `AudioTrackPipe`/`MutePipe` и сохраняют по UID (см. п.4). При `OnEachClientEnterView` — применять сохранённые настройки по UID.

### 1. Иконки статусов: мьют микрофона/звука и AFK (вкл. себя)
Файлы: новые вектор-дроблы `MobileTS/Resources/drawable/ic_mic_off.xml`, `ic_speaker_off.xml`, `ic_afk.xml`; [item_client.xml](MobileTS/Resources/layout/item_client.xml); [ServerTreeAdapter.cs](MobileTS/Activity/Server/ServerTreeAdapter.cs).

- `item_client.xml` переделать из одиночного `TextView` в горизонтальный `LinearLayout`: имя (`txtClientName`, weight=1) + три `ImageView` (`imgMicOff`, `imgSpeakerOff`, `imgAfk`), по умолчанию `visibility=gone`.
- `ClientViewHolder` хранит три `ImageView`; в `OnBindViewHolder` показывать по `clientItem.Client.InputMuted`, `OutputMuted`, `AwayMessage != null`. Подсветка «говорит» (зелёный) — без изменений. Свой ряд — те же поля Book (после `OnEachClientUpdated → OnBookChanged` обновляются автоматически).

### 2. Отправка серверу состояния микрофона/звука
Файл: [ServerFragment.cs](MobileTS/Activity/Server/ServerFragment.cs). Кнопки `btnMuteMic`/`btnMuteSound` уже зовут `Client.SetMicMuted/SetSoundMuted` — после правки п.0 эти методы шлют `clientupdate`. Доп. UI не требуется.

### 3. Переход между каналами
Файлы: [ServerTreeAdapter.cs](MobileTS/Activity/Server/ServerTreeAdapter.cs), [ServerFragment.cs](MobileTS/Activity/Server/ServerFragment.cs).

- Сделать ряд канала кликабельным (callback из адаптера в фрагмент: `Action<BookChannel> OnChannelClick`).
- По клику звать `Client.MoveToChannel(channel.Id, null)`. Если канал с паролем (`channel.HasPassword == true`) — показать `AlertDialog` с вводом пароля и затем `MoveToChannel(id, pwd)`. Дерево обновится по `OnEachClientMoved → OnBookChanged`.

### 4. Громкость и мьют каждого пользователя (локально, с сохранением по UID)
Файлы: [AudioTrackPipe.cs](MobileTS/Audio/AudioTrackPipe.cs), `Client.Audio.cs`, `Client.cs`, [ServerTreeAdapter.cs](MobileTS/Activity/Server/ServerTreeAdapter.cs), новый диалог-лейаут `MobileTS/Resources/layout/dialog_client_volume.xml`, [AppSettings.cs](MobileTS/AppSettings.cs).

- `AudioTrackPipe`: добавить `Dictionary<ClientId,float> volumes` и `SetClientVolume(ClientId, float)` (0..1) — применять к существующему `AudioTrack.SetVolume` и к вновь создаваемому в `GetAudioTrack`. Мьют собеседника уже есть в `MutePipe.MuteClient`.
- Долгий тап (или иконка) по ряду клиента → `AlertDialog` (`dialog_client_volume.xml`): `SeekBar` громкости (0..100%) + чекбокс «Заглушить». На изменение — `Client.SetClientVolume(id, v)` / `Client.SetClientMuted(id, m)`.
- Сохранение по UID: в `AppSettings` добавить `Get/SetClientVolume(context, uid)` и `Get/SetClientMuted(context, uid)` (`SharedPreferences`, ключи `vol_<uid>`, `mute_<uid>`). `Client` мапит `ClientId↔Uid` по Book; применяет сохранённое при входе клиента (`OnEachClientEnterView`).

### 5. AFK-статус (ручной тумблер с сообщением)
Файлы: [fragment_server.xml](MobileTS/Resources/layout/fragment_server.xml) (кнопка `btnAfk` в нижней панели), [ServerFragment.cs](MobileTS/Activity/Server/ServerFragment.cs), `Client.cs`.

- Кнопка «Отойти/Вернуться». При включении — `AlertDialog` с `EditText` для необязательного сообщения → `Client.SetAway(true, message)`. Повторный тап → `Client.SetAway(false, null)`. Текст кнопки отражает состояние.
- Отображение AFK у всех — уже в п.1 (`ic_afk`).

### 6. Иконки сервера (кэш после подключения)
Файлы: новый `MobileTS/Client/Client.Icons.cs`, [ClientService.cs](MobileTS/Services/ClientService.cs) или `ClientThread`, [ServerAdapter.cs](MobileTS/Activity/ServersList/ServerAdapter.cs) + [item_server.xml](MobileTS/Resources/layout/item_server.xml) + [ServerViewHolder.cs](MobileTS/Activity/ServersList/ServerViewHolder.cs), [MainActivity.cs](MobileTS/Activity/MainActivity.cs) (шапка активных подключений).

- После `OnEachInitServer` (есть `Server.IconId`): если `IconId != 0`, скачать `DownloadFile(MemoryStream, ChannelId(0), "/icon_" + unchecked((uint)iconId), "")` через `Client.Invoke`. Сохранить PNG/raw в `context.FilesDir/server_icons/<addressKey>.img`. Ключ — нормализованный адрес сервера.
- `item_server.xml`: добавить `ImageView` (иконка) слева от заголовка. `ServerAdapter` грузит `Bitmap` из кэша по адресу (если файла нет — плейсхолдер/скрыть).
- Шапка активных подключений в `MainActivity.RebuildConnectedServers` — добавить иконку из кэша рядом с названием.

### 7. Чат текущего канала (нижний переключатель Каналы/Чат)
Файлы: [fragment_server.xml](MobileTS/Resources/layout/fragment_server.xml), новый `MobileTS/Resources/layout/item_chat_message.xml`, [ServerFragment.cs](MobileTS/Activity/Server/ServerFragment.cs), новый адаптер `ServerFragment.ChatAdapter` (как вложенный, по образцу `ServerTreeAdapter`), `Client.Chat.cs` (п.0).

- `fragment_server.xml`: контент-область — `FrameLayout` с двумя детьми: `recycler` (дерево) и `chatContainer` (`gone`) = `RecyclerView chatRecycler` (weight=1) + строка `EditText` + кнопка «Отправить». Над аудиопанелью — переключатель `Каналы | Чат` (два `Button`/`ToggleButton`), меняющий видимость `recycler`/`chatContainer`.
- Чат показывает только сообщения текущего канала из буфера `Client.GetChatHistory()`; новые — по `Client.OnChatMessage` (через `RunOnUiThread`). Очистка по `Client.OnChatCleared` (смена канала) и при пересоздании фрагмента после дисконнекта. Отправка — `Client.SendChannelChat(text)`.

---

## Файлы

**Создать:** `Client/Client.Chat.cs`, `Client/Client.Icons.cs`; дроблы `ic_mic_off.xml`, `ic_speaker_off.xml`, `ic_afk.xml` (+ при необходимости `ic_send.xml`); лейауты `dialog_client_volume.xml`, `item_chat_message.xml`.

**Изменить:** `Client/Client.cs`, `Client/Client.Audio.cs`, `Audio/AudioTrackPipe.cs`, `AppSettings.cs`, `Activity/Server/ServerFragment.cs`, `Activity/Server/ServerTreeAdapter.cs`, `Resources/layout/item_client.xml`, `Resources/layout/fragment_server.xml`, `Activity/ServersList/ServerAdapter.cs`, `Activity/ServersList/ServerViewHolder.cs`, `Resources/layout/item_server.xml`, `Activity/MainActivity.cs`.

Все новые `.cs` в проекте `MobileTS` подхватываются glob-ом сборки автоматически (новые ресурсы — тоже).

---

## Прогресс (TODO)

- [x] **0. Бридж**: OnEachClientUpdated→OnBookChanged; серверный мьют mic/sound; SetAway; MoveToChannel; per-client volume/mute по UID
- [x] **Client.Chat.cs**: буфер чата канала, OnChatMessage/OnChatCleared, SendChannelChat, очистка при move/disconnect
- [x] **Client.Icons.cs**: скачивание иконки сервера на initserver, кэш на диск по адресу
- [x] **AudioTrackPipe**: per-client SetClientVolume через нативный AudioTrack.SetVolume
- [x] **AppSettings**: persist per-UID volume/mute
- [x] **Drawables**: ic_mic_off, ic_speaker_off, ic_afk, ic_send
- [x] **item_client.xml + ServerTreeAdapter**: иконки статусов, клик по каналу, диалог громкости клиента
- [x] **fragment_server.xml + ServerFragment**: переключатель Каналы/Чат, кнопка AFK, адаптер чата, диалоги, проводка
- [x] **Иконки в списке серверов** (item_server.xml, ServerAdapter, ServerViewHolder) + шапка MainActivity
- [x] **Сборка** `dotnet build MobileTS/MobileTS.csproj` — успешно, 0 ошибок

**Статус: всё реализовано и собирается. Осталась проверка на устройстве (нужны 2 клиента на одном TS-сервере) — сценарии ниже.**

---

## Проверка (end-to-end)

Тест-проекта нет; проверяем сборкой:

```powershell
dotnet build MobileTS.sln
```

Сценарии (нужны 2 клиента на одном сервере — например, телефон + ПК-клиент TeamSpeak):
1. **Статусы**: на втором клиенте выключить микрофон/звук, уйти в AFK → в MobileTS у этого пользователя появляются иконки перечёркнутого микрофона/динамика и AFK; снятие — иконки исчезают.
2. **Свой мьют → сервер**: в MobileTS нажать «Микрофон: выкл»/«Звук: выкл» → на втором клиенте видно мьют у нашего пользователя; свой ряд тоже показывает иконку.
3. **Переход**: тап по другому каналу перемещает в него (и на втором клиенте видно перемещение); канал с паролем спрашивает пароль.
4. **Громкость/мьют**: открыть диалог по ряду собеседника, убавить громкость и заглушить → звук от него тише/пропадает; переподключиться → настройки восстановились (сохранение по UID).
5. **AFK свой**: тумблер «Отойти» с сообщением → на втором клиенте наш пользователь помечен AFK с сообщением.
6. **Иконки**: подключиться к серверу с иконкой → иконка появляется в шапке активных подключений и (после реконнекта/возврата в список) в списке серверов.
7. **Чат**: переключатель «Чат» внизу, отправить сообщение → приходит второму клиенту в этом канале и наоборот; смена канала очищает историю; дисконнект — история не сохраняется.

---

## Заметки по реализации (важные детали)

- **TSLib не редактировать** — только проект MobileTS.
- `SendVoid`, `ClientMove`, `SendChannelMessage`, `DownloadFile` — публичные в TSLib (проверено).
- `TsCommand.Add` поддерживает `bool` и `string` (проверено) → `clientupdate` с мьютом/away работает.
- `Client.Invoke` имеет 3 перегрузки: `Func<TsFullClient, Task>`, `Func<…, Task<E<CommandError>>>` (→ `Task<bool>`), `Func<…, Task<R<T[],CommandError>>>` (→ `Task<(bool,T[])>`).
- Свой `ClientId` — `client.ClientId` (используется в Client.cs:146).
- `Book.Client`: `InputMuted`, `OutputMuted`, `AwayMessage` (null=не AFK), `Uid` (`Uid?`), `Name`, `Id`, `Channel`.
- `Book.Channel`: `Id`, `Name`, `Order`, `Parent`, `HasPassword` (`bool?`), `IconId` (`IconHash?`).
- `Book.Server`: `IconId` (`IconHash`=int32), `Name`.
- События: `OnEachClientUpdated`, `OnEachClientEnterView`, `OnEachClientMoved` (`ClientMoved`: `ClientId`, `TargetChannelId`), `OnEachTextMessage` (`TextMessage`: `InvokerId`, `InvokerName`, `Message`, `Target` (`TextMessageTargetMode`), `TargetClientId`), `OnEachInitServer` (`ServerName`).
- Иконки скачиваются через file transfer (отдельный TCP сокет) — инициировать через `Client.Invoke`, путь `"/icon_" + unchecked((uint)iconId)`, канал `ChannelId(0)`, пароль `""`.
- Дерево обновляется через существующий `OnBookChanged` (UI пересобирает по `GetBookSnapshot()`); per-client talking — `OnClientIsTalkingChanged`.
- Нужно проверить при реализации: строковый аксессор `Uid` (вероятно `uid.Value`), сигнатуры аргументов событий `ClientMoved`/`TextMessage`/`ClientEnterView`.
