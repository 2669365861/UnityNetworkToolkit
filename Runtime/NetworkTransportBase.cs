using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Common.Networking
{
    /// <summary>
    /// 所有传输实现的抽象基类（TCP / UDP / 串口 / WebSocket 共用）。
    /// 统一负责三件事：
    /// 1. 保存一份独立的运行时配置副本并校验；
    /// 2. 用线程安全队列缓存收到的消息，供 ReceiveAsync / Receive 消费；
    /// 3. 以事件形式对外发布「状态变化 / 收到消息 / 出错」，并保证单个订阅者异常不会影响其他人。
    /// 注意：事件可能来自后台线程，直接使用本类时不要在事件里调用 Unity API。
    /// </summary>
    public abstract class NetworkTransportBase : IDisposable
    {
        private readonly AsyncMessageQueue incoming = new AsyncMessageQueue();
        private int disposed;

        /// <summary>
        /// 构造函数：克隆传入配置（避免外部后续修改影响运行中的连接），并完成参数校验。
        /// </summary>
        protected NetworkTransportBase(NetworkConfig config, NetworkTransportKind kind)
        {
            Config = config == null ? new NetworkConfig() : config.Clone();
            Config.Validate();
            Kind = kind;
        }

        public NetworkTransportKind Kind { get; private set; }
        public NetworkConfig Config { get; private set; }
        public NetworkConnectionState State { get; protected set; }
        public bool IsConnected { get { return State == NetworkConnectionState.Connected; } }

        public event Action<NetworkConnectionState> StateChanged;
        public event Action<byte[]> MessageReceived;
        public event Action<Exception> Error;

        /// <summary>建立连接。具体实现里可能包含重连循环。</summary>
        public abstract Task ConnectAsync();

        /// <summary>主动断开连接并释放底层句柄。</summary>
        public abstract Task DisconnectAsync();

        /// <summary>发送一条完整消息；拆包（加长度头/分隔符）由协议帧实现负责。</summary>
        public abstract Task SendAsync(byte[] payload, CancellationToken cancellationToken);

        /// <summary>
        /// 同步发送（阻塞等待发送完成）。
        /// 仅建议用于编辑器工具或一次性小消息；不要在 Unity 主线程频繁调用。
        /// </summary>
        public void Send(byte[] payload)
        {
            SendAsync(payload, CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>异步等待下一条完整消息（从内部接收队列取，超时或取消由 token 控制）。</summary>
        public Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
        {
            return incoming.ReceiveAsync(cancellationToken);
        }

        /// <summary>同步等待下一条消息；超过 timeoutMs 没有数据则返回 null。仅用于调试。</summary>
        public byte[] Receive(int timeoutMs = 1000)
        {
            return incoming.Receive(timeoutMs);
        }

        /// <summary>
        /// 收到一条完整消息后调用：先入队供 Receive 接口消费，再触发 MessageReceived 事件。
        /// 事件处理里抛出的异常会被捕获并转成 Error 事件，不会打断接收循环。
        /// </summary>
        protected void PublishMessage(byte[] payload)
        {
            if (payload == null) return;
            incoming.Enqueue(payload);
            Action<byte[]> handler = MessageReceived;
            if (handler != null)
            {
                try { handler(payload); }
                catch (Exception exception) { PublishError(exception); }
            }
        }

        /// <summary>更新并广播连接状态。订阅者抛出的异常同样会被兜底为 Error 事件。</summary>
        protected void PublishState(NetworkConnectionState state)
        {
            State = state;
            Action<NetworkConnectionState> handler = StateChanged;
            if (handler != null)
            {
                try { handler(state); }
                catch (Exception exception) { PublishError(exception); }
            }
        }

        /// <summary>广播错误事件；任意订阅者抛异常都会被吞掉，避免连锁失败。</summary>
        protected void PublishError(Exception exception)
        {
            Action<Exception> handler = Error;
            if (handler != null)
            {
                try { handler(exception); } catch { }
            }
        }

        protected bool IsDisposed { get { return Volatile.Read(ref disposed) != 0; } }

        /// <summary>
        /// 释放传输对象：保证只执行一次；若仍处于连接状态则先断开，最后关闭接收队列，
        /// 让正在等待 ReceiveAsync 的调用方立即收到异常而不是永久挂起。
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try
            {
                if (State != NetworkConnectionState.Disconnected)
                    DisconnectAsync().GetAwaiter().GetResult();
            }
            finally { incoming.Complete(); }
        }

        /// <summary>
        /// 内部接收队列：把「后台接收线程写入」和「业务线程读取」解耦。
        /// 用信号量做异步等待，用 volatile completed 标记关闭，避免读线程永久阻塞。
        /// </summary>
        private sealed class AsyncMessageQueue
        {
            private readonly ConcurrentQueue<byte[]> queue = new ConcurrentQueue<byte[]>();
            private readonly SemaphoreSlim signal = new SemaphoreSlim(0);
            private volatile bool completed;

            /// <summary>入队并唤醒一个等待中的读取者；队列已关闭时直接丢弃。</summary>
            public void Enqueue(byte[] item)
            {
                if (completed) return;
                queue.Enqueue(item);
                try { signal.Release(); } catch (ObjectDisposedException) { }
            }

            /// <summary>异步等待并取出下一条消息；队列关闭且无数据时抛出异常通知调用方。</summary>
            public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
            {
                while (true)
                {
                    if (queue.TryDequeue(out byte[] item)) return item;
                    if (completed) throw new InvalidOperationException("Transport receive queue is closed.");
                    await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            /// <summary>同步等待并取出下一条消息；超时返回 null。</summary>
            public byte[] Receive(int timeoutMs)
            {
                if (queue.TryDequeue(out byte[] item)) return item;
                if (!signal.Wait(timeoutMs)) return null;
                queue.TryDequeue(out item);
                return item;
            }

            /// <summary>关闭队列并唤醒所有等待者，让它们能感知到结束状态。</summary>
            public void Complete()
            {
                if (completed) return;
                completed = true;
                try { signal.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }
}
