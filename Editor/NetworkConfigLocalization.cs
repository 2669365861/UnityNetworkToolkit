using UnityEditor;

namespace Common.Networking.EditorTools
{
    /// <summary>Inspector 支持的语言。</summary>
    public enum InspectorLanguage
    {
        Chinese = 0,
        English = 1
    }

    /// <summary>NetworkConfig 中一个字段的双语显示定义。</summary>
    public sealed class ConfigFieldDefinition
    {
        /// <summary>NetworkConfig 中的字段名（必须与代码里的字段名完全一致）。</summary>
        public string Name;

        public string ChineseLabel;
        public string EnglishLabel;

        public string ChineseTooltip;
        public string EnglishTooltip;

        /// <summary>非空时该字段用下拉框显示；中文选项名，顺序需与枚举/取值表一致。</summary>
        public string[] ChineseOptions;

        /// <summary>非空时该字段用下拉框显示；英文选项名。</summary>
        public string[] EnglishOptions;

        /// <summary>
        /// 下拉框选项对应的实际取值。
        /// 为 null 表示字段本身就是枚举，直接按枚举下标取值；
        /// 非 null 表示字段是 int，需要按下标映射回真实数值（如串口校验位、停止位）。
        /// </summary>
        public int[] OptionValues;
    }

    /// <summary>Inspector 里的一组配置（对应一个分组标题）。</summary>
    public sealed class ConfigSectionDefinition
    {
        public string ChineseTitle;
        public string EnglishTitle;
        public ConfigFieldDefinition[] Fields;
    }

    /// <summary>
    /// NetworkConfig Inspector 的双语文案表与语言状态。
    /// 语言偏好保存在 EditorPrefs 中，跨 Unity 会话保留，且不会写进配置资产，
    /// 因此切换语言不会造成资产被改脏（不会进版本控制）。
    /// </summary>
    public static class NetworkConfigLocalization
    {
        private const string LanguagePrefsKey = "UnityNetworkToolkit.Inspector.Language";

        private static InspectorLanguage current;

        /// <summary>语言变化通知，供 Inspector 刷新界面。</summary>
        public static event System.Action LanguageChanged;

        static NetworkConfigLocalization()
        {
            current = (InspectorLanguage)EditorPrefs.GetInt(LanguagePrefsKey, (int)InspectorLanguage.Chinese);
        }

        /// <summary>当前语言（默认中文）。写入时会立即持久化并广播变化。</summary>
        public static InspectorLanguage Language
        {
            get { return current; }
            set
            {
                if (current == value) return;
                current = value;
                EditorPrefs.SetInt(LanguagePrefsKey, (int)value);
                System.Action handler = LanguageChanged;
                if (handler != null) handler();
            }
        }

        /// <summary>当前是否中文界面。</summary>
        public static bool IsChinese
        {
            get { return current == InspectorLanguage.Chinese; }
        }

        /// <summary>按当前语言二选一。</summary>
        public static string Pick(string chinese, string english)
        {
            return IsChinese ? chinese : english;
        }

        // ============================ 字段分组定义 ============================

        /// <summary>普通字段（文本 / 数值 / 布尔）。</summary>
        private static ConfigFieldDefinition Text(string name, string chinese, string english,
            string chineseTip = null, string englishTip = null)
        {
            return new ConfigFieldDefinition
            {
                Name = name,
                ChineseLabel = chinese,
                EnglishLabel = english,
                ChineseTooltip = chineseTip,
                EnglishTooltip = englishTip
            };
        }

        /// <summary>枚举字段（按枚举下标取值）。</summary>
        private static ConfigFieldDefinition EnumChoice(string name, string chinese, string english,
            string[] chineseOptions, string[] englishOptions, string chineseTip = null, string englishTip = null)
        {
            return new ConfigFieldDefinition
            {
                Name = name,
                ChineseLabel = chinese,
                EnglishLabel = english,
                ChineseOptions = chineseOptions,
                EnglishOptions = englishOptions,
                ChineseTooltip = chineseTip,
                EnglishTooltip = englishTip
            };
        }

        /// <summary>用 int 存储的枚举字段（按下标映射回真实数值）。</summary>
        private static ConfigFieldDefinition IntChoice(string name, string chinese, string english,
            string[] chineseOptions, string[] englishOptions, int[] values,
            string chineseTip = null, string englishTip = null)
        {
            return new ConfigFieldDefinition
            {
                Name = name,
                ChineseLabel = chinese,
                EnglishLabel = english,
                ChineseOptions = chineseOptions,
                EnglishOptions = englishOptions,
                OptionValues = values,
                ChineseTooltip = chineseTip,
                EnglishTooltip = englishTip
            };
        }

        private static ConfigSectionDefinition Section(string chinese, string english, params ConfigFieldDefinition[] fields)
        {
            return new ConfigSectionDefinition
            {
                ChineseTitle = chinese,
                EnglishTitle = english,
                Fields = fields
            };
        }

        /// <summary>
        /// Inspector 的完整字段分组顺序。
        /// 顺序与 NetworkConfig 中的字段声明保持一致，新增字段时记得同步这里。
        /// </summary>
        public static readonly ConfigSectionDefinition[] Sections =
        {
            Section("TCP / UDP", "TCP / UDP",
                Text("host", "目标地址", "Host",
                    "TCP / UDP 连接的目标服务器地址，例如 127.0.0.1 或局域网 IP。",
                    "Target server address for TCP/UDP, e.g. 127.0.0.1 or a LAN IP."),
                Text("tcpPort", "TCP 端口", "TCP Port"),
                Text("udpPort", "UDP 端口", "UDP Port"),
                Text("udpLocalPort", "UDP 本地端口", "UDP Local Port",
                    "本地监听端口；0 表示由系统自动分配（设备需要回发时才需要固定端口）。",
                    "Local listen port; 0 lets the OS pick a free port. Set a fixed port only if the device replies back.")),

            Section("TCP 拆包", "TCP Framing",
                EnumChoice("tcpFrameProtocol", "TCP 消息协议", "TCP Frame Protocol",
                    new[] { "长度前缀（大端，推荐）", "长度前缀（小端）", "UTF-8 分隔符", "原始数据（不拆包）" },
                    new[] { "Length Prefix (Big Endian, recommended)", "Length Prefix (Little Endian)", "UTF-8 Delimiter", "Raw (no framing)" },
                    "决定 TCP 字节流如何切分出一条完整消息；工控设备常用小端长度前缀或分隔符。",
                    "How the TCP byte stream is split into messages. Industrial devices often use a little-endian length prefix or a delimiter."),
                Text("messageDelimiter", "消息分隔符", "Message Delimiter",
                    "仅在选择「UTF-8 分隔符」协议时生效，例如 \\n 或 \\r\\n。",
                    "Only used by the UTF-8 Delimiter protocol, e.g. \\n or \\r\\n."),
                Text("maxMessageSize", "最大消息字节数", "Max Message Size",
                    "单条消息的上限，超过会被判定为协议异常并断开，用于防御异常长度导致的内存风险。",
                    "Upper bound of a single message. Larger frames are treated as protocol errors, protecting against memory abuse.")),

            Section("缓冲区与超时", "Buffers & Timeout",
                Text("receiveBufferSize", "接收缓冲区（字节）", "Receive Buffer Size"),
                Text("sendBufferSize", "发送缓冲区（字节）", "Send Buffer Size"),
                Text("connectTimeoutMs", "连接超时（毫秒）", "Connect Timeout (ms)"),
                Text("receivePollTimeoutMs", "接收轮询超时（毫秒）", "Receive Poll Timeout (ms)",
                    "串口接收循环的读超时，超时后继续轮询而不是报错。",
                    "Read timeout for the serial receive loop; a timeout just continues polling instead of failing.")),

            Section("断线重连", "Reconnect",
                Text("autoReconnect", "自动重连", "Auto Reconnect"),
                Text("reconnectIntervalMs", "重连间隔（毫秒）", "Reconnect Interval (ms)"),
                Text("maxReconnectAttempts", "最大重连次数", "Max Reconnect Attempts",
                    "0 = 禁用重连；-1 = 无限重试；重连成功后计数会清零。",
                    "0 disables reconnect, -1 retries forever. The counter resets after a successful reconnect.")),

            Section("Socket 选项", "Socket",
                Text("noDelay", "禁用 Nagle 算法", "No Delay",
                    "开启后小包立即发送，降低延迟；适合实时互动与设备指令。",
                    "Sends small packets immediately to reduce latency; good for realtime interaction and device commands."),
                Text("keepAlive", "启用 KeepAlive", "Keep Alive"),
                Text("useTls", "启用 TLS", "Use TLS",
                    "正式环境建议开启；WebGL 平台无效。",
                    "Recommended in production; has no effect on WebGL."),
                Text("tlsServerName", "TLS 服务器名称", "TLS Server Name",
                    "TLS 证书中的主机名；留空时使用上面的目标地址。",
                    "Host name in the TLS certificate; falls back to Host when left empty."),
                Text("allowInvalidTlsCertificate", "允许无效 TLS 证书", "Allow Invalid TLS Certificate",
                    "仅本地调试可开启，生产环境必须关闭！",
                    "Local debugging only. Must stay off in production!")),

            Section("串口", "Serial",
                Text("serialPortName", "串口名称", "Serial Port Name",
                    "Windows 如 COM3，Linux 如 /dev/ttyUSB0。",
                    "Windows: COM3; Linux: /dev/ttyUSB0."),
                Text("serialBaudRate", "波特率", "Baud Rate"),
                Text("serialDataBits", "数据位", "Data Bits"),
                IntChoice("serialStopBits", "停止位", "Stop Bits",
                    new[] { "1 位", "2 位" },
                    new[] { "One", "Two" },
                    new[] { 1, 2 }),
                IntChoice("serialParity", "校验位", "Parity",
                    new[] { "无校验", "奇校验", "偶校验", "标记 (Mark)", "空格 (Space)" },
                    new[] { "None", "Odd", "Even", "Mark", "Space" },
                    new[] { 0, 1, 2, 3, 4 }),
                Text("serialDtrEnable", "启用 DTR", "DTR Enable"),
                Text("serialRtsEnable", "启用 RTS", "RTS Enable")),

            Section("UDP", "UDP",
                Text("udpEnableBroadcast", "启用 UDP 广播", "Enable UDP Broadcast",
                    "开启后目标地址可填 255.255.255.255 做局域网广播。",
                    "When enabled the target may be 255.255.255.255 for LAN broadcast.")),

            Section("WebSocket", "WebSocket",
                Text("webSocketUrl", "WebSocket 地址", "WebSocket URL",
                    "例如 ws://127.0.0.1:9002 或 wss://example.com/device。",
                    "e.g. ws://127.0.0.1:9002 or wss://example.com/device."),
                Text("webSocketSubProtocol", "子协议", "Sub Protocol"),
                Text("webSocketConnectTimeoutMs", "连接超时（毫秒）", "Connect Timeout (ms)"))
        };
    }
}
