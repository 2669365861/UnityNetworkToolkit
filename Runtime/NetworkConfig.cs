using System;
using UnityEngine;

namespace Common.Networking
{
    /// <summary>可选的传输类型：TCP / UDP / 串口 / WebSocket。</summary>
    public enum NetworkTransportKind
    {
        Tcp,
        Udp,
        Serial,
        WebSocket
    }

    /// <summary>连接状态机的取值。</summary>
    public enum NetworkConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Faulted
    }

    /// <summary>传输层的消息拆包协议。</summary>
    public enum FrameProtocol
    {
        LengthPrefixBigEndian,
        LengthPrefixLittleEndian,
        DelimiterUtf8,
        Raw
    }

    /// <summary>
    /// 网络配置数据（纯 C# 数据类，会被序列化进 ScriptableObject 资产）。
    /// 字段分组标题由 Editor 目录下的 NetworkConfigAssetEditor 按当前界面语言绘制，
    /// 因此这里刻意不使用 [Header] 特性，避免和自定义 Inspector 的分组标题重复出现。
    /// </summary>
    [Serializable]
    public sealed class NetworkConfig
    {
        // ------------------------- TCP / UDP -------------------------
        public string host = "127.0.0.1";
        public int tcpPort = 9000;
        public int udpPort = 9001;
        public int udpLocalPort = 0;

        // ------------------------- TCP 拆包 -------------------------
        public FrameProtocol tcpFrameProtocol = FrameProtocol.LengthPrefixBigEndian;
        public string messageDelimiter = "\n";
        public int maxMessageSize = 1024 * 1024;

        // ------------------------- 缓冲区与超时 -------------------------
        public int receiveBufferSize = 16 * 1024;
        public int sendBufferSize = 16 * 1024;
        public int connectTimeoutMs = 5000;
        public int receivePollTimeoutMs = 100;

        // ------------------------- 断线重连 -------------------------
        public bool autoReconnect = true;
        public int reconnectIntervalMs = 2000;
        public int maxReconnectAttempts = 5; // -1 = 无限重试；0 = 禁用重连

        // ------------------------- Socket 选项 -------------------------
        public bool noDelay = true;
        public bool keepAlive = true;
        public bool useTls = false;
        public string tlsServerName = "";
        public bool allowInvalidTlsCertificate = false;

        // ------------------------- 串口 -------------------------
        public string serialPortName = "COM3";
        public int serialBaudRate = 115200;
        public int serialDataBits = 8;
        public int serialStopBits = 1;
        public int serialParity = 0; // 对应 System.IO.Ports.Parity.None
        public bool serialDtrEnable = false;
        public bool serialRtsEnable = false;

        // ------------------------- UDP -------------------------
        public bool udpEnableBroadcast = false;

        // ------------------------- WebSocket -------------------------
        public string webSocketUrl = "ws://127.0.0.1:9002";
        public string webSocketSubProtocol = "";
        public int webSocketConnectTimeoutMs = 5000;

        /// <summary>
        /// 校验并把各字段收敛到合法范围（端口、超时、缓冲区、串口参数等）。
        /// 在真正建立连接之前调用，避免把非法数值直接抛给底层 socket 才报错。
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";
            if (string.IsNullOrWhiteSpace(webSocketUrl)) webSocketUrl = "ws://127.0.0.1:9002";
            tcpPort = ClampPort(tcpPort, 9000);
            udpPort = ClampPort(udpPort, 9001);
            udpLocalPort = ClampPort(udpLocalPort, 0);
            maxMessageSize = Math.Max(256, maxMessageSize);
            receiveBufferSize = Math.Max(1024, receiveBufferSize);
            sendBufferSize = Math.Max(1024, sendBufferSize);
            connectTimeoutMs = Math.Max(100, connectTimeoutMs);
            receivePollTimeoutMs = Math.Max(20, receivePollTimeoutMs);
            reconnectIntervalMs = Math.Max(100, reconnectIntervalMs);
            webSocketConnectTimeoutMs = Math.Max(100, webSocketConnectTimeoutMs);
            maxReconnectAttempts = Math.Max(-1, maxReconnectAttempts);
            serialBaudRate = Math.Max(300, serialBaudRate);
            serialDataBits = Math.Max(5, Math.Min(8, serialDataBits));
            serialStopBits = serialStopBits == 2 ? 2 : 1;
            serialParity = Math.Max(0, Math.Min(4, serialParity));
        }

        /// <summary>
        /// 深拷贝当前配置。
        /// 运行时使用的配置都来自克隆体，绝不直接修改工程里的原始资产，避免编辑器里被意外改脏。
        /// </summary>
        public NetworkConfig Clone()
        {
            return new NetworkConfig
            {
                host = host,
                tcpPort = tcpPort,
                udpPort = udpPort,
                udpLocalPort = udpLocalPort,
                tcpFrameProtocol = tcpFrameProtocol,
                messageDelimiter = messageDelimiter,
                maxMessageSize = maxMessageSize,
                receiveBufferSize = receiveBufferSize,
                sendBufferSize = sendBufferSize,
                connectTimeoutMs = connectTimeoutMs,
                receivePollTimeoutMs = receivePollTimeoutMs,
                autoReconnect = autoReconnect,
                reconnectIntervalMs = reconnectIntervalMs,
                maxReconnectAttempts = maxReconnectAttempts,
                noDelay = noDelay,
                keepAlive = keepAlive,
                useTls = useTls,
                tlsServerName = tlsServerName,
                allowInvalidTlsCertificate = allowInvalidTlsCertificate,
                serialPortName = serialPortName,
                serialBaudRate = serialBaudRate,
                serialDataBits = serialDataBits,
                serialStopBits = serialStopBits,
                serialParity = serialParity,
                serialDtrEnable = serialDtrEnable,
                serialRtsEnable = serialRtsEnable,
                udpEnableBroadcast = udpEnableBroadcast,
                webSocketUrl = webSocketUrl,
                webSocketSubProtocol = webSocketSubProtocol,
                webSocketConnectTimeoutMs = webSocketConnectTimeoutMs
            };
        }

        /// <summary>端口合法性校验：超出 0 ~ 65535 范围时回退到默认端口。</summary>
        private static int ClampPort(int value, int fallback)
        {
            return value >= 0 && value <= 65535 ? value : fallback;
        }
    }

    /// <summary>
    /// NetworkConfig 的资产包装器，可在 Project 窗口右键创建：
    /// Create → Networking → Network Config。
    /// Inspector 面板支持中英文切换，见 Editor/NetworkConfigAssetEditor.cs。
    /// </summary>
    [CreateAssetMenu(fileName = "NetworkConfig", menuName = "Networking/Network Config")]
    public sealed class NetworkConfigAsset : ScriptableObject
    {
        public NetworkConfig config = new NetworkConfig();

        /// <summary>
        /// 生成一份经过校验的运行时配置副本（先深拷贝再 Validate）。
        /// 组件连接时调用，保证网络层拿到的永远是一份安全、独立的配置。
        /// </summary>
        public NetworkConfig CreateRuntimeConfig()
        {
            NetworkConfig result = config == null ? new NetworkConfig() : config.Clone();
            result.Validate();
            return result;
        }
    }
}
