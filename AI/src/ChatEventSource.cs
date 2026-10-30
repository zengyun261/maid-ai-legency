using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace AI;

/// <summary>
/// SSE 事件分发器：将 Agent 的所有输出事件推送给已连接的前端客户端
/// </summary>
public class ChatEventSource
{
    private readonly ConcurrentDictionary<string, Channel<string>> _clients = new();
    private readonly ConcurrentQueue<string> _history = new();
    private const int MaxHistory = 500;
    private int _clientId;

    /// <summary>订阅事件流，返回客户端 ID</summary>
    public string Subscribe()
    {
        var id = Interlocked.Increment(ref _clientId).ToString();
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        // 重放历史事件，让新连接的客户端看到之前的消息
        // 跳过一次性事件（如 shutdown），避免刷新后再次弹窗
        foreach (var msg in _history)
        {
            if (msg.StartsWith("event: shutdown\n")) continue;
            channel.Writer.TryWrite(msg);
        }

        _clients[id] = channel;
        return id;
    }

    /// <summary>取消订阅</summary>
    public void Unsubscribe(string id)
    {
        if (_clients.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    /// <summary>向所有客户端推送 SSE 事件（默认进入历史，新连接的客户端会重放）</summary>
    public Task EmitAsync(string type, object data) => EmitAsync(type, data, replay: true);

    /// <summary>
    /// 向所有客户端推送 SSE 事件。
    /// replay=false 时事件不进入历史队列，不会重放给后续连接的客户端（适合一次性弹窗类事件）。
    /// </summary>
    public async Task EmitAsync(string type, object data, bool replay)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        var sseMessage = $"event: {type}\ndata: {json}\n\n";

        if (replay)
        {
            _history.Enqueue(sseMessage);
            while (_history.Count > MaxHistory) _history.TryDequeue(out _);
        }

        var deadClients = new List<string>();
        foreach (var (id, channel) in _clients)
        {
            if (!channel.Writer.TryWrite(sseMessage))
            {
                deadClients.Add(id);
            }
        }

        // 清理已断开的客户端
        foreach (var id in deadClients)
        {
            _clients.TryRemove(id, out _);
        }
    }

    /// <summary>获取指定客户端的事件流</summary>
    public IAsyncEnumerable<string> GetEventStream(string clientId, CancellationToken ct)
    {
        if (_clients.TryGetValue(clientId, out var channel))
        {
            return channel.Reader.ReadAllAsync(ct);
        }
        return EmptyAsync(ct);
    }

    private static async IAsyncEnumerable<string> EmptyAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        yield break;
    }
}
