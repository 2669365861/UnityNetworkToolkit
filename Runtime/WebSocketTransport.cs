using System;
using System.IO;
#if !UNITY_WEBGL
using System.Net.WebSockets;
using System.Text;
#endif
using System.Threading;
using System.Threading.Tasks;

namespace Common.Networking
{
    /// <summary>
    /// WebSocket 客户端传输。
    /// 二进制帧和文本帧都会以 UTF-8/原始字节形式抛出为 byte[] 消息。
    /// 发送侧默认走二进制帧；需要真正的文本帧时用 SendTextFrameAsync。
    /// 注意：WebGL 下 .NET 原生 ClientWebSocket 不可用，需要接浏览器 JS WebSocket 适配器。
    /// </summary>
    public sealed class WebSocketTransport : NetworkTransportBase
    {
#if !UNITY_WEBGL
        private readonly object sync = new object();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private ClientWebSocket socket;
        private CancellationTokenSource lifetime;
        private Task runTask;
        private bool manualDisconnect;
#endif

        public WebSocketTransport(NetworkConfig config) : base(config, NetworkTransportKind.WebSocket)
        {
            PublishState(NetworkConnectionState.Disconnected);
        }

        /// <summary>
        /// 启动连接：首次调用拉起后台循环（连接 + 读取 + 自动重连），
        /// 重复调用复用已有循环，返回的 Task 会一直等到连上或确认失败。
        /// </summary>
        public override Task ConnectAsync()
        {
#if UNITY_WEBGL
            return Task.FromException(new PlatformNotSupportedException(
                "Use a browser JavaScript WebSocket adapter for Unity WebGL."));
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

        /// <summary>主动断开：标记手动断开、取消后台循环、释放 socket，再等待循环退出。</summary>
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

        /// <summary>通用发送入口：以二进制帧发送一条消息。</summary>
        public override Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (payload == null) throw new ArgumentNullException("payload");
#if UNITY_WEBGL
            throw new PlatformNotSupportedException(
                "Use a browser JavaScript WebSocket adapter for Unity WebGL.");
#else
            return SendFrameAsync(payload, WebSocketMessageType.Binary, cancellationToken);
#endif
        }

        /// <summary>发送一个真正的 WebSocket 文本帧；SendTextAsync 走统一 byte[] 接口并发送二进制帧。</summary>
        public Task SendTextFrameAsync(string text, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (text == null) text = string.Empty;
#if UNITY_WEBGL
            throw new PlatformNotSupportedException(
                "Use a browser JavaScript WebSocket adapter for Unity WebGL.");
#else
            return SendFrameAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, cancellationToken);
#endif
        }

#if !UNITY_WEBGL
        /// <summary>
        /// 实际的帧发送实现：长度校验 + 信号量串行化 + 单帧完整发送（endOfMessage = true）。
        /// </summary>
        private async Task SendFrameAsync(byte[] payload, WebSocketMessageType messageType, CancellationToken cancellationToken)
        {
            if (payload.Length > Config.maxMessageSize)
                throw new InvalidOperationException("WebSocket message exceeds maxMessageSize.");

            ClientWebSocket current = socket;
            if (current == null || current.State != WebSocketState.Open)
                throw new InvalidOperationException("WebSocket is not connected.");

            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ArraySegment<byte> segment = new ArraySegment<byte>(payload);
                await current.SendAsync(segment, messageType, true, cancellationToken).ConfigureAwait(false);
            }
            finally { sendGate.Release(); }
        }

        /// <summary>轮询等待握手完成；循环结束仍未连上则抛出异常。</summary>
        private async Task WaitUntilConnectedAsync()
        {
            while (!IsConnected && runTask != null && !runTask.IsCompleted)
                await Task.Delay(20).ConfigureAwait(false);
            if (!IsConnected)
                throw new InvalidOperationException("WebSocket connection failed or was closed.");
        }

        /// <summary>
        /// 后台主循环：连接 → 读取，失败后按「手动断开 / 是否开启自动重连 / 剩余重试次数」决定退出或延时重连。
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
        /// 校验 URL 合法性（必须是 ws:// 或 wss://），按需带上子协议，
        /// 并用 CancelAfter 实现握手超时；超时会 Abort 掉 socket 并抛出 TimeoutException。
        /// </summary>
        private async Task OpenSocketAsync(CancellationToken cancellationToken)
        {
            Uri uri;
            if (!Uri.TryCreate(Config.webSocketUrl, UriKind.Absolute, out uri) ||
                (uri.Scheme != "ws" && uri.Scheme != "wss"))
                throw new InvalidOperationException("Invalid WebSocket URL: " + Config.webSocketUrl);

            ClientWebSocket next = new ClientWebSocket();
            if (!string.IsNullOrWhiteSpace(Config.webSocketSubProtocol))
                next.Options.AddSubProtocol(Config.webSocketSubProtocol.Trim());

            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(Config.webSocketConnectTimeoutMs);
                try { await next.ConnectAsync(uri, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    next.Abort();
                    next.Dispose();
                    throw new TimeoutException("WebSocket connect timed out.");
                }
                catch
                {
                    next.Abort();
                    next.Dispose();
                    throw;
                }
            }

            lock (sync) { socket = next; }
        }

        /// <summary>
        /// 读取循环：一个 WebSocket 消息可能被拆成多帧返回，
        /// 因此用 MemoryStream 累积分片，只有收到 EndOfMessage 才作为一条完整消息抛出。
        /// 收到 Close 帧视为对端正常关闭，抛异常交给外层触发重连。
        /// </summary>
        private async Task ReadLoopAsync(CancellationToken cancellationToken)
        {
            ClientWebSocket current = socket;
            byte[] buffer = new byte[Config.receiveBufferSize];
            MemoryStream message = null;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    WebSocketReceiveResult result = await current.ReceiveAsync(
                        new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        try
                        {
                            await current.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,
                                "", CancellationToken.None).ConfigureAwait(false);
                        }
                        catch { }
                        throw new EndOfStreamException("WebSocket peer closed the connection.");
                    }

                    if (message == null)
                    {
                        message = new MemoryStream(Math.Min(Config.maxMessageSize, buffer.Length));
                    }

                    if (message.Length + result.Count > Config.maxMessageSize)
                        throw new InvalidDataException("WebSocket message exceeds maxMessageSize.");
                    message.Write(buffer, 0, result.Count);

                    if (result.EndOfMessage)
                    {
                        byte[] payload = message.ToArray();
                        message.Dispose();
                        message = null;
                        // 文本帧的内容本身就是 UTF-8 字节，和二进制帧统一交给上层。
                        PublishMessage(payload);
                    }
                }
            }
            finally
            {
                if (message != null) message.Dispose();
            }
        }

        /// <summary>中止并释放当前 socket（重复调用安全）。</summary>
        private void CloseSocket()
        {
            lock (sync)
            {
                if (socket == null) return;
                try { socket.Abort(); } catch { }
                try { socket.Dispose(); } catch { }
                socket = null;
            }
        }
#endif
    }
}
