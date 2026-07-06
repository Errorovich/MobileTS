# TSLib: аудит модернизации под net9.0 BCL

Дата аудита: **2026-07-06**. TSLib таргетит `net9.0`, `LangVersion=8.0`, `Nullable=enable`.

TSLib изначально писался под netstandard2.0 / net472 и мульти-таргет; после перехода на чистый net9.0 в коде остались полифиллы, устаревшие идиомы и — что важнее — **мёртвые `#if`-ветки, из-за которых сейчас компилируется именно старый, медленный путь**. Ниже все найденные правки по убыванию приоритета.

Все замены — **runtime-API**, ни одна не требует поднимать `LangVersion` выше 8.0.

Порядок внедрения: правки делаются в репозитории TSLib ([Errorovich/TSLib](https://github.com/Errorovich/TSLib)), затем в MobileTS бампается указатель сабмодуля. Блок 1 можно сделать одним коммитом; блоки 2–3 — отдельными коммитами с проверкой подключения на устройстве.

---

## 1. Высокий приоритет — тривиально, реальный выигрыш

### 1.1. Мёртвые `#if NETSTANDARD* / NETCOREAPP3_1` ветки

На net9.0 эти символы не определены, поэтому везде компилируется **fallback-ветка** (старая и медленная). Чисто механическая зачистка.

| Файл | Что происходит | Замена |
|---|---|---|
| `Helper/SpanExtensions.cs:16-23` | `NewUtf8String` компилируется в `GetString(span.ToArray())` — **лишняя аллокация массива на каждый декод строки из протокола**. Горячий путь: `Deserializer`, `Messages.gen.cs` — каждое поле каждой команды. | `Tools.Utf8Encoder.GetString(span)` — перегрузка со `ReadOnlySpan<byte>` есть в BCL с netcoreapp2.1. Убрать `#if` целиком. |
| `Query/TsQueryClient.cs:104-133` | `NetworkToPipeLoopAsync` использует legacy `stream.ReadAsync(byte[],int,int)` + копирование в `Memory`, плюс лишний буфер `dataReadBuffer`. | Безусловно использовать ветку `stream.ReadAsync(Memory<byte>, CancellationToken)`, буфер удалить. Не голосовой путь (только query-клиент) — риск минимален. |
| `Helper/NativeLibraryLoader.cs:21-24,45-52` | Мёртвый P/Invoke `kernel32!LoadLibrary` и его ветка. | Оставить только `NativeLibrary.TryLoad`. |
| `TsBaseFunctions.FileTransfer.cs:53,89,160,236` | Четыре мёртвые ветки `#if NETSTANDARD2_0 stream.Dispose()`. | Оставить только `await stream.DisposeAsync()`. |
| `Commands/TsString.cs:14-17,111-136` | SSE2-путь `IsDoubleChar` за `#if NETCOREAPP3_1` мёртв — работает скалярное 9-кратное сравнение. | Либо просто удалить мёртвый код, либо вернуть векторизацию через `SearchValues<byte>` (см. п. 3.4). |

**Возможные проблемы:** нет — при условии, что TSLib больше никогда не будет мульти-таргетным (сейчас `TargetFrameworks=net9.0` только). Если планируется синхронизация с upstream Splamy — правки увеличат диф с апстримом (общий риск для всего аудита, апстрим фактически неактивен).

### 1.2. Пакеты-полифиллы в `TSLib.csproj`

- **`Nullable` 1.2.1** (`TSLib.csproj:35-38`) — полифилл nullable-атрибутов (`[MaybeNullWhen]`, `[NotNullIfNotNull]`…) для старых фреймворков. На net9.0 все атрибуты в BCL, пакет — no-op. Удалить.
- **`System.IO.Pipelines` 6.0.3** (`TSLib.csproj:42`) — входит в shared framework net9.0; явная ссылка лишь пиннит старую версию 6.0.3. Удалить ссылку (используется в `Query/TsQueryClient.cs` — продолжит работать из framework).

**Возможные проблемы:** нет; проверяется обычной сборкой.

### 1.3. `TsCrypt.cs:724-736` — устаревшие `SHA*Managed` + локи

`SHA1Managed`/`SHA256Managed`/`SHA512Managed` — `[Obsolete]` (SYSLIB0021, активные предупреждения компилятора). Держатся как shared-синглтоны под `lock (hashAlgo)`.

**Замена:** статические one-shot `SHA1.HashData()` / `SHA256.HashData()` / `SHA512.HashData()` (net5+, потокобезопасны). Удаляются три статических поля, `HashItInternal` и оба `lock`. ~15 вызовов через `Hash1It`/`Hash256It`/`Hash512It` в `TsCrypt.cs` и `License.cs`.

**Возможные проблемы:** это крипто-путь handshake — обязательно прогнать подключение к реальному серверу на устройстве. Поведенчески хэши идентичны, риск низкий. `Chaos.NaCl.Sha512.Hash` в `GetSharedSecret2` (строка ~358) **не трогать** — оставить для паритета с Ed25519-путём.

### 1.4. `Helper/Tools.cs` — устаревшие хелперы

- **`Tools.Random` → `Random.Shared`** — текущий общий `new Random()` **не потокобезопасен** (латентный баг: при гонке `Random` может начать возвращать нули); `Random.Shared` (net6+) потокобезопасен. Используется в т.ч. в `TsCrypt`. Это не только упрощение, но и багфикс.
- **`Tools.Clamp(int/float)` → `Math.Clamp`** — идентичное поведение.
- **`Tools.IsLinux` → `OperatingSystem.IsLinux()`** — текущая проверка через `Environment.OSVersion.Platform` устарела. Используется в `NativeLibraryLoader.cs`.

**Возможные проблемы:** нет. `Tools.PickRandom` переписать через `Random.Shared.Next`.

---

## 2. Средний приоритет — тривиально, но меньше выигрыш / нужна аккуратность

### 2.1. `SpanExtensions.Trim/TrimStart/TrimEnd(byte)` → `MemoryExtensions.Trim*`

Ручные циклы дублируют BCL `MemoryExtensions.Trim<T>(ReadOnlySpan<T>, T)` (поведение идентично, `byte : IEquatable<byte>`). Используется в `Deserializer.cs` (`.Trim(AsciiSpace)`). После замены методы можно удалить, а вместе с 1.1 файл `SpanExtensions.cs` исчезает целиком (останется только вызов `GetString` по месту или один тонкий хелпер).

### 2.2. `TsCrypt.GenerateTemporaryKey` (:508-510) — RNG

`using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(...)` → одна строка `RandomNumberGenerator.Fill(privateKey)` (net6+, без disposable).

### 2.3. `TsCrypt.CheckEqual` (:706-715) → `CryptographicOperations.FixedTimeEquals`

Самописный constant-time компаратор → проверенный BCL-примитив. **Проблема:** текущий код сравнивает только первые `len` байт, а `FixedTimeEquals` требует спаны равной длины — обязательно `a.Slice(0, len)` / `b.Slice(0, len)` (с сохранением текущей проверки `a1.Length < len || a2.Length < len`). 2 вызова в `FakeDecrypt`. Security-позитивная замена.

### 2.4. `Tools.ToUnix/FromUnix/UnixNow` → `DateTimeOffset`

Ручная epoch-математика → `DateTimeOffset.ToUnixTimeSeconds()` / `FromUnixTimeSeconds()`.

**Проблемы:**
- сохранить **усечение до `uint`** — wire-формат TS3 передаёт секунды как `uint` (`ToUnixTimeSeconds` возвращает `long`);
- сохранить UTC-семантику (текущий код на `DateTime`);
- `Messages.gen.cs` (генерированный код) вызывает `Tools.FromUnix` — либо сохранить сигнатуру хелпера (замена только внутренностей — рекомендуемый путь), либо править `Util.ttinclude`/шаблоны и регенерировать T4.

---

## 3. Умеренные — нужны тесты, каждая правка отдельным коммитом

### 3.1. `WaitBlock.cs` — таймауты команд

Идиома `Task.Delay` + `Task.WhenAny` → `Task.WaitAsync(timeout)` (net6+): меньше аллокаций таймеров на каждую команду. Заодно `TaskCompletionSource` создавать с `TaskCreationOptions.RunContinuationsAsynchronously` (сейчас продолжения могут исполняться инлайн на потоке диспетчера — известный источник дедлоков в таких конструкциях).

**Проблемы:** семантика отмены/таймаута (`WaitAsync` бросает `TimeoutException` — сейчас другой путь ошибки); проверить обработку таймаута команды (`CommandError` vs исключение).

### 3.2. `EventDispatcher.cs` (`ExtraThreadEventDispatcher`) → `System.Threading.Channels`

Ручной поток + `ConcurrentQueue<LazyNotification>` + `AutoResetEvent` — классический паттерн, который `Channel<T>` (unbounded, single-reader) закрывает чище и без ручной сигнализации.

**Проблемы:** меняется модель потоков (dedicated thread → async-цикл читателя); необходимо сохранить **строгий порядок доставки нотификаций** и то, на каком потоке исполняются обработчики (приложение маршалит в UI само, но порядок важен для `Book`). Средний риск.

### 3.3. `Newtonsoft.Json` → `System.Text.Json`

Единственное использование Newtonsoft в TSLib — 6 реализаций `JsonConverter<T>` для ID-обёрток в `Types.gen.cs` (генерируется из `Types.gen.tt`).

**Проблемы:**
- правится **шаблон** `Types.gen.tt`, не сгенерированный файл; регенерация T4 с известными нюансами (движок VS2022 глотает newline после inline-`<# #>`, шаблоны строго CRLF — см. README TSLib);
- API конвертеров STJ другой (`Read/Write(Utf8JsonReader/Writer)`);
- проверить потребителей: MobileTS уже на STJ source-gen (`AppJsonContext`) из-за `TrimMode=full` — если приложение нигде не сериализует TSLib-типы через Newtonsoft, уход от него **снимает потенциальный trim-риск** и убирает ~700 КБ зависимости из APK (если Newtonsoft не вытримливается целиком).

**Выигрыш:** минус целая зависимость. Стоит делать при следующей регенерации T4.

### 3.4. `TsString.IsDoubleChar` / `TokenLength` → `SearchValues<byte>`

Вместо мёртвого SSE2-пути (п. 1.1) и скалярных сравнений — `SearchValues<byte>.Create(...)` + `Contains` (net8+, векторизовано внутри BCL, кроссплатформенно — важно для ARM64 на Android, где SSE2-путь всё равно не работал бы). Убрать LINQ `str.Count(IsDoubleChar)` в `TokenLength` (аллокация делегата) — ручной цикл или `span.Count`.

**Проблемы:** замерить до/после; путь горячий при экранировании исходящих команд, но не критичный.

### 3.5. `TsCrypt.GetLeadingZeroBits` (:879-892)

Ручной побайтовый/побитовый скан SHA1-дайджеста в брутфорсе уровня безопасности identity (`ImproveSecurity`) → чтение 8-байтовыми блоками `BinaryPrimitives.ReadUInt64BigEndian` + `BitOperations.LeadingZeroCount`.

**Проблемы:** дайджест 20 байт — не кратен 8, аккуратно с хвостом; нужен паритет-тест на случайных входах против старой реализации. Греет только генерацию/улучшение identity — редкая операция, приоритет низкий.

### 3.6. `PacketHandler.cs` — сетевой слой (самая рискованная правка)

- Приём: ручной `SocketAsyncEventArgs` + callback `FetchPacketEvent` (:146-151, :332-371) → `await socket.ReceiveFromAsync(Memory<byte>, ...)` в цикле (`ValueTask`-перегрузки, net5+).
- Отправка: синхронный `socket.SendTo` в `SendRaw` (:762) → `SendToAsync(ReadOnlyMemory<byte>, ...)`.
- `resendTimer` (`System.Threading.Timer`, тик 100 мс, :166) → `PeriodicTimer` + async-цикл.

**Проблемы:** это **голосовой горячий путь** — точный порядок обработки, `sendLoopLock`, `Interlocked`-гварды (`closed`, `pingCheckRunning`) должны быть сохранены; синхронный `SendTo` для UDP на практике не блокирует, так что выигрыш скорее идиоматический. Делать только при необходимости (например, при охоте за джиттером), с длительными прогонами голоса на устройстве. Высокий риск / низкая срочность.

---

## 4. Не заменять — эквивалента в BCL нет

| Код | Почему остаётся |
|---|---|
| `Helper/R.cs` — `R<T,E>` / `E<T>` | В BCL нет Result/Either-типа. ~117 использований в 11 файлах TSLib + приложение. Каркас обработки ошибок всей библиотеки. Живёт в `namespace System` (намеренно) — помнить о риске коллизий имён с будущими BCL-типами. |
| `Helper/AsyncEventHandler.cs` | Async-событий (multicast `Task`-делегатов) в BCL нет до сих пор. Channels/Rx — другой дизайн, не drop-in. Основной механизм событий приложения (`Client.cs`). Единственная микро-оптимизация — заменить LINQ `Select`+`WhenAll` на ручной `Task[]`, но это не модернизация. |
| `Scheduler/DedicatedTaskScheduler.cs` + `TickWorker.cs` | Однопоточного `TaskScheduler` в BCL нет (`ConcurrentExclusiveSchedulerPair` работает на пуле и без таймеров). Однопоточность — контракт `TsFullClient`. |
| `Full/RingQueue.cs`, `Full/GenerationWindow.cs` | Не generic-кольцевые буферы, а протокольные окна пересборки пакетов TS3 (sequence/generation, out-of-order). `Channels`/`ConcurrentQueue` не о том. |
| `Full/QuickerLz.cs` | Wire-формат TS3 требует именно QuickLZ; `Deflate`/`Brotli` несовместимы. Внутри уже современно (`Span` + `BinaryPrimitives`). |
| AES-**EAX** (`Portable.BouncyCastle`) | В BCL только GCM/CCM; TeamSpeak требует EAX. BouncyCastle остаётся (также: P-256 `ECPoint`/`BigInteger`, ASN.1 DER, TS-специфичный KDF в `GetSharedSecret` — SHA1 от X-координаты, самодельные DER-теги `0x00/0x80/0xC0`). |
| Ed25519/Curve25519 (`Splamy.Ed25519.Toolkit` / `Chaos.NaCl`) | В net9.0 нет Ed25519 в `System.Security.Cryptography`. Нужен для лицензий (`License.cs`: `ge_*`-операции в `DeriveKey`) и проверки подписи версии (`EdCheck`). |
| ECDSA sign/verify через BouncyCastle (`TsCrypt.Sign/VerifySign`) | Формально BCL умеет P-256+SHA256, но ключи живут как BouncyCastle `ECPoint`/`BigInteger`, мост через `ECParameters` — риск без выгоды, BouncyCastle всё равно остаётся ради EAX. |
| `Commands/TsString.cs` `Escape`/`Unescape` | TS3-специфичное экранирование (`\s`, `\p`, …), в BCL нет. |
| `Heijden.Dns.Portable` | SRV-записей и TSDNS в `System.Net.Dns` нет до сих пор. |
| `Helper/DebugUtil.cs` | Hex с пробелами-разделителями — `Convert.ToHexString` не drop-in. Но используется только в закомментированных логах `PacketHandler` — **кандидат на удаление целиком**, а не на замену. |
| `Helper/MissingEnumCaseException.cs`, `LogId.cs`, `GetFlags`, `MathMod`, `Min/Max(TimeSpan)` | Доменные мелочи; BCL-эквивалента нет либо выгода нулевая. |

**Уже современно (ничего делать не нужно):** `BinaryPrimitives` используется повсеместно (`Packet`, `PacketHandler`, `QuickerLz`, `License`, `TsCrypt`); парсинг чисел — `Utf8Parser.TryParse`; base64 — `Convert.*` и `Base64.DecodeFromUtf8InPlace`. Ручных endianness-сдвигов и самописного парсинга чисел не осталось.

---

## Рекомендуемый порядок и верификация

1. **Коммит 1 (блоки 1.1–1.4 + 2.1–2.3):** механические правки. Верификация: `dotnet build MobileTS.sln` без предупреждений SYSLIB0021 + подключение к серверу на устройстве (handshake задействует все хэши, RNG и `FakeDecrypt`).
2. **Коммит 2 (2.4):** Unix-время, только внутренности `Tools.*` без смены сигнатур.
3. **Отдельные коммиты по мере надобности:** 3.1–3.6, каждый со своим прогоном; 3.3 (Newtonsoft) — совместить со следующей регенерацией T4.
4. После каждого пуша в TSLib — бамп сабмодуля в MobileTS.
