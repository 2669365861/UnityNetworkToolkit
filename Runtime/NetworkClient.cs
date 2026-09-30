using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Common.Networking
{
    /// <summary>
    /// Unity 入口组件：把后台线程收到的事件转回 Unity 主线程。
    /// 底层传输的事件可能来自 socket 线程，而 Unity API 只能在主线程调用，
    /// 因此这里统一把事件塞进 ConcurrentQueue，在 Update 里逐个分发。
    /// 业务脚本只跟本组件打交道即可，无需关心线程问题。
    /// </summary>
    public sealed class NetworkClient : MonoBehaviour
    {
        [SerializeField] private NetworkConfigAsset configAsset;
        [SerializeField] private bool connectOnStart;
        [SerializeField] private NetworkTransportKind startTransport = NetworkTransportKind.Tcp;

        // 主线程动作队列：存放从后台线程抛回来的事件回调
        private readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
        private NetworkTransportBase transport;
        private Action<NetworkConnectionState> stateHandler;
        private Action<byte[]> messageHandler;
        private Action<Exception> errorHandler;

        public event Action<NetworkTransportKind, NetworkConnectionState> StateChanged;
        public event Action<NetworkTransportKind, byte[]> MessageReceived;
        public event Action<NetworkTransportKind, Exception> Error;

        public NetworkTransportBase ActiveTransport { get { return transport; } }
        public NetworkConfigAsset ConfigAsset { get { return configAsset; } set { configAsset = value; } }
        public bool ConnectOnStart { get { return connectOnStart; } set { connectOnStart = value; } }
        public NetworkTransportKind StartTransport { get { return startTransport; } set { startTransport = value; } }

        /// <summary>若勾选了 Connect On Start，则场景启动后自动连接指定传输。</summary>
        private async void Start()
        {
            if (!connectOnStart) return;
            try { await ConnectAsync(startTransport); }
            catch (Exception exception) { Debug.LogException(exception, this); }
        }

        /// <summary>在 Unity 主线程里逐个执行后台线程排进来的回调，保证事件回调可安全操作 Unity API。</summary>
        private void Update()
        {
            while (mainThreadActions.TryDequeue(out Action action)) action();
        }

        /// <summary>
        /// 连接指定传输：先断开旧连接，再依据配置资产生成一份运行时配置副本，创建对应传输并订阅事件。
        /// 连接失败时会回滚（释放已创建的传输并重新抛出异常），避免残留半连接状态。
        /// </summary>
        public async Task ConnectAsync(NetworkTransportKind kind)
        {
            await DisconnectAsync().ConfigureAwait(false);
            NetworkConfig config = configAsset == null ? new NetworkConfig() : configAsset.CreateRuntimeConfig();
            transport = CreateTransport(kind, config);
            Subscribe(transport);
            try
            {
                await transport.ConnectAsync().ConfigureAwait(false);
            }
            catch
            {
                // 连接失败：清空状态、退订并释放，保证下次 Connect 是干净的开始
                NetworkTransportBase failed = transport;
                transport = null;
                Unsubscribe(failed);
                try { failed.Dispose(); } catch { }
                throw;
            }
        }

        /// <summary>断开并释放当前传输；重复调用安全（没有活动传输时直接返回）。</summary>
        public async Task DisconnectAsync()
        {
            NetworkTransportBase old = transport;
            transport = null;
            if (old == null) return;
            Unsubscribe(old);
            try { await old.DisconnectAsync().ConfigureAwait(false); }
            finally { old.Dispose(); }
        }

        /// <summary>发送原始字节；未连接时抛出 InvalidOperationException。</summary>
        public Task SendAsync(byte[] payload, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (transport == null) throw new InvalidOperationException("No active network transport.");
            return transport.SendAsync(payload, cancellationToken);
        }

        /// <summary>以 UTF-8 编码发送一段文本（按当前传输协议的默认帧类型，WebSocket 下为二进制帧）。</summary>
        public Task SendTextAsync(string text, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendAsync(Encoding.UTF8.GetBytes(text ?? string.Empty), cancellationToken);
        }

        /// <summary>发送 WebSocket 文本帧；当前传输不是 WebSocket 时抛出异常。</summary>
        public Task SendWebSocketTextAsync(string text, CancellationToken cancellationToken = default(CancellationToken))
        {
            WebSocketTransport webSocket = transport as WebSocketTransport;
            if (webSocket == null) throw new InvalidOperationException("The active transport is not WebSocket.");
            return webSocket.SendTextFrameAsync(text, cancellationToken);
        }

        /// <summary>发送十六进制指令，例如 "AA 01 00 FF"（内部用 HexCodec 解析）。</summary>
        public Task SendHexAsync(string hex, CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendAsync(HexCodec.Parse(hex), cancellationToken);
        }

        /// <summary>向指定地址发送 UDP 数据报（广播或多设备展项用）；仅 UDP 传输可用。</summary>
        public Task SendUdpToAsync(byte[] payload, string host, int port, CancellationToken cancellationToken = default(CancellationToken))
        {
            UdpTransport udp = transport as UdpTransport;
            if (udp == null) throw new InvalidOperationException("The active transport is not UDP.");
            return udp.SendToAsync(payload, host, port, cancellationToken);
        }

        /// <summary>同步发送，仅建议用于调试或编辑器工具。</summary>
        public void Send(byte[] payload)
        {
            if (transport == null) throw new InvalidOperationException("No active network transport.");
            transport.Send(payload);
        }

        /// <summary>同步接收下一条消息；超时返回 null。不要在 Unity 主线程循环调用。</summary>
        public byte[] Receive(int timeoutMs = 1000)
        {
            if (transport == null) throw new InvalidOperationException("No active network transport.");
            return transport.Receive(timeoutMs);
        }

        /// <summary>异步等待下一条完整消息，推荐在正式业务里使用。</summary>
        public Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (transport == null) throw new InvalidOperationException("No active network transport.");
            return transport.ReceiveAsync(cancellationToken);
        }

        /// <summary>按传输类型创建对应的传输实例。</summary>
        private static NetworkTransportBase CreateTransport(NetworkTransportKind kind, NetworkConfig config)
        {
            switch (kind)
            {
                case NetworkTransportKind.Udp: return new UdpTransport(config);
                case NetworkTransportKind.Serial: return new SerialPortTransport(config);
                case NetworkTransportKind.WebSocket: return new WebSocketTransport(config);
                default: return new TcpClientTransport(config);
            }
        }

        /// <summary>
        /// 订阅传输事件。这里保存委托引用，是为了 Unsubscribe 时能精确移除，
        /// 避免 lambda 无法退订导致的内存泄漏与重复回调。
        /// </summary>
        private void Subscribe(NetworkTransportBase value)
        {
            stateHandler = state => OnTransportStateChanged(value, state);
            messageHandler = payload => OnTransportMessageReceived(value, payload);
            errorHandler = exception => OnTransportError(value, exception);
            value.StateChanged += stateHandler;
            value.MessageReceived += messageHandler;
            value.Error += errorHandler;
        }

        /// <summary>退订传输事件并清空委托引用。</summary>
        private void Unsubscribe(NetworkTransportBase value)
        {
            if (stateHandler != null) value.StateChanged -= stateHandler;
            if (messageHandler != null) value.MessageReceived -= messageHandler;
            if (errorHandler != null) value.Error -= errorHandler;
            stateHandler = null;
            messageHandler = null;
            errorHandler = null;
        }

        private void OnTransportStateChanged(NetworkTransportBase source, NetworkConnectionState state)
        {
            EnqueueState(source.Kind, state);
        }

        private void OnTransportMessageReceived(NetworkTransportBase source, byte[] payload)
        {
            EnqueueMessage(source.Kind, payload);
        }

        private void OnTransportError(NetworkTransportBase source, Exception exception)
        {
            EnqueueError(source.Kind, exception);
        }

        /// <summary>把状态变化排进主线程队列（可能从后台线程调用）。</summary>
        private void EnqueueState(NetworkTransportKind kind, NetworkConnectionState state)
        {
            mainThreadActions.Enqueue(() =>
            {
                Action<NetworkTransportKind, NetworkConnectionState> handler = StateChanged;
                if (handler != null) handler(kind, state);
            });
        }

        /// <summary>把收到消息排进主线程队列，保证业务侧在 Unity 主线程处理数据。</summary>
        private void EnqueueMessage(NetworkTransportKind kind, byte[] payload)
        {
            mainThreadActions.Enqueue(() =>
            {
                Action<NetworkTransportKind, byte[]> handler = MessageReceived;
                if (handler != null) handler(kind, payload);
            });
        }

        /// <summary>把错误排进主线程队列。</summary>
        private void EnqueueError(NetworkTransportKind kind, Exception exception)
        {
            mainThreadActions.Enqueue(() =>
            {
                Action<NetworkTransportKind, Exception> handler = Error;
                if (handler != null) handler(kind, exception);
            });
        }

        /// <summary>组件销毁时直接释放底层传输（这里不再等待异步断开，避免对象已销毁还在跑回调）。</summary>
        private void OnDestroy()
        {
            if (transport != null)
            {
                try { transport.Dispose(); } catch (Exception exception) { Debug.LogException(exception); }
                transport = null;
            }
        }
    }
}
