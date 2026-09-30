using System;
using System.Collections.Generic;
using System.Text;

namespace Common.Networking
{
    /// <summary>
    /// 消息拆包器接口：负责在字节流里切出「一条完整业务消息」。
    /// Encode 用于发送侧加包头/包尾，Append 用于接收侧累积并回调完整消息。
    /// </summary>
    internal interface IMessageFramer
    {
        /// <summary>把一条业务消息编码成适合该协议发送的字节序列（例如加长度头）。</summary>
        byte[] Encode(byte[] payload);

        /// <summary>追加一段刚读到的字节；每切出一条完整消息就调用一次 onMessage。</summary>
        void Append(byte[] buffer, int offset, int count, Action<byte[]> onMessage);

        /// <summary>重置内部缓冲，通常在一次连接（重）建立后调用，避免上次的半包残留。</summary>
        void Reset();
    }

    /// <summary>根据配置里的 tcpFrameProtocol 创建对应的拆包器。</summary>
    internal static class MessageFramerFactory
    {
        public static IMessageFramer Create(NetworkConfig config)
        {
            switch (config.tcpFrameProtocol)
            {
                case FrameProtocol.DelimiterUtf8:
                    return new DelimiterMessageFramer(config.messageDelimiter, config.maxMessageSize);
                case FrameProtocol.Raw:
                    return new RawMessageFramer();
                case FrameProtocol.LengthPrefixLittleEndian:
                    return new LengthPrefixMessageFramer(config.maxMessageSize, true);
                default:
                    return new LengthPrefixMessageFramer(config.maxMessageSize, false);
            }
        }
    }

    /// <summary>
    /// 「4 字节长度前缀 + 消息体」拆包器（默认协议）。
    /// 优势是消息体可以包含任意字节，不需要转义；大端/小端由 littleEndian 决定。
    /// </summary>
    internal sealed class LengthPrefixMessageFramer : IMessageFramer
    {
        private readonly List<byte> buffer = new List<byte>();
        private readonly int maxMessageSize;
        private readonly bool littleEndian;

        public LengthPrefixMessageFramer(int maxMessageSize, bool littleEndian)
        {
            this.maxMessageSize = maxMessageSize;
            this.littleEndian = littleEndian;
        }

        /// <summary>在消息体前面拼上 4 字节长度头。</summary>
        public byte[] Encode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (payload.Length > maxMessageSize) throw new InvalidOperationException("Message exceeds maxMessageSize.");

            byte[] result = new byte[payload.Length + 4];
            if (littleEndian)
            {
                result[0] = (byte)payload.Length;
                result[1] = (byte)(payload.Length >> 8);
                result[2] = (byte)(payload.Length >> 16);
                result[3] = (byte)(payload.Length >> 24);
            }
            else
            {
                result[0] = (byte)(payload.Length >> 24);
                result[1] = (byte)(payload.Length >> 16);
                result[2] = (byte)(payload.Length >> 8);
                result[3] = (byte)payload.Length;
            }
            Buffer.BlockCopy(payload, 0, result, 4, payload.Length);
            return result;
        }

        /// <summary>
        /// 追加新数据后循环尝试切包：只要缓冲里「已有数据 &gt;= 长度头 + 消息体」就切出一条。
        /// 数据不够（半包）时直接 return 等下一次读取，因此天然支持粘包/半包。
        /// 长度头非法或超过 maxMessageSize 时抛异常，由外层断开连接，防止恶意长度导致内存暴涨。
        /// </summary>
        public void Append(byte[] source, int offset, int count, Action<byte[]> onMessage)
        {
            for (int i = 0; i < count; i++) buffer.Add(source[offset + i]);

            while (buffer.Count >= 4)
            {
                int length = littleEndian
                    ? buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24)
                    : (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
                if (length < 0 || length > maxMessageSize)
                    throw new InvalidOperationException("Invalid TCP frame length: " + length);
                if (buffer.Count < length + 4) return;

                byte[] message = new byte[length];
                if (length > 0) buffer.CopyTo(4, message, 0, length);
                buffer.RemoveRange(0, length + 4);
                onMessage(message);
            }
        }

        public void Reset() { buffer.Clear(); }
    }

    /// <summary>
    /// 分隔符拆包器（例如以 \n 结尾的文本协议）。
    /// 简单直观，但要求消息体本身不能包含分隔符，否则会被错误切分。
    /// </summary>
    internal sealed class DelimiterMessageFramer : IMessageFramer
    {
        private readonly List<byte> buffer = new List<byte>();
        private readonly byte[] delimiter;
        private readonly int maxMessageSize;

        public DelimiterMessageFramer(string delimiter, int maxMessageSize)
        {
            this.delimiter = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(delimiter) ? "\n" : delimiter);
            this.maxMessageSize = maxMessageSize;
        }

        /// <summary>在消息体末尾追加分隔符。</summary>
        public byte[] Encode(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            byte[] result = new byte[payload.Length + delimiter.Length];
            Buffer.BlockCopy(payload, 0, result, 0, payload.Length);
            Buffer.BlockCopy(delimiter, 0, result, payload.Length, delimiter.Length);
            return result;
        }

        /// <summary>不断查找分隔符，切出完整消息；缓冲超过上限说明对端协议异常，直接报错。</summary>
        public void Append(byte[] source, int offset, int count, Action<byte[]> onMessage)
        {
            for (int i = 0; i < count; i++) buffer.Add(source[offset + i]);
            int delimiterIndex;
            while ((delimiterIndex = FindDelimiter()) >= 0)
            {
                byte[] message = buffer.GetRange(0, delimiterIndex).ToArray();
                buffer.RemoveRange(0, delimiterIndex + delimiter.Length);
                onMessage(message);
            }
            if (buffer.Count > maxMessageSize) throw new InvalidOperationException("TCP message exceeds maxMessageSize.");
        }

        /// <summary>在缓冲中查找分隔符起始下标，找不到返回 -1。</summary>
        private int FindDelimiter()
        {
            for (int i = 0; i <= buffer.Count - delimiter.Length; i++)
            {
                bool found = true;
                for (int j = 0; j < delimiter.Length; j++)
                {
                    if (buffer[i + j] != delimiter[j]) { found = false; break; }
                }
                if (found) return i;
            }
            return -1;
        }

        public void Reset() { buffer.Clear(); }
    }

    /// <summary>
    /// 原始拆包器：不做任何边界处理，每次读到的字节直接当成一条消息。
    /// 仅适合上层自己已经定义边界的场景，正式 TCP 业务不推荐。
    /// </summary>
    internal sealed class RawMessageFramer : IMessageFramer
    {
        public byte[] Encode(byte[] payload) { return payload ?? throw new ArgumentNullException("payload"); }

        public void Append(byte[] source, int offset, int count, Action<byte[]> onMessage)
        {
            byte[] message = new byte[count];
            Buffer.BlockCopy(source, offset, message, 0, count);
            onMessage(message);
        }

        public void Reset() { }
    }
}
