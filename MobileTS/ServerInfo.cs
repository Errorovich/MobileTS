using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MobileTS {
    public sealed class ServerInfo {
        /// <summary>
        /// IP или доменное имя сервера (host:port при необходимости)
        /// </summary>
        public string Address { get; set; }

        /// <summary>
        /// Ник пользователя
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

        public ServerInfo() { }

        public ServerInfo(
            string address,
            string? nickname,
            string? serverPassword,
            string? defaultChannel,
            string? defaultChannelPassword) {
            Address = address;
            Nickname = nickname;
            ServerPassword = serverPassword;
            DefaultChannel = defaultChannel;
            DefaultChannelPassword = defaultChannelPassword;
        }

        public override string ToString() {
            return $"{Address} ({Nickname ?? "no nick"})";
        }
    }
}
