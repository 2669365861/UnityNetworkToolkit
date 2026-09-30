using System;
using System.Text;

namespace Common.Networking
{
    /// <summary>
    /// 展厅设备通信常用的十六进制工具。
    /// 支持 "AA 01 FF"、"AA-01-FF"、"0xAA 0x01 0xFF" 和连续字符串。
    /// </summary>
    public static class HexCodec
    {
        private const string Digits = "0123456789ABCDEF";

        /// <summary>
        /// 字节数组转十六进制字符串，默认用空格分隔（如 "AA 01 FF"）。
        /// separator 传空字符串即可得到连续形式（如 "AA01FF"）。
        /// </summary>
        public static string ToHex(byte[] bytes, string separator = " ")
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            separator = separator ?? string.Empty;

            if (bytes.Length == 0) return string.Empty;
            StringBuilder result = new StringBuilder(bytes.Length * (2 + separator.Length));
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) result.Append(separator);
                result.Append(Digits[bytes[i] >> 4]);
                result.Append(Digits[bytes[i] & 0x0F]);
            }
            return result.ToString();
        }

        /// <summary>解析十六进制文本；格式非法时直接抛出 FormatException。</summary>
        public static byte[] Parse(string text)
        {
            byte[] bytes;
            string error;
            if (!TryParse(text, out bytes, out error))
                throw new FormatException(error);
            return bytes;
        }

        /// <summary>解析十六进制文本的简化版，不关心具体错误原因。</summary>
        public static bool TryParse(string text, out byte[] bytes)
        {
            string error;
            return TryParse(text, out bytes, out error);
        }

        /// <summary>
        /// 容错解析十六进制文本：
        /// 自动忽略空白、'-'、'_'、':'、',' 等分隔符，并识别 "0x" 前缀；
        /// 遇到非法字符、或数字位数为奇数时返回 false 并给出错误信息。
        /// </summary>
        public static bool TryParse(string text, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            if (text == null)
            {
                error = "Hex text cannot be null.";
                return false;
            }

            StringBuilder digits = new StringBuilder(text.Length);
            bool tokenHasDigits = false;
            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                // 只在单个 token 开头识别 "0x" 前缀，避免把普通数字里的 x 误判。
                if (current == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X') && !tokenHasDigits)
                {
                    i++;
                    continue;
                }

                if (IsHexDigit(current))
                {
                    digits.Append(current);
                    tokenHasDigits = true;
                    continue;
                }

                if (char.IsWhiteSpace(current) || current == '-' || current == '_' || current == ':' || current == ',')
                {
                    tokenHasDigits = false;
                    continue;
                }

                error = "Invalid hexadecimal character '" + current + "' at index " + i + ".";
                return false;
            }

            if (digits.Length == 0)
            {
                bytes = new byte[0];
                return true;
            }
            if ((digits.Length & 1) != 0)
            {
                error = "Hex text must contain an even number of digits.";
                return false;
            }

            bytes = new byte[digits.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)((HexValue(digits[i * 2]) << 4) | HexValue(digits[i * 2 + 1]));
            }
            return true;
        }

        /// <summary>按 UTF-8 把字节数组还原成文本（调试日志用）。</summary>
        public static string ToUtf8(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException("bytes");
            return Encoding.UTF8.GetString(bytes);
        }

        /// <summary>判断字符是否为合法的十六进制字符（不区分大小写）。</summary>
        private static bool IsHexDigit(char value)
        {
            return (value >= '0' && value <= '9') ||
                   (value >= 'a' && value <= 'f') ||
                   (value >= 'A' && value <= 'F');
        }

        /// <summary>把单个十六进制字符转成 0~15 的数值（调用前需确保字符合法）。</summary>
        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            return value - 'A' + 10;
        }
    }
}
