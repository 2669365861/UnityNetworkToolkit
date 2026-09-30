using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Common.Networking;
using UnityEngine;

/// <summary>
/// NetworkClient 使用示例：订阅事件 → 连接 → 收发消息。
/// 事件回调都已被 NetworkClient 切回主线程，可以放心直接操作 UI 和游戏对象；
/// 也可以把这些方法直接挂到 UI Button 的 OnClick 上做手动联调。
/// </summary>
public sealed class NetworkClientExample : MonoBehaviour
{
    [SerializeField] private NetworkClient client;
    [SerializeField] private NetworkTransportKind transportKind = NetworkTransportKind.Tcp;

    /// <summary>启动时订阅事件并发起连接，然后发一条测试文本。</summary>
    private async void Start()
    {
        client.StateChanged += OnStateChanged;
        client.MessageReceived += OnMessageReceived;
        client.Error += OnError;

        try
        {
            await client.ConnectAsync(transportKind);
            await client.SendTextAsync("hello from Unity", CancellationToken.None);
            // 设备指令通常直接写十六进制：AA 01 00 FF
            // await client.SendHexAsync("AA 01 00 FF", CancellationToken.None);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    /// <summary>连接状态回调：已切回主线程，可安全更新 UI。</summary>
    private void OnStateChanged(NetworkTransportKind kind, NetworkConnectionState state)
    {
        Debug.Log("[Network] " + kind + " => " + state);
    }

    /// <summary>收到消息回调：同一份字节既按十六进制打印，也按 UTF-8 文本打印，方便对照协议。</summary>
    private void OnMessageReceived(NetworkTransportKind kind, byte[] data)
    {
        Debug.Log("[Network] " + kind + " received HEX: " + HexCodec.ToHex(data));
        Debug.Log("[Network] " + kind + " received TEXT: " + Encoding.UTF8.GetString(data));
    }

    /// <summary>错误回调：连接失败、协议异常等都会走这里。</summary>
    private void OnError(NetworkTransportKind kind, Exception exception)
    {
        Debug.LogError("[Network] " + kind + ": " + exception);
    }

    /// <summary>场景销毁时主动断开，避免后台线程在对象销毁后继续回调。</summary>
    private async void OnDestroy()
    {
        if (client == null) return;
        try { await client.DisconnectAsync(); } catch (Exception exception) { Debug.LogException(exception); }
    }
}
