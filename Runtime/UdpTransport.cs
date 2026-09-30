using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Networking
{
    /// <summary>
    /// UDP 传输。
    /// 一个数据报就是一条消息，因此没有 TCP 式的拆包逻辑，收到多少就抛出多少。
    /// 注意：UDP 不保证送达、顺序和不重复，可靠性需要业务层自己加序号/ACK/重发。
    /// </summary>
    public sealed class UdpTransport : NetworkTransportBase
    {
#if !UNITY_WEBGL
        private readonly object sync = new object();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private UdpClient client;
        private CancellationTokenSource lifetime;
        private Task receiveTask;
#endif

        public UdpTransport(NetworkConfig config) : base(config, NetworkTransportKind.Udp)
        {
            PublishState(NetworkConnectionState.Disconnected);
        }

        /// <summary>
        /// 打开 UDP 客户端并启动接收循环。
        /// udpLocalPort 大于 0 时绑定固定本地端口（设备需要回发时用），否则由系统随机分配。
        /// </summary>
        public override async Task ConnectAsync()
        {
#if UNITY_WEBGL
            throw new PlatformNotSupportedException("WebGL cannot open a raw UDP socket. Use a WebSocket gateway adapter.");
#else
            lock (sync)
            {
                if (receiveTask != null && !receiveTask.IsCompleted) return;
                lifetime = new CancellationTokenSource();
                client = Config.udpLocalPort > 0 ? new UdpClient(Config.udpLocalPort) : new UdpClient();
                client.Client.ReceiveBufferSize = Config.receiveBufferSize;
                client.Client.SendBufferSize = Config.sendBufferSize;
                client.EnableBroadcast = Config.udpEnableBroadcast;
                client.Connect(Config.host, Config.udpPort);
                receiveTask = ReceiveLoopAsync(client, lifetime.Token);
            }
            PublishState(NetworkConnectionState.Connected);
            await Task.CompletedTask;
#endif
        }

        /// <summary>取消接收循环并关闭 UdpClient，最后把状态置为 Disconnected。</summary>
        public override async Task DisconnectAsync()
        {
#if UNITY_WEBGL
            PublishState(NetworkConnectionState.Disconnected);
            await Task.CompletedTask;
#else
            CancellationTokenSource source;
            Task task;
            lock (sync)
            {
                source = lifetime;
                task = receiveTask;
                if (source != null) source.Cancel();
                try { if (client != null) client.Close(); } catch { }
                client = null;
            }
            if (task != null) try { await task.ConfigureAwait(false); } catch { }
            PublishState(NetworkConnectionState.Disconnected);
#endif
        }

        /// <summary>
        /// 向已连接的目标地址发送一个数据报。
        /// 超过 maxMessageSize 或超过 UDP 理论上限 65507 字节时直接拒绝，避免底层静默截断。
        /// </summary>
        public override async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (payload == null) throw new ArgumentNullException("payload");
#if UNITY_WEBGL
            throw new PlatformNotSupportedException("WebGL cannot send through a raw UDP socket. Use a WebSocket gateway adapter.");
#else
            if (payload.Length > Math.Min(Config.maxMessageSize, 65507)) throw new InvalidOperationException("UDP datagram exceeds the configured UDP limit.");
            UdpClient current = client;
            if (current == null || !IsConnected) throw new InvalidOperationException("UDP is not connected.");
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await current.SendAsync(payload, payload.Length).ConfigureAwait(false); }
            finally { sendGate.Release(); }
#endif
        }

        /// <summary>向指定地址发送一个 UDP 数据报，适合广播或临时目标。</summary>
        public async Task SendToAsync(byte[] payload, string host, int port, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (payload == null) throw new ArgumentNullException("payload");
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host is required.", "host");
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException("port");
#if UNITY_WEBGL
            throw new PlatformNotSupportedException("WebGL cannot send through a raw UDP socket. Use a WebSocket gateway adapter.");
#else
            if (payload.Length > Math.Min(Config.maxMessageSize, 65507)) throw new InvalidOperationException("UDP datagram exceeds the configured UDP limit.");
            UdpClient current = client;
            if (current == null || !IsConnected) throw new InvalidOperationException("UDP is not connected.");
            // 临时创建一个独立 sender，避免破坏已连接客户端的默认目标地址。
            UdpClient sender = new UdpClient();
            try
            {
                sender.EnableBroadcast = Config.udpEnableBroadcast;
                await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try { await sender.SendAsync(payload, payload.Length, host, port).ConfigureAwait(false); }
                finally { sendGate.Release(); }
            }
            finally { sender.Close(); }
#endif
        }

#if !UNITY_WEBGL
        /// <summary>
        /// 接收循环：不断等待数据报并 PublishMessage。
        /// 主动断开导致的 ObjectDisposedException / SocketException 会被忽略，
        /// 其他异常才作为 Error 上报，避免正常的关闭流程刷一堆错误日志。
        /// </summary>
        private async Task ReceiveLoopAsync(UdpClient current, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    UdpReceiveResult result = await current.ReceiveAsync().ConfigureAwait(false);
                    if (result.Buffer.Length <= Config.maxMessageSize) PublishMessage(result.Buffer);
                    else PublishError(new InvalidOperationException("UDP datagram exceeds maxMessageSize."));
                }
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) { PublishError(exception); }
        }
#endif
    }
}
