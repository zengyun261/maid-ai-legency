using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AI;

#pragma warning disable MAAI001
/// <summary>
/// 后台循环服务：管理 Agent 运行、消息处理、压缩协调
/// 会话持久化委托给 SessionStore
/// </summary>
public class MaidService : IHostedService
{
    private readonly MessageBus _messageBus;
    private readonly SleepState _sleepState;
    private readonly MaidAgent _maidAgent;
    private readonly ChatEventSource _eventSource;
    private readonly ImageStore _imageStore;
    private readonly SessionStore _sessionStore;
    private readonly ILogger<MaidService> _logger;
    private AIAgent? _agent;
    private AgentSession? _session;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    private bool _running;

    // 压缩锁：压缩期间持有，Agent 循环与消息注入需先等锁释放再继续
    private readonly SemaphoreSlim _compactionLock = new(1, 1);

    // 本轮 Agent 是否调用过 WaitForMessage（用于检测漏调，避免空转 LLM 调用）
    private volatile bool _waitCalledThisTurn;

    public bool IsRunning => _running;

    /// <summary>标记本轮已调用 WaitForMessage（供工具调用）</summary>
    public void MarkWaitCalled() => _waitCalledThisTurn = true;

    /// <summary>当前循环的取消令牌，供工具（如 WaitForMessage）感知关闭信号</summary>
    public CancellationToken LoopToken => _loopCts?.Token ?? CancellationToken.None;

    public MaidService(
        MessageBus messageBus,
        SleepState sleepState,
        MaidAgent maidAgent,
        ChatEventSource eventSource,
        ImageStore imageStore,
        SessionStore sessionStore, ILogger<MaidService> logger)
    {
        _messageBus = messageBus;
        _sleepState = sleepState;
        _maidAgent = maidAgent;
        _eventSource = eventSource;
        _imageStore = imageStore;
        _sessionStore = sessionStore;
        _logger = logger;
    }

    /// <summary>以 User 角色将消息追加到 session 对话历史（图片作为内容直接注入）</summary>
    [Experimental("MAAI001")]
    public async Task InjectUserMessages(IEnumerable<UserMessage> messages)
    {
        await _maidAgent.MessageInjector.EnqueueMessagesAsync(_session, messages.Select(ToChatMessage));
    }

    private ChatMessage ToChatMessage(UserMessage m)
    {
        if (m.ImageId != null && _imageStore.Load(m.ImageId) is { } bytes)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(m.Text))
            {
                contents.Add(new TextContent(m.Text));
            }

            contents.Add(new DataContent(bytes, "image/png"));
            return new ChatMessage(ChatRole.User, contents);
        }

        return new ChatMessage(ChatRole.User, m.Text);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 只初始化 Agent，不启动循环
        _agent = _maidAgent.Agent;
        _session = await _agent.CreateSessionAsync(cancellationToken: cancellationToken)
                   ?? throw new InvalidOperationException("Failed to create agent session.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _loopCts?.Cancel();
        if (_loopTask != null)
        {
            try
            {
                await _loopTask.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>启动 Agent 循环（重建 session，清空上下文）</summary>
    public async Task StartLoopAsync()
    {
        // 若循环在运行，先停止并等待退出，避免新旧循环并发
        if (_running)
        {
            _loopCts?.Cancel();
            _running = false;
            await _eventSource.EmitAsync("loop_stopped", new { });
            await WaitForLoopExitAsync();
        }

        _running = true;
        _loopCts = new CancellationTokenSource();

        _maidAgent.ReloadAgent();
        _agent = _maidAgent.Agent;

        _session = await _agent!.CreateSessionAsync()
                   ?? throw new InvalidOperationException("Failed to create agent session.");

        _messageBus.Clear();

        await _eventSource.EmitAsync("loop_started", new { });
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    /// <summary>停止 Agent 循环</summary>
    public async Task StopLoopAsync()
    {
        if (!_running) return;
        _loopCts?.Cancel();
        _running = false;
        await _eventSource.EmitAsync("loop_stopped", new { });
        await WaitForLoopExitAsync();
    }

    /// <summary>等待循环任务退出，确保不会与新一轮循环并发</summary>
    private async Task WaitForLoopExitAsync()
    {
        if (_loopTask == null) return;
        try
        {
            await _loopTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("等待循环退出超时");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "循环任务异常退出");
        }
        _loopTask = null;
    }

    /// <summary>外部推送消息（HTTP 接口调用）</summary>
    public void PostMessage(UserMessage msg) => _messageBus.Post(msg);

    /// <summary>唤醒等待中的 Agent；若压缩进行中，唤醒信号会被 MessageBus 缓冲，压缩结束后自然消费</summary>
    public void SafeWakeUp() => _messageBus.WakeUp();

    /// <summary>等待压缩结束（供 WaitForMessage 工具调用）</summary>
    public async Task WaitForCompactionAsync()
    {
        await _compactionLock.WaitAsync();
        _compactionLock.Release();
    }

    /// <summary>手动压缩上下文（压缩期间阻止 Agent 新一轮生成）</summary>
    [Experimental("MAAI001")]
    public async Task CompactAsync()
    {
        if (_session == null) return;
        await _compactionLock.WaitAsync();
        try
        {
            // 压缩是破坏性操作：先保存压缩前的状态，再压缩，再覆盖主文件
            await _sessionStore.SaveAsync(_session);
            await _maidAgent.CompactSessionAsync(_session, 30);
            await _sessionStore.SaveAsync(_session);
            await _eventSource.EmitAsync("compacted", new { });
        }
        finally
        {
            _compactionLock.Release();
        }
    }

    /// <summary>保存当前会话到 session.json</summary>
    public async Task SaveSessionAsync()
    {
        if (_session == null) return;
        await _sessionStore.SaveAsync(_session);
    }

    /// <summary>从 session.json 恢复会话并启动循环</summary>
    public async Task RestoreSessionAsync()
    {
        // 若循环在运行，先停止并等待退出，避免新旧循环并发
        if (_running)
        {
            _loopCts?.Cancel();
            _running = false;
            await _eventSource.EmitAsync("loop_stopped", new { });
            await WaitForLoopExitAsync();
        }

        _maidAgent.ReloadAgent();
        _agent = _maidAgent.Agent;
        _session = await _sessionStore.LoadAsync();
        if (_session == null) return;

        await StartLoopInternal();
    }

    /// <summary>从 session.json 恢复会话，压缩后启动循环</summary>
    public async Task CompactAndRestoreAsync()
    {
        // 若循环在运行，先停止并等待退出，避免新旧循环并发
        if (_running)
        {
            _loopCts?.Cancel();
            _running = false;
            await _eventSource.EmitAsync("loop_stopped", new { });
            await WaitForLoopExitAsync();
        }

        _maidAgent.ReloadAgent();
        _agent = _maidAgent.Agent;
        _session = await _sessionStore.LoadAsync();
        if (_session == null) return;

        await _compactionLock.WaitAsync();
        try
        {
            // 压缩前当前主文件即为压缩前状态，已有快照逻辑兜底；压缩后保存新状态
            await _maidAgent.CompactSessionAsync(_session, 5);
            await _sessionStore.SaveAsync(_session);
            await _eventSource.EmitAsync("compacted", new { });
        }
        finally
        {
            _compactionLock.Release();
        }

        await StartLoopInternal();
    }

    /// <summary>启动循环的公共逻辑（恢复会话路径复用）</summary>
    private async Task StartLoopInternal()
    {
        _running = true;
        _loopCts = new CancellationTokenSource();
        _messageBus.Clear();

        await _eventSource.EmitAsync("loop_started", new { });
        _loopTask = Task.Run(() => LoopAsync(_loopCts.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        bool isFirstMessage = true;
        bool needShutdown = false;

        while (!token.IsCancellationRequested && !needShutdown)
        {
            // 等待压缩结束再开始新一轮（压缩期间锁被持有，此处会阻塞）
            await _compactionLock.WaitAsync(token);
            _compactionLock.Release();

            if (!string.IsNullOrEmpty(_sleepState.Remark))
            {
                _sleepState.Remark = "";
            }

            await _eventSource.EmitAsync("turn_start", new { });
            _waitCalledThisTurn = false;

            try
            {
                var pendingMessages = new List<ChatMessage>();
                if (isFirstMessage)
                {
                    isFirstMessage = false;
                    var interval = "";
                    
                    if (File.Exists(DataPaths.TimestampFile))
                    {
                        var lastTime = File.GetLastWriteTimeUtc(DataPaths.TimestampFile).ToLocalTime();
                        var d = DateTime.Now.Subtract(lastTime);
                        interval = $"，距离上次交互已过去{(int)d.TotalHours}小时{d.Minutes}分钟。";
                        
                    }
                    
                    pendingMessages.Add(new ChatMessage(ChatRole.System, $"[系统消息]")//：上线了，当前时间：{DateTime.Now.ToString("yyyy年MM月dd日 HH时mm分")}{interval}]")
                    {
                        AuthorName = "上线提醒"
                    });
                }
                
                while (true)
                {
                    var approvalRequests = new List<ToolApprovalRequestContent>();
                    var queueMessages = pendingMessages.ToArray();
                    pendingMessages.Clear();

                    var parser = new MonologueParser();
                    parser.OnMonologueContent = (text) =>
                    {
                        _ = _eventSource.EmitAsync("thought",
                            new { text });
                    };
                    parser.OnMonologueStart = () =>
                    {
                        _ = _eventSource.EmitAsync("thought_start",
                            new { });
                    };
                    await foreach (var update in _agent!.RunStreamingAsync(queueMessages, _session!,
                                       cancellationToken: token))
                    {
                        foreach (var content in update.Contents)
                        {
                            if (content is TextReasoningContent reasoning)
                            {
                                await _eventSource.EmitAsync("thought",
                                    new { text = reasoning.Text });
                            }
                            else if (content is TextContent text)
                            {
                                parser.Feed(text.Text);
                            }
                            else if (content is FunctionCallContent functionCallContent)
                            {
                                _logger.LogInformation("{0} 工具调用 {1}", DateTime.Now, functionCallContent.Name);
                                if (functionCallContent.Name == "Shutdown")
                                {
                                    needShutdown = true;
                                }
                            }
                            else if (content is UsageContent usage)
                            {
                                await _eventSource.EmitAsync("usage", new
                                {
                                    totalTokens = usage.Details.TotalTokenCount ?? 0,
                                    inputTokens = usage.Details.InputTokenCount ?? 0,
                                    outputTokens = usage.Details.OutputTokenCount ?? 0
                                });
                            }
                            else if (content is ToolApprovalRequestContent tool)
                            {
                                approvalRequests.Add(tool);
                            }
                        }
                    }

                    if (approvalRequests.Count > 0)
                    {
                        foreach (var approvalRequest in approvalRequests)
                        {
                            pendingMessages.Add(new ChatMessage(ChatRole.User, [approvalRequest.CreateResponse(true)]));
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                await _eventSource.EmitAsync("agent_error", new { text = "Agent 运行被取消" });
                break;
            }
            catch (Exception ex)
            {
                await _eventSource.EmitAsync("agent_error", new { text = $"Agent 异常: {ex.Message}" });
                break;
            }

            await _eventSource.EmitAsync("turn_end", new { });

            if (!_waitCalledThisTurn)
            {
                // 模型漏调 WaitForMessage：直接等待新消息，避免空转 LLM 调用
                _sleepState.Remark = "";
                await _eventSource.EmitAsync("waiting", new { seconds = 3, remark = "自动等待", endAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3 * 1000 });
                await _messageBus.WaitAsync(3, token);
                await WaitForCompactionAsync();
                var pending = _messageBus.DrainMessages();
                if (pending.Length > 0)
                {
                    await InjectUserMessages(pending);
                    foreach (var m in pending)
                    {
                        await _eventSource.EmitAsync("user_message", new { text = m.Text, imageId = m.ImageId });
                    }
                }
            }
            else
            {
                // 正常结束，等待一小段时间再重启
                _logger.LogWarning("正常结束");
                if (!needShutdown)
                {
                    await Task.Delay(1000, token);
                }
            }
        }

        _running = false;
        await _eventSource.EmitAsync("loop_stopped", new { });
        if (needShutdown)
        {
            await SaveSessionAsync();
        }
    }
}