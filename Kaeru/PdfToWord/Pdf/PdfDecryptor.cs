using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PdfToWord.Pdf
{
    /// <summary>
    /// 標準セキュリティハンドラーの復号。空のユーザーパスワード（いわゆる
    /// 「閲覧はできるが編集できない」PDF）に対応する。
    /// </summary>
    public sealed class PdfDecryptor
    {
        private enum Method { None, Rc4, AesV2, AesV3 }

        private static readonly byte[] Pad = new byte[]
        {
            0x28,0xBF,0x4E,0x5E,0x4E,0x75,0x8A,0x41,0x64,0x00,0x4E,0x56,0xFF,0xFA,0x01,0x08,
            0x2E,0x2E,0x00,0xB6,0xD0,0x68,0x3E,0x80,0x2F,0x0C,0xA9,0xFE,0x64,0x53,0x69,0x7A
        };

        private byte[] _key;
        private Method _stringMethod = Method.Rc4;
        private Method _streamMethod = Method.Rc4;
        private int _revision;

        public bool IsSupported { get; private set; }
        public string UnsupportedReason { get; private set; }

        public static PdfDecryptor Create(PdfDictionary encrypt, byte[] documentId, PdfFile file)
        {
            return Create(encrypt, documentId, file, null);
        }

        public static PdfDecryptor Create(PdfDictionary encrypt, byte[] documentId, PdfFile file, string password)
        {
            var decryptor = new PdfDecryptor();
            decryptor.Initialize(encrypt, documentId, file, password ?? string.Empty);
            return decryptor;
        }

        /// <summary>利用者パスワードを 32 バイトに整える（規格の詰め物を使う）。</summary>
        private static byte[] PadPassword(string password)
        {
            byte[] raw = password == null ? new byte[0] : Encoding.GetEncoding(28591).GetBytes(password);
            var result = new byte[32];
            int count = Math.Min(32, raw.Length);
            Buffer.BlockCopy(raw, 0, result, 0, count);
            Buffer.BlockCopy(Pad, 0, result, count, 32 - count);
            return result;
        }

        private void Initialize(PdfDictionary encrypt, byte[] documentId, PdfFile file, string password)
        {
            IsSupported = false;
            var filter = file.Resolve(encrypt.Get("Filter")) as PdfName;
            if (filter != null && filter.Value != "Standard")
            {
                UnsupportedReason = "標準以外のセキュリティハンドラー (" + filter.Value + ")";
                return;
            }

            int v = file.GetInt(encrypt, "V", 0);
            _revision = file.GetInt(encrypt, "R", 2);
            int lengthBits = file.GetInt(encrypt, "Length", 40);
            int permissions = file.GetInt(encrypt, "P", -1);
            byte[] owner = GetBytes(file.Resolve(encrypt.Get("O")));
            byte[] user = GetBytes(file.Resolve(encrypt.Get("U")));
            bool encryptMetadata = true;
            var metadataFlag = file.Resolve(encrypt.Get("EncryptMetadata")) as PdfBoolean;
            if (metadataFlag != null) encryptMetadata = metadataFlag.Value;

            if (v == 4 || v == 5)
            {
                var cryptFilters = file.Resolve(encrypt.Get("CF")) as PdfDictionary;
                string streamFilterName = NameOf(file.Resolve(encrypt.Get("StmF")), "Identity");
                string stringFilterName = NameOf(file.Resolve(encrypt.Get("StrF")), "Identity");
                _streamMethod = MethodOf(cryptFilters, streamFilterName, file, ref lengthBits);
                _stringMethod = MethodOf(cryptFilters, stringFilterName, file, ref lengthBits);
            }
            else
            {
                _streamMethod = Method.Rc4;
                _stringMethod = Method.Rc4;
            }

            if (_revision >= 5)
            {
                byte[] ownerEncrypted = GetBytes(file.Resolve(encrypt.Get("UE")));
                if (user.Length < 48)
                {
                    UnsupportedReason = "/U エントリーが不正です";
                    return;
                }
                var validationSalt = new byte[8];
                var keySalt = new byte[8];
                Buffer.BlockCopy(user, 32, validationSalt, 0, 8);
                Buffer.BlockCopy(user, 40, keySalt, 0, 8);

                byte[] passwordBytes = Utf8Password(password);
                byte[] check = Hash2B(passwordBytes, validationSalt, new byte[0]);
                bool passwordOk = true;
                for (int i = 0; i < 32 && i < user.Length; i++)
                {
                    if (check[i] != user[i]) { passwordOk = false; break; }
                }
                if (!passwordOk)
                {
                    UnsupportedReason = password.Length == 0
                        ? "パスワードが設定されています"
                        : "パスワードが違います";
                    return;
                }
                byte[] intermediate = Hash2B(passwordBytes, keySalt, new byte[0]);
                _key = AesNoPadding(intermediate, new byte[16], ownerEncrypted, false);
                if (_key == null || _key.Length < 32)
                {
                    UnsupportedReason = "鍵の復元に失敗しました";
                    return;
                }
                if (_key.Length > 32)
                {
                    var trimmed = new byte[32];
                    Buffer.BlockCopy(_key, 0, trimmed, 0, 32);
                    _key = trimmed;
                }
                IsSupported = true;
                return;
            }

            int keyLength = Math.Max(5, Math.Min(16, lengthBits / 8));
            if (_revision == 2) keyLength = 5;

            byte[] padded = PadPassword(password);
            using (var md5 = MD5.Create())
            {
                var buffer = new MemoryStream();
                buffer.Write(padded, 0, padded.Length);
                if (owner.Length >= 32) buffer.Write(owner, 0, 32);
                else buffer.Write(owner, 0, owner.Length);
                buffer.WriteByte((byte)(permissions & 0xFF));
                buffer.WriteByte((byte)((permissions >> 8) & 0xFF));
                buffer.WriteByte((byte)((permissions >> 16) & 0xFF));
                buffer.WriteByte((byte)((permissions >> 24) & 0xFF));
                if (documentId != null) buffer.Write(documentId, 0, documentId.Length);
                if (_revision >= 4 && !encryptMetadata)
                {
                    buffer.WriteByte(0xFF); buffer.WriteByte(0xFF); buffer.WriteByte(0xFF); buffer.WriteByte(0xFF);
                }
                byte[] hash = md5.ComputeHash(buffer.ToArray());
                if (_revision >= 3)
                {
                    for (int i = 0; i < 50; i++)
                    {
                        var slice = new byte[keyLength];
                        Buffer.BlockCopy(hash, 0, slice, 0, keyLength);
                        hash = md5.ComputeHash(slice);
                    }
                }
                _key = new byte[keyLength];
                Buffer.BlockCopy(hash, 0, _key, 0, keyLength);
            }

            if (!UserPasswordMatches(user, documentId))
            {
                UnsupportedReason = password.Length == 0
                    ? "パスワードが設定されています"
                    : "パスワードが違います";
                IsSupported = false;
                return;
            }
            IsSupported = true;
        }

        /// <summary>UTF-8（最大 127 バイト）に整えたパスワード。改訂 5 以降で使う。</summary>
        private static byte[] Utf8Password(string password)
        {
            byte[] raw = Encoding.UTF8.GetBytes(password ?? string.Empty);
            if (raw.Length <= 127) return raw;
            var trimmed = new byte[127];
            Buffer.BlockCopy(raw, 0, trimmed, 0, 127);
            return trimmed;
        }

        /// <summary>/U と突き合わせて、いまの鍵で本文が読めるかを確かめる。</summary>
        private bool UserPasswordMatches(byte[] user, byte[] documentId)
        {
            if (user == null || user.Length < 16) return true;   // 確かめようがないときは通す
            if (_revision == 2)
            {
                byte[] expected = Rc4(_key, Pad);
                return SameBytes(expected, user, 32);
            }

            using (var md5 = MD5.Create())
            {
                var buffer = new MemoryStream();
                buffer.Write(Pad, 0, Pad.Length);
                if (documentId != null) buffer.Write(documentId, 0, documentId.Length);
                byte[] hash = md5.ComputeHash(buffer.ToArray());
                byte[] current = Rc4(_key, hash);
                for (int i = 1; i <= 19; i++)
                {
                    var temporaryKey = new byte[_key.Length];
                    for (int k = 0; k < _key.Length; k++) temporaryKey[k] = (byte)(_key[k] ^ i);
                    current = Rc4(temporaryKey, current);
                }
                return SameBytes(current, user, 16);
            }
        }

        private static bool SameBytes(byte[] a, byte[] b, int count)
        {
            if (a == null || b == null) return false;
            if (a.Length < count || b.Length < count) return false;
            for (int i = 0; i < count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string NameOf(PdfObject value, string fallback)
        {
            var name = value as PdfName;
            return name != null ? name.Value : fallback;
        }

        private static Method MethodOf(PdfDictionary cryptFilters, string filterName, PdfFile file, ref int lengthBits)
        {
            if (filterName == "Identity" || cryptFilters == null) return Method.None;
            var entry = file.Resolve(cryptFilters.Get(filterName)) as PdfDictionary;
            if (entry == null) return Method.None;
            string cfm = NameOf(file.Resolve(entry.Get("CFM")), "None");
            int length = file.GetInt(entry, "Length", 0);
            if (length > 0) lengthBits = length <= 40 ? length * 8 : length;
            switch (cfm)
            {
                case "V2": return Method.Rc4;
                case "AESV2": return Method.AesV2;
                case "AESV3": return Method.AesV3;
                default: return Method.None;
            }
        }

        private static byte[] GetBytes(PdfObject value)
        {
            var text = value as PdfStringObject;
            return text != null ? text.Bytes : new byte[0];
        }

        public byte[] DecryptStream(byte[] data, int objectNumber, int generation)
        {
            return Decrypt(data, objectNumber, generation, _streamMethod);
        }

        public byte[] DecryptString(byte[] data, int objectNumber, int generation)
        {
            return Decrypt(data, objectNumber, generation, _stringMethod);
        }

        private byte[] Decrypt(byte[] data, int objectNumber, int generation, Method method)
        {
            if (!IsSupported || data == null || data.Length == 0 || method == Method.None) return data;
            try
            {
                if (method == Method.AesV3)
                    return DecryptAes(data, _key);

                byte[] objectKey = MakeObjectKey(objectNumber, generation, method == Method.AesV2);
                if (method == Method.AesV2) return DecryptAes(data, objectKey);
                return Rc4(objectKey, data);
            }
            catch (Exception)
            {
                return data;
            }
        }

        private byte[] MakeObjectKey(int objectNumber, int generation, bool aes)
        {
            using (var md5 = MD5.Create())
            {
                var buffer = new MemoryStream();
                buffer.Write(_key, 0, _key.Length);
                buffer.WriteByte((byte)(objectNumber & 0xFF));
                buffer.WriteByte((byte)((objectNumber >> 8) & 0xFF));
                buffer.WriteByte((byte)((objectNumber >> 16) & 0xFF));
                buffer.WriteByte((byte)(generation & 0xFF));
                buffer.WriteByte((byte)((generation >> 8) & 0xFF));
                if (aes)
                {
                    buffer.WriteByte(0x73); buffer.WriteByte(0x41);
                    buffer.WriteByte(0x6C); buffer.WriteByte(0x54);
                }
                byte[] hash = md5.ComputeHash(buffer.ToArray());
                int length = Math.Min(16, _key.Length + 5);
                var result = new byte[length];
                Buffer.BlockCopy(hash, 0, result, 0, length);
                return result;
            }
        }

        private static byte[] DecryptAes(byte[] data, byte[] key)
        {
            if (data.Length <= 16) return new byte[0];
            var iv = new byte[16];
            Buffer.BlockCopy(data, 0, iv, 0, 16);
            int bodyLength = ((data.Length - 16) / 16) * 16;
            if (bodyLength <= 0) return new byte[0];
            var body = new byte[bodyLength];
            Buffer.BlockCopy(data, 16, body, 0, bodyLength);
            byte[] plain = AesNoPadding(key, iv, body, false);
            if (plain == null || plain.Length == 0) return new byte[0];
            int padding = plain[plain.Length - 1];
            if (padding >= 1 && padding <= 16 && padding <= plain.Length)
            {
                var trimmed = new byte[plain.Length - padding];
                Buffer.BlockCopy(plain, 0, trimmed, 0, trimmed.Length);
                return trimmed;
            }
            return plain;
        }

        private static byte[] AesNoPadding(byte[] key, byte[] iv, byte[] data, bool encrypt)
        {
            if (data == null || data.Length == 0) return new byte[0];
            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.KeySize = key.Length * 8;
                aes.Key = key;
                aes.IV = iv;
                using (ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                {
                    int length = (data.Length / 16) * 16;
                    if (length == 0) return new byte[0];
                    return transform.TransformFinalBlock(data, 0, length);
                }
            }
        }

        /// <summary>PDF 2.0 (R6) のハッシュアルゴリズム 2.B。</summary>
        private byte[] Hash2B(byte[] password, byte[] salt, byte[] userData)
        {
            byte[] k;
            using (var sha256 = SHA256.Create())
            {
                var input = new MemoryStream();
                input.Write(password, 0, password.Length);
                input.Write(salt, 0, salt.Length);
                input.Write(userData, 0, userData.Length);
                k = sha256.ComputeHash(input.ToArray());
            }
            if (_revision == 5) return k;

            byte[] e = null;
            for (int round = 0; ; round++)
            {
                var single = new MemoryStream();
                single.Write(password, 0, password.Length);
                single.Write(k, 0, k.Length);
                single.Write(userData, 0, userData.Length);
                byte[] block = single.ToArray();

                var k1 = new byte[block.Length * 64];
                for (int i = 0; i < 64; i++) Buffer.BlockCopy(block, 0, k1, i * block.Length, block.Length);

                var aesKey = new byte[16];
                var aesIv = new byte[16];
                Buffer.BlockCopy(k, 0, aesKey, 0, 16);
                Buffer.BlockCopy(k, 16, aesIv, 0, 16);
                e = AesNoPadding(aesKey, aesIv, k1, true);
                if (e == null || e.Length == 0) return k;

                int sum = 0;
                for (int i = 0; i < 16 && i < e.Length; i++) sum += e[i];
                int selector = sum % 3;
                if (selector == 0) using (var h = SHA256.Create()) k = h.ComputeHash(e);
                else if (selector == 1) using (var h = SHA384.Create()) k = h.ComputeHash(e);
                else using (var h = SHA512.Create()) k = h.ComputeHash(e);

                if (round >= 63 && (e[e.Length - 1] & 0xFF) <= round - 31) break;
            }
            var result = new byte[32];
            Buffer.BlockCopy(k, 0, result, 0, 32);
            return result;
        }

        public static byte[] Rc4(byte[] key, byte[] data)
        {
            var state = new byte[256];
            for (int i = 0; i < 256; i++) state[i] = (byte)i;
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + state[i] + key[i % key.Length]) & 0xFF;
                byte tmp = state[i]; state[i] = state[j]; state[j] = tmp;
            }
            var output = new byte[data.Length];
            int a = 0, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + 1) & 0xFF;
                b = (b + state[a]) & 0xFF;
                byte tmp = state[a]; state[a] = state[b]; state[b] = tmp;
                output[i] = (byte)(data[i] ^ state[(state[a] + state[b]) & 0xFF]);
            }
            return output;
        }
    }
}
