using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Text;

namespace MobileTS {
    public static class Crypto {
        private const string KeyAlias = "server_password_key";

        public static void EnsureKey() {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);

            if (ks.ContainsAlias(KeyAlias))
                return;

            var keyGenerator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            var spec = new KeyGenParameterSpec.Builder(
                    KeyAlias,
                    KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm)
                .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .Build();

            keyGenerator.Init(spec);
            keyGenerator.GenerateKey();
        }

        private static IKey GetKey() {
            var ks = KeyStore.GetInstance("AndroidKeyStore")!;
            ks.Load(null);

            var entry = ks.GetEntry(KeyAlias, null) as KeyStore.SecretKeyEntry;
            if (entry == null)
                throw new InvalidOperationException("Keystore entry не найден или недействителен");

            return entry.SecretKey;
        }

        public static string Encrypt(string plainText) {
            if (string.IsNullOrEmpty(plainText))
                return string.Empty;

            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            var key = GetKey();

            cipher.Init(CipherMode.EncryptMode, key);

            var iv = cipher.GetIV();
            var encrypted = cipher.DoFinal(Encoding.UTF8.GetBytes(plainText));

            var result = new byte[iv.Length + encrypted.Length];
            Buffer.BlockCopy(iv, 0, result, 0, iv.Length);
            Buffer.BlockCopy(encrypted, 0, result, iv.Length, encrypted.Length);

            return Convert.ToBase64String(result);
        }

        public static string Decrypt(string encryptedText) {
            if (string.IsNullOrEmpty(encryptedText))
                return string.Empty;

            var data = Convert.FromBase64String(encryptedText);
            var iv = data.Take(12).ToArray();
            var cipherText = data.Skip(12).ToArray();

            var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            var key = GetKey();
            cipher.Init(CipherMode.DecryptMode, key, new GCMParameterSpec(128, iv));

            return Encoding.UTF8.GetString(cipher.DoFinal(cipherText));
        }
    }
}
