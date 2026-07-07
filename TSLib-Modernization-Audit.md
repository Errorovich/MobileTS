# TSLib: аудит модернизации под net9.0 BCL

Дата аудита: **2026-07-06**. TSLib таргетит `net9.0`, `Nullable=enable`.

> **Статус:** механические блоки аудита (мёртвые `#if`-ветки, пакеты-полифиллы, `SHA*.HashData`, `Random.Shared`, `FixedTimeEquals`, `RandomNumberGenerator.Fill`, `MemoryExtensions.Trim`, Unix-время через `DateTimeOffset`) **выполнены 2026-07-07** (коммиты TSLib `848f10a`, `5e115f4`), пин `LangVersion=8.0` снят (`f70196a`), **подключение на устройстве прогнано — работает**. Ниже только оставшиеся пункты.

Порядок внедрения: правки делаются в репозитории TSLib ([Errorovich/TSLib](https://github.com/Errorovich/TSLib)), затем в MobileTS бампается указатель сабмодуля. Каждая правка — отдельным коммитом со своим прогоном на устройстве.

---

## 1. Оставшиеся правки — нужны тесты, по мере надобности

### 1.1. `WaitBlock.cs` — таймауты команд

Идиома `Task.Delay` + `Task.WhenAny` → `Task.WaitAsync(timeout)` (net6+): меньше аллокаций таймеров на каждую команду. Заодно `TaskCompletionSource` создавать с `TaskCreationOptions.RunContinuationsAsynchronously` (сейчас продолжения могут исполняться инлайн на потоке диспетчера — известный источник дедлоков в таких конструкциях).

**Проблемы:** семантика отмены/таймаута (`WaitAsync` бросает `TimeoutException` — сейчас другой путь ошибки); проверить обработку таймаута команды (`CommandError` vs исключение).

### 1.2. `EventDispatcher.cs` (`ExtraThreadEventDispatcher`) → `System.Threading.Channels`

Ручной поток + `ConcurrentQueue<LazyNotification>` + `AutoResetEvent` — классический паттерн, который `Channel<T>` (unbounded, single-reader) закрывает чище и без ручной сигнализации.

**Проблемы:** меняется модель потоков (dedicated thread → async-цикл читателя); необходимо сохранить **строгий порядок доставки нотификаций** и то, на каком потоке исполняются обработчики (приложение маршалит в UI само, но порядок важен для `Book`). Средний риск.

### 1.3. `Newtonsoft.Json` → `System.Text.Json`

Единственное использование Newtonsoft в TSLib — 6 реализаций `JsonConverter<T>` для ID-обёрток в `Types.gen.cs` (генерируется из `Types.gen.tt`).

**Проблемы:**
- правится **шаблон** `Types.gen.tt`, не сгенерированный файл; регенерация T4 с известными нюансами (движок VS2022 глотает newline после inline-`<# #>`, шаблоны строго CRLF — см. README TSLib);
- API конвертеров STJ другой (`Read/Write(Utf8JsonReader/Writer)`);
- проверить потребителей: MobileTS уже на STJ source-gen (`AppJsonContext`) из-за `TrimMode=full` — если приложение нигде не сериализует TSLib-типы через Newtonsoft, уход от него **снимает потенциальный trim-риск** и убирает ~700 КБ зависимости из APK (если Newtonsoft не вытримливается целиком).

**Выигрыш:** минус целая зависимость. Стоит делать при следующей регенерации T4 — и совместить с переводом ID-обёрток на `readonly record struct` (см. п. 2), раз всё равно трогается шаблон.

### 1.4. `TsString.IsDoubleChar` / `TokenLength` → `SearchValues<byte>`

`SearchValues<byte>.Create(...)` + `Contains` (net8+, векторизовано внутри BCL, кроссплатформенно — важно для ARM64 на Android). Убрать LINQ `str.Count(IsDoubleChar)` в `TokenLength` (аллокация делегата) — ручной цикл или `span.Count`. Мёртвый SSE2-путь уже удалён (2026-07-07).

**Проблемы:** замерить до/после; путь горячий при экранировании исходящих команд, но не критичный.

### 1.5. `TsCrypt.GetLeadingZeroBits`

Ручной побайтовый/побитовый скан SHA1-дайджеста в брутфорсе уровня безопасности identity (`ImproveSecurity`) → чтение 8-байтовыми блоками `BinaryPrimitives.ReadUInt64BigEndian` + `BitOperations.LeadingZeroCount`.

**Проблемы:** дайджест 20 байт — не кратен 8, аккуратно с хвостом; нужен паритет-тест на случайных входах против старой реализации. Греет только генерацию/улучшение identity — редкая операция, приоритет низкий.

### 1.6. `PacketHandler.cs` — сетевой слой (самая рискованная правка)

- Приём: ручной `SocketAsyncEventArgs` + callback `FetchPacketEvent` → `await socket.ReceiveFromAsync(Memory<byte>, ...)` в цикле (`ValueTask`-перегрузки, net5+).
- Отправка: синхронный `socket.SendTo` в `SendRaw` → `SendToAsync(ReadOnlyMemory<byte>, ...)`.
- `resendTimer` (`System.Threading.Timer`, тик 100 мс) → `PeriodicTimer` + async-цикл.

**Проблемы:** это **голосовой горячий путь** — точный порядок обработки, `sendLoopLock`, `Interlocked`-гварды (`closed`, `pingCheckRunning`) должны быть сохранены; синхронный `SendTo` для UDP на практике не блокирует, так что выигрыш скорее идиоматический. Делать только при необходимости (например, при охоте за джиттером), с длительными прогонами голоса на устройстве. Высокий риск / низкая срочность.

---

## 2. Новый синтаксис (LangVersion теперь по умолчанию C# 13)

Внедрять оппортунистически, по мере правок соседнего кода, без big-bang-реформата:

| Фича | Где применимо | Ценность |
|---|---|---|
| `readonly record struct` (C# 10) | ID-обёртки в `Types.gen.cs` (`ClientId`, `ChannelId`, `Uid`, …) — сейчас шаблон `Types.gen.tt` генерирует ручные `Equals`/`GetHashCode`/операторы | **Самая осязаемая**: сильно ужимает шаблон и генерённый код; совместить с п. 1.3 (Newtonsoft) |
| `is not null`, паттерны `and`/`or`/`not` (C# 9) | Повсеместно вместо `!(x is null)` и цепочек сравнений | Читаемость, механическая замена |
| File-scoped namespaces (C# 10) | Все файлы: минус уровень отступа | Косметика; если делать — одним механическим коммитом, диф большой |
| Target-typed `new()` (C# 9), collection expressions `[]` (C# 12) | Инициализаторы полей | Косметика |
| UTF-8 литералы `"…"u8` (C# 11) | `TsCrypt.Ts3InitMac`, `TsIdentityObfuscationKey` | Малая: обе константы и так статические, считаются один раз |
| `params ReadOnlySpan<T>` (C# 13) | Хелперы построения команд (`TsCommand`) | Убирает аллокации массивов на вызовах; точечно |
| `required`/`init` (C# 9/11) | POCO сообщений/Book | Только вместе с правкой шаблонов, отдельно не затевать |

---

## 3. Не заменять — эквивалента в BCL нет

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
