# UnityNetworkToolkit 使用手册

这是一个不依赖第三方网络库的 Unity 通信模块，提供：

- TCP 客户端：长度前缀/分隔符自动拆包，自动缓存半包、合并包和粘包数据。
- TCP 断线自动重连：可配置重试间隔、最大重试次数、连接超时和 KeepAlive。
- UDP：一个数据报对应一条消息，不需要 TCP 式拆包。
- WebSocket：支持 `ws://` / `wss://`、文本帧和二进制帧、自动重连。
- 串口：使用 `System.IO.Ports.SerialPort`，支持分隔符或长度前缀协议。
- `SendAsync` / `ReceiveAsync`：推荐用于正式业务。
- 同步 `Send` / `Receive`：只建议用于简单调试、编辑器工具或一次性小消息。
- `HexCodec`：解析/输出 `AA 01 FF`、`AA-01-FF`、`0xAA 0x01 0xFF` 等设备指令格式。
- `NetworkClient`：把后台接收线程的事件转回 Unity 主线程，避免在 socket 线程直接操作 Unity API。

## 1. 安装

将 `UnityNetworkToolkit` 文件夹复制到 Unity 工程的 `Assets/` 下，例如：

```text
Assets/
└── UnityNetworkToolkit/
    ├── Runtime/
    └── Examples/
```

Unity 会自动识别 `Runtime/UnityNetworkToolkit.asmdef`。建议 Unity 2021 LTS 及以上版本；Unity 6 LTS 可直接使用。

## 2. 创建配置文件

在 Project 窗口右键：

`Create → Networking → Network Config`

然后把配置资产拖到 `NetworkClient` 组件的 `Config Asset` 字段。

选中配置资产时，Inspector 面板顶部带有「中文 / English」切换按钮，点击后字段标题、分组标题和悬停提示会立即切换语言；TCP 协议、校验位、停止位等枚举字段也会显示为本地化下拉框。语言偏好保存在 Unity 的 EditorPrefs 中，不会写进配置资产，因此切换语言不会产生任何文件改动。

如果希望场景启动后自动连接，勾选 `Connect On Start` 并选择 `Start Transport`；也可以保持关闭，由业务脚本手动调用 `ConnectAsync`。

主要字段：

| 字段 | 说明 |
|---|---|
| Host | TCP/UDP 目标地址，默认 `127.0.0.1` |
| Tcp Port | TCP 目标端口 |
| Udp Port | UDP 目标端口 |
| Udp Local Port | 本地监听端口，`0` 表示系统自动分配 |
| Tcp Frame Protocol | TCP 消息协议，推荐 `LengthPrefixBigEndian` |
| Max Message Size | 单条消息最大字节数，防止异常长度导致内存风险 |
| Connect Timeout Ms | TCP 连接超时时间 |
| Auto Reconnect | 是否自动重连 |
| Reconnect Interval Ms | 两次重连间隔 |
| Max Reconnect Attempts | 最大重试次数；`0` 禁用，`-1` 无限重试 |
| Use Tls | TCP 使用 TLS；正式环境建议开启 |
| Tls Server Name | TLS 证书中的主机名，留空时使用 Host |
| Allow Invalid Tls Certificate | 仅本地调试可开启，生产环境必须关闭 |
| Use Serial Port | 串口名称、波特率等串口参数 |
| UDP Enable Broadcast | 允许 UDP 广播；目标地址可填写 `255.255.255.255` |
| Web Socket Url | WebSocket 地址，例如 `ws://127.0.0.1:9002` 或 `wss://example.com/device` |
| Web Socket Sub Protocol | 可选的 WebSocket 子协议 |
| Web Socket Connect Timeout Ms | WebSocket 握手超时时间 |

配置资产在运行时会被复制并校验，不会修改 Project 中的原始资产。

## 3. TCP 消息协议

TCP 是字节流，没有消息边界。默认协议为 4 字节大端序长度 + 消息体：

```text
00 00 00 05  →  后面有 5 个字节
hello        →  消息体
```

发送 `SendAsync(Encoding.UTF8.GetBytes("hello"))` 时，模块会自动加上长度头；接收时会自动处理：

- 一次只收到半个包；
- 一次收到多个完整包；
- 一个完整包和下一个包的一部分；
- 长度头与消息体跨多次读取。

`LengthPrefixLittleEndian` 适合部分工控设备的小端长度头；`DelimiterUtf8` 适合文本调试协议，例如分隔符设置为 `\n`。`Raw` 不提供消息边界，只适合上层已经自行定义边界的场景，正式 TCP 业务不推荐。

UDP 每个 datagram 天然是一条消息；UDP 不保证送达、顺序或不重复，可靠性需要业务层增加序号、确认、超时和重发。

## 4. 推荐调用方式

场景中放置一个 `NetworkClient` 组件，再通过代码连接：

```csharp
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Networking;
using UnityEngine;

public sealed class MyNetworkBehaviour : MonoBehaviour
{
    [SerializeField] private NetworkClient client;

    private async void Start()
    {
        client.StateChanged += (kind, state) => Debug.Log(kind + " => " + state);
        client.MessageReceived += (kind, data) =>
            Debug.Log("收到：" + Encoding.UTF8.GetString(data));
        client.Error += (kind, error) => Debug.LogException(error);

        try
        {
            await client.ConnectAsync(NetworkTransportKind.Tcp);
            await client.SendTextAsync("hello", CancellationToken.None);
            await client.SendHexAsync("AA 01 00 FF", CancellationToken.None);
        }
        catch (Exception error)
        {
            Debug.LogException(error);
        }
    }
}
```

`Examples/NetworkClientExample.cs` 还提供了可直接绑定 Unity UI Button 的 `Connect`、`SendTextMessage`、`SendHexMessage`、`SendWebSocketTextMessage`、`SendUdpMessage`、`SendSyncMessage` 和 `Disconnect` 方法。

事件会在 Unity 主线程的 `Update` 中触发，可以安全地更新 UI 或游戏对象。底层 `TcpClientTransport`、`UdpTransport`、`SerialPortTransport`、`WebSocketTransport` 的事件则可能来自后台线程，直接使用底层类时不要在事件中调用 Unity API。

## 5. ReceiveAsync 与同步 Receive

推荐业务使用：

```csharp
byte[] data = await client.ReceiveAsync(cancellationToken);
```

也可以只监听 `MessageReceived` 事件。两者读取的是同一个接收队列：如果先由 `ReceiveAsync` 取走，事件仍会收到同一条消息；如果不需要队列式消费，只订阅事件即可。

同步接口仅用于调试：

```csharp
client.Send(Encoding.UTF8.GetBytes("debug"));
byte[] data = client.Receive(1000); // 1000ms 超时，超时返回 null
```

不要在 Unity 主线程循环调用同步 `Receive`，否则会造成卡顿。不要对大文件或大消息直接使用一次性发送，建议分块并在业务层确认。

## 6. UDP 调用

配置 `Host`、`Udp Port`，然后：

```csharp
await client.ConnectAsync(NetworkTransportKind.Udp);
await client.SendAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
```

UDP 没有 TCP 重连语义；本模块会报告 socket 错误，但不会把 UDP 伪装成可靠协议。需要可靠传输时，请使用 TCP 或在应用层实现 ACK、序号、重试和去重。

已连接 UDP 后，也可以临时向另一个地址发送，适合广播或多设备展项：

```csharp
await client.SendUdpToAsync(HexCodec.Parse("AA 10 01"), "192.168.1.255", 9001, CancellationToken.None);
```

## 7. 十六进制工具

```csharp
byte[] command = HexCodec.Parse("0xAA 01-FF");
string display = HexCodec.ToHex(command); // AA 01 FF

if (HexCodec.TryParse(input, out byte[] data, out string error))
    await client.SendAsync(data, CancellationToken.None);
else
    Debug.LogError(error);
```

## 8. WebSocket 调用

配置 `Web Socket Url` 后即可连接。WebSocket 的文本帧和二进制帧都会统一通过 `MessageReceived` 事件作为 `byte[]` 返回；发送设备指令时默认使用二进制帧。

```csharp
await client.ConnectAsync(NetworkTransportKind.WebSocket);
await client.SendHexAsync("AA 01 00 FF", CancellationToken.None);
await client.SendWebSocketTextAsync("hello", CancellationToken.None);
```

`SendHexAsync` 和通用 `SendTextAsync` 会发送二进制帧；需要真正的 WebSocket 文本帧时使用 `SendWebSocketTextAsync`。服务端需要鉴权或协议协商时，可以配置 `Web Socket Sub Protocol`。正式部署建议使用 `wss://`，不要在公网关闭 TLS 或把敏感信息写入日志。

## 9. 串口调用

在配置资产中设置：

- `Serial Port Name`：Windows 如 `COM3`，Linux 如 `/dev/ttyUSB0`；
- `Serial Baud Rate`：常见 `9600` 或 `115200`；
- 数据位、停止位、校验位、DTR/RTS；
- 若设备按行通信，使用 `DelimiterUtf8` 并把分隔符设为 `\n` 或 `\r\n`。

然后：

```csharp
await client.ConnectAsync(NetworkTransportKind.Serial);
await client.SendTextAsync("AT\r\n", CancellationToken.None);
```

Android 串口通常需要 USB Host 权限、设备枚举和厂商 USB-Serial SDK；本实现提供 .NET `SerialPort` 路径，但如果目标设备不暴露标准串口，需另写原生 Android 适配器。iOS 也需要硬件厂商 SDK 或原生桥接。

## 10. WebGL 说明

浏览器安全模型不允许 Unity WebGL 直接创建原生 TCP、UDP socket，也不能直接访问 COM 串口。当前 `WebSocketTransport` 使用 .NET 原生客户端，因此在 WebGL 下也会抛出 `PlatformNotSupportedException`；如果目标是 WebGL，请接入浏览器 JavaScript WebSocket 插件或宿主层适配器。

WebGL 正确架构是：

```text
Unity WebGL --WebSocket/WSS--> WebSocket 网关 --TCP/UDP/串口--> 真实设备或服务器
```

生产环境推荐 `wss://`，并在网关上做鉴权、限流、协议转换。当前模块没有捆绑第三方 WebGL WebSocket 插件，避免锁定具体插件实现。

## 11. 安全与可靠性建议

当前模块已做连接超时、最大消息长度限制、发送串行化、TCP 半包/粘包处理、异常事件、可选 TLS 和自动重连。正式环境还应：

1. TCP 打开 `Use Tls`，WebSocket 使用 `wss://`，证书校验不要关闭；
2. 登录后给每个消息增加版本、请求 ID、时间戳和鉴权签名；
3. 对服务端下发数据做长度、类型和权限校验；
4. 不要把密码、Token、私钥写进 ScriptableObject 或客户端日志；
5. UDP 业务增加序号、ACK、超时和重放保护；
6. 断线重连采用有限次数或指数退避，避免大量客户端同时冲击服务器；
7. 对外网服务在服务端做连接数、消息频率和单 IP 限流。

## 12. 常见问题

### TCP 收到乱码

确认双方使用同一种编码；文本一般使用 UTF-8。确认服务端也使用 4 字节大端长度头，或把 `Tcp Frame Protocol` 改为双方约定的分隔符协议。

### TCP 一直重连

检查服务器是否监听正确网卡和端口、防火墙、Android 网络权限，以及配置中的 Host 是否可达。开发环境不要把服务端只绑定在 `127.0.0.1`，手机访问 PC 时应使用 PC 的局域网 IP。

### WebSocket 连接失败

确认 URL 使用 `ws://` 或 `wss://`，服务端路径、端口和子协议一致。浏览器或 WebGL 场景还需确认服务端允许对应的 Origin，并使用浏览器 WebSocket 适配器。

### `ReceiveAsync` 没有返回

它是等待下一条完整消息的异步方法。TCP 必须先收到完整长度前缀和消息体；建议传入 `CancellationTokenSource.CancelAfter(...)` 实现业务超时。

### Android 能连 Wi-Fi 但不能联网

检查 Android Manifest 的 `INTERNET` 权限；Unity 通常会自动加入，但自定义 Gradle/Manifest 时可能覆盖。Android 访问 PC 还需开放防火墙端口。

### WebGL 报不支持

这是浏览器平台限制，不是 TCP 脚本故障。接入 WebSocket 客户端和服务端网关，或把通信放在原生宿主层。

## 13. 文件结构

```text
UnityNetworkToolkit/
├── README.md
├── Runtime/
│   ├── NetworkClient.cs
│   ├── NetworkConfig.cs
│   ├── NetworkTransportBase.cs
│   ├── TcpClientTransport.cs
│   ├── UdpTransport.cs
│   ├── SerialPortTransport.cs
│   ├── WebSocketTransport.cs
│   ├── MessageFraming.cs
│   ├── HexCodec.cs
│   └── UnityNetworkToolkit.asmdef
├── Editor/
│   ├── NetworkConfigAssetEditor.cs
│   ├── NetworkConfigLocalization.cs
│   └── UnityNetworkToolkit.Editor.asmdef
└── Examples/
    └── NetworkClientExample.cs
```
