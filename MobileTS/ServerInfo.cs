using System.Text.Json.Serialization;

namespace MobileTS {
    public sealed class ServerInfo {
        /// <summary>
        /// IP или доменное имя сервера (host:port при необходимости)
        /// </summary>
        public string Address { get; set; } = "";

        /// <summary>
        /// Псевдоним сервера для отображения в списке (необязательно)
        /// </summary>
        public string? Alias { get; set; }

        /// <summary>
        /// Ник пользователя. Необязателен: если не задан, используется имя из настроек (AppSettings).
        /// </summary>
        public string? Nickname { get; set; }

        /// <summary>
        /// Пароль сервера
        /// </summary>
        public string? ServerPassword { get; set; }

        /// <summary>
        /// Канал по умолчанию
        /// </summary>
        public string? DefaultChannel { get; set; }

        /// <summary>
        /// Пароль канала
        /// </summary>
        public string? DefaultChannelPassword { get; set; }

        /// <summary>
        /// Что показывать в заголовке карточки: псевдоним, если задан, иначе адрес.
        /// </summary>
        [JsonIgnore]
        public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Address : Alias!;

        // Переходное состояние карточки в списке серверов; не сериализуется и не передаётся в сервис.
        [JsonIgnore] public bool IsExpanded { get; set; }
        [JsonIgnore] public bool IsEditing { get; set; }

        public override string ToString() => DisplayName;
    }
}
