using System;
#if !UNITY_WEBGL
using System.IO.Ports;
#endif
using System.Threading;
using System.Threading.Tasks;

namespace Common.Networking
{
    /// <summary>
    /// 串口传输（基于 System.IO.Ports.SerialPort）。
    /// 支持长度前缀或分隔符两种拆包方式，适合下位机/灯控/雷达等设备通信。
    /// 注意：端口名在 Windows 形如 COM3，Linux 形如 /dev/ttyUSB0；
    /// Android / iOS 通常需要厂商 USB-Serial SDK，本实现只覆盖标准 SerialPort 路径。
    /// </summary>
    public sealed class SerialPortTransport : NetworkTransportBase
    {
#if !UNITY_WEBGL
        private readonly object sync = new object();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private readonly IMessageFramer framer;
        private SerialPort port;
        private CancellationTokenSource lifetime;
        private Task receiveTask;
#endif

        public SerialPortTransport(NetworkConfig config) : base(config, NetworkTransportKind.Serial)
        {
#if !UNITY_WEBGL
            framer = MessageFramerFactory.Create(config);
#endif
            PublishState(NetworkConnectionState.Disconnected);
        }

        /// <summary>
        /// 按配置打开串口（端口名、波特率、校验位、停止位、数据位、DTR/RTS），
        /// 并把读超时设为 receivePollTimeoutMs，使接收循环能定期让出线程。
        /// </summary>
        public override async Task ConnectAsync()
        {
#if UNITY_WEBGL
            throw new PlatformNotSupportedException("WebGL cannot access a serial port directly.");
#else
            lock (sync)
            {
                if (receiveTask != null && !receiveTask.IsCompleted) return;
                port = new SerialPort(Config.serialPortName, Config.serialBaudRate, (Parity)Config.serialParity, Config.serialDataBits,
                    Config.serialStopBits == 2 ? StopBits.Two : StopBits.One);
                port.ReadTimeout = Config.receivePollTimeoutMs;
                port.DtrEnable = Config.serialDtrEnable;
                port.RtsEnable = Config.serialRtsEnable;
                port.Open();
                lifetime = new CancellationTokenSource();
                receiveTask = ReceiveLoopAsync(lifetime.Token);
            }
            PublishState(NetworkConnectionState.Connected);
            await Task.CompletedTask;
#endif
        }

        /// <summary>取消接收循环、关闭串口句柄，最后把状态置为 Disconnected。</summary>
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
                try { if (port != null && port.IsOpen) port.Close(); } catch { }
                port = null;
            }
            if (task != null) try { await task.ConfigureAwait(false); } catch { }
            PublishState(NetworkConnectionState.Disconnected);
#endif
        }

        /// <summary>
        /// 发送一条消息：先经拆包器编码，再用信号量串行化写入。
        /// SerialPort.Write 是同步阻塞调用，这里放到线程池执行，避免卡住调用线程。
        /// </summary>
        public override async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            if (payload == null) throw new ArgumentNullException("payload");
#if UNITY_WEBGL
            throw new PlatformNotSupportedException("WebGL cannot access a serial port directly.");
#else
            if (!IsConnected || port == null) throw new InvalidOperationException("Serial port is not connected.");
            byte[] packet = framer.Encode(payload);
            await sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() => port.Write(packet, 0, packet.Length), cancellationToken).ConfigureAwait(false);
            }
            finally { sendGate.Release(); }
#endif
        }

#if !UNITY_WEBGL
        /// <summary>
        /// 接收循环：阻塞读取 + 超时继续轮询的组合。
        /// 读到数据就交给拆包器；读超时属于正常空闲，直接 continue 而不是报错。
        /// 非取消状态下的异常才上报 Error 并置为 Faulted。
        /// </summary>
        private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
        {
            byte[] buffer = new byte[Config.receiveBufferSize];
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read;
                    try { read = port.Read(buffer, 0, buffer.Length); }
                    catch (TimeoutException) { continue; }
                    if (read > 0) framer.Append(buffer, 0, read, PublishMessage);
                    await Task.Yield();
                }
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                PublishError(exception);
                PublishState(NetworkConnectionState.Faulted);
            }
        }
#endif
    }
}
