using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace MobileTS {
    // Метаданные System.Text.Json, сгенерированные на этапе компиляции (source generation).
    //
    // В Release включён TrimMode=full — он вырезает рефлексивный сериализатор STJ, и тогда
    // обычный JsonSerializer.Serialize<List<ServerInfo>>(...) падает в рантайме с
    // InvalidOperationException: JsonSerializerIsReflectionDisabled (в Debug без триминга всё
    // работает, поэтому баг проявляется только в собранном APK).
    //
    // Решение: все сериализуемые приложением типы регистрируются здесь, а сериализация идёт через
    // AppJsonContext.Default.<Тип> — это сгенерированные контракты без рефлексии (trim/AOT-safe).
    // По умолчанию имена свойств — как в коде (PascalCase), что совпадает с уже сохранёнными в
    // SharedPreferences данными, поэтому старые записи читаются без миграции.
    [JsonSerializable(typeof(ServerInfo))]
    [JsonSerializable(typeof(List<ServerInfo>))]
    internal partial class AppJsonContext : JsonSerializerContext {
    }
}
