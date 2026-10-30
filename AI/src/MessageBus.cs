using System.Collections.Concurrent;

namespace AI;

/// <summary>
/// 用户消息（可包含图片引用）
/// </summary>
public class UserMessage
{
    public string Text { get; set; } = "";
    /// <summary>图片 ID（图片已存本地，注入对话历史时按 ID 加载字节）</summary>
    public string? ImageId { get; set; }
}

/// <summary>
/// 消息总线：线程安全的消息队列 + 唤醒信号
/// </summary>
public class MessageBus
{
    private readonly ConcurrentQueue<UserMessage> _messages = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    /// <summary>推送一条消息并立即唤醒</summary>
    public void Post(UserMessage msg)
    {
        _messages.Enqueue(msg);
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 信号量已满，说明已有唤醒信号，忽略
        }
    }

    /// <summary>
    /// 等待休眠超时或收到消息唤醒
    /// </summary>
    public async Task WaitAsync(int timeoutSec, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

        try
        {
            await _wakeSignal.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // 超时或外部取消，正常退出
        }
    }

    /// <summary>取消当前等待（外部调用时无新消息内容）</summary>
    public void WakeUp()
    {
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // 信号量已满，说明已有唤醒信号，忽略
        }
    }

    /// <summary>消费所有已堆积的消息，并清空残留的唤醒信号</summary>
    public UserMessage[] DrainMessages()
    {
        var msgs = new List<UserMessage>();
        while (_messages.TryDequeue(out var msg))
        {
            msgs.Add(msg);
        }
        // 清空残留信号，防止下一轮 WaitAsync 虚假唤醒
        while (_wakeSignal.Wait(0)) { }
        return msgs.ToArray();
    }

    /// <summary>清空所有消息和信号</summary>
    public void Clear()
    {
        while (_messages.TryDequeue(out _)) { }
        while (_wakeSignal.Wait(0)) { }
    }
}
