using System;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Networking
{
    /// <summary>
    /// TCP 客户端传输。
    /// 特点：后台循环负责「连接 → 读取 → 断线自动重连」，发送侧用信号量串行化，
    /// 拆包交给 IMessageFramer，因此上层拿到的永远是完整的业务消息。
    /// 注意：WebGL 平台无法创建原生 TCP socket，相关方法会直接抛出 PlatformNotSupportedException。
    /// </summary>
    public sealed class TcpClientTransport : NetworkTransportBase
    {
#if !UNITY_WEBGL
        private readonly object sync = new object();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly IMessageFramer framer;
        private TcpClient client;
        private Stream stream;
        private CancellationTokenSource lifetime;
        private Task runTask;
        private bool manualDisconnect;
#endif

        /// <summary>构造时按配置创建对应的拆包器，并把状态初始化为未连接。</summary>
        public TcpClientTransport(NetworkConfig config) : base(config, NetworkTransportKind.Tcp)
        {
#if !UNITY_WEBGL
            framer = MessageFramerFactory.Create(Config);
#endif
            PublishState(NetworkConnectionState.Disconnected);
        }

        /// <summary>
        /// 启动连接：首次调用会拉起后台运行循环（RunAsync），重复调用则复用已有循环。
        /// 返回的 Task 会一直等待到真正连上，或确认连接失败为止。
        /// </summary>
        public override Task ConnectAsync()
        {
#if UNITY_WEBGL
            return Task.FromException(new PlatformNotSupportedException("WebGL cannot open a raw TCP socket. Use a WebSocket gateway adapter."));
#else
            lock (sync)
            {
                if (runTask != null && !runTask.IsCompleted) return WaitUntilConnectedAsync();
                manualDisconnect = false;
                lifetime = new CancellationTokenSource();
                runTask = RunAsync(lifetime.Token);
            }
            return WaitUntilConnectedAsync();
#endif
        }

        /// <summary>
        /// 主动断开：标记为手动断开（阻止自动重连）、取消后台循环、关闭 socket，
        /// 并等待后台循环真正退出后再把状态置为 Disconnected。
        /// </summary>
        public override async Task DisconnectAsync()
        {
#if UNITY_WEBGL
            PublishState(NetworkConnectionState.Disconnected);
            await Task.CompletedTask;
#else
            Task task;
            lock (sync)
            {
                manualDisconnect = true;
                if (lifetime != null) lifetime.Cancel();
                CloseSocket();
                task = runTask;
            }
            if (task != null)
            {
                try { await task.ConfigureAwait(false); } catch { }
            }
            PublishState(NetworkConnectionState.Disconnected);
#endif
        }

        /// <summary>
        /// 发送一条消息：先用拆包协议编码（例如加 4 字节长度头），
        /// 再用信号量保证同一时刻只有一个发送流程，最后按 64KB 分块写完并 Flush。
        /// </summary>
        public override async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (payload == null) throw new ArgumentNullException("payload");
#if UNITY_WEBGL
            throw new PlatformNotSupportedException("WebGL cannot send through a raw TCP socket. Use a WebSocket gateway adapter.");
#else
            Stream currentStream = stream;
            if (currentStream == null || !IsConnected) throw new InvalidOperationException("TCP is not connected.");
            byte[] packet = framer.Encode(payload);
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                int offset = 0;
                while (offset < packet.Length)
                {
                    int count = Math.Min(packet.Length - offset, 64 * 1024);
                    await currentStream.WriteAsync(packet, offset, count, cancellationToken).ConfigureAwait(false);
                    offset += count;
                }
                await currentStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { sendGate.Release(); }
#endif
        }

#if !UNITY_WEBGL
        /// <summary>
        /// 轮询等待后台循环把状态置为 Connected。
        /// 循环结束仍没连上时抛出异常，避免调用方拿到一个「假装成功」的 Task。
        /// </summary>
        private async Task WaitUntilConnectedAsync()
        {
            while (!IsConnected && runTask != null && !runTask.IsCompleted)
                await Task.Delay(20).ConfigureAwait(false);
            if (!IsConnected)
                throw new InvalidOperationException("TCP connection failed or was closed.");
        }

        /// <summary>
        /// 后台主循环：连接 → 读取，任一步失败都走统一的异常处理。
        /// 失败后根据 manualDisconnect / autoReconnect / 剩余重试次数决定「断开」还是「延时重连」；
        /// 一旦重连成功，重试计数清零，保证长期运行中偶发断线不会耗尽重试额度。
        /// </summary>
        private async Task RunAsync(CancellationToken cancellationToken)
        {
            int reconnectAttempts = 0;
            bool everConnected = false;
            Exception lastError = null;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    PublishState(everConnected ? NetworkConnectionState.Reconnecting : NetworkConnectionState.Connecting);
                    await OpenSocketAsync(cancellationToken).ConfigureAwait(false);
                    reconnectAttempts = 0;
                    everConnected = true;
                    PublishState(NetworkConnectionState.Connected);
                    await ReadLoopAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    lastError = exception;
                    PublishError(exception);
                    CloseSocket();
                    if (manualDisconnect || !Config.autoReconnect || !CanRetry(reconnectAttempts)) break;
                    reconnectAttempts++;
                    PublishState(NetworkConnectionState.Reconnecting);
                    try { await Task.Delay(Config.reconnectIntervalMs, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            CloseSocket();
            // 因异常退出才标记为 Faulted；正常断开（手动/取消）就是 Disconnected。
            if (lastError != null && State != NetworkConnectionState.Disconnected)
                PublishState(NetworkConnectionState.Faulted);
            else
                PublishState(NetworkConnectionState.Disconnected);
        }

        /// <summary>是否还允许重连：-1 表示无限重试，否则判断是否达到最大次数。</summary>
        private bool CanRetry(int attempts)
        {
            return Config.maxReconnectAttempts < 0 || attempts < Config.maxReconnectAttempts;
        }

        /// <summary>
        /// 建立一次 TCP 连接并完成可选 TLS 握手。
        /// 连接阶段用 Task.WhenAny 与超时 Task 竞争，避免 ConnectAsync 永久挂起；
        /// 成功后重置拆包器缓冲区，防止上一次连接的半包残留污染本次数据。
        /// </summary>
        private async Task OpenSocketAsync(CancellationToken cancellationToken)
        {
            TcpClient next = new TcpClient();
            next.NoDelay = Config.noDelay;
            next.ReceiveBufferSize = Config.receiveBufferSize;
            next.SendBufferSize = Config.sendBufferSize;
            if (Config.keepAlive) next.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            Task connectTask = next.ConnectAsync(Config.host, Config.tcpPort);
            Task timeoutTask = Task.Delay(Config.connectTimeoutMs, cancellationToken);
            Task completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
            if (completed != connectTask)
            {
                cancellationToken.ThrowIfCancellationRequested();
                next.Close();
                throw new TimeoutException("TCP connect timed out.");
            }
            await connectTask.ConfigureAwait(false);

            lock (sync) { client = next; }

            try
            {
                Stream nextStream = next.GetStream();
                if (Config.useTls)
                {
                    SslStream sslStream = new SslStream(nextStream, false, ValidateServerCertificate);
                    string targetHost = string.IsNullOrWhiteSpace(Config.tlsServerName) ? Config.host : Config.tlsServerName;
                    await sslStream.AuthenticateAsClientAsync(targetHost).ConfigureAwait(false);
                    nextStream = sslStream;
                }
                lock (sync)
                {
                    stream = nextStream;
                    framer.Reset();
                }
            }
            catch
            {
                CloseSocket();
                throw;
            }
        }

        /// <summary>
        /// 读取循环：每次读取到的字节片段都喂给拆包器，由拆包器负责处理半包/粘包，
        /// 只有凑齐一条完整消息才会通过 PublishMessage 抛给上层。
        /// 读到 0 字节说明对端正常关闭，抛异常交给外层触发重连。
        /// </summary>
        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[Config.receiveBufferSize];
            while (!cancellationToken.IsCancellationRequested)
            {
                Stream currentStream = stream;
                if (currentStream == null) throw new IOException("TCP stream is unavailable.");
                int read = await currentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("TCP peer closed the connection.");
                framer.Append(buffer, 0, read, PublishMessage);
            }
        }

        /// <summary>关闭并清空当前 socket/流句柄（重复调用安全）。</summary>
        private void CloseSocket()
        {
            lock (sync)
            {
                try { if (stream != null) stream.Close(); } catch { }
                try { if (client != null) client.Close(); } catch { }
                stream = null;
                client = null;
            }
        }

        /// <summary>
        /// TLS 证书校验回调：默认要求证书完全有效；
        /// 只有显式打开 allowInvalidTlsCertificate（仅限本地调试）才放过自签名/无效证书。
        /// </summary>
        private bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain,
            SslPolicyErrors sslPolicyErrors)
        {
            return sslPolicyErrors == SslPolicyErrors.None || Config.allowInvalidTlsCertificate;
        }
#endif
    }
}
