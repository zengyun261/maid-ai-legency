using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.AI;

namespace AI;

/// <summary>
/// Agent 工具：通过 [Description] 注解描述方法语义，由 AIFunctionFactory 反射创建。
/// 工具方法在 Agent 运行期间执行，依赖在运行期通过持有的 IServiceProvider 按需解析（避免与 MaidAgent 构造期循环依赖）。
/// </summary>
#pragma warning disable MAAI001
public class MaidTools
{
    private readonly IServiceProvider _services;

    /// <param name="services">运行期解析服务用，不在此构造，由 MaidAgent 注入</param>
    public MaidTools(IServiceProvider services) => _services = services;

    /// <summary>构建女仆 Agent 的全部工具</summary>
    public IList<AIFunction> Build()
    {
        return
        [
            AIFunctionFactory.Create(Shutdown, new AIFunctionFactoryOptions()),
            AIFunctionFactory.Create(WaitForMessage, new AIFunctionFactoryOptions()),
            AIFunctionFactory.Create(SendMessages, new AIFunctionFactoryOptions()),
        ];
    }

    /// <summary>从持有 ServiceProvider 解析所需服务</summary>
    private T Get<T>() where T : class
        => _services.GetService(typeof(T)) as T
           ?? throw new InvalidOperationException($"{typeof(T).Name} 未注册为服务");


    [Description("关机，用户主动要求时才能调用。")]
    public async Task<string> Shutdown()
    {
        var es = Get<ChatEventSource>();
        // shutdown 是一次性弹窗事件，不进入历史重放，避免刷新页面再次弹出
        await es.EmitAsync("shutdown", new { }, replay: false);
        await es.EmitAsync("tool", new { name = "shutdown", label = "关机" });
        return "[已关机]";
    }

    [Description("进入等待状态。")]
    public async Task<string> WaitForMessage(
        [Description("等待时间（秒），比如60/180")] int waitSeconds,
        [Description("碎碎念(一句话)")] string remark)
    {
        waitSeconds = Math.Clamp(waitSeconds, 20, int.MaxValue);
        var eventSource = Get<ChatEventSource>();
        var sleepState = Get<SleepState>();
        var messageBus = Get<MessageBus>();
        var maidService = Get<MaidService>();

        maidService.MarkWaitCalled();
        sleepState.Remark = remark ?? "";

        await eventSource.EmitAsync("waiting", new { seconds = waitSeconds, remark = remark ?? "", endAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + waitSeconds * 1000 });

        // 阻塞等待，收到消息会立即唤醒；应用关闭时也会立即取消
        await messageBus.WaitAsync(waitSeconds, maidService.LoopToken);

        // 等待压缩结束再继续，避免压缩和消息处理并发
        await maidService.WaitForCompactionAsync();

        // 取出等待期间收到的消息，以 User 角色注入到 session 对话历史
        var msgs = messageBus.DrainMessages();
        if (msgs.Length > 0)
        {
            await maidService.InjectUserMessages(msgs);
            // 通知前端消息已被消费（从 pending 移到正式消息列表）
            foreach (var m in msgs)
            {
                await eventSource.EmitAsync("user_message", new { text = m.Text, imageId = m.ImageId });
            }
        }

        var resultMsg = msgs.Length == 0
            ? "等待结束，没有收到新消息。"
            : $"收到{msgs.Length}条新消息。";

        await eventSource.EmitAsync("tool", new { name = "WaitForMessage", label = "等待回复", result = resultMsg });
        return resultMsg;
    }

    [Description("发送消息，支持一次发送最多3条，contents 数组长度超过3条会失败。")]
    public async Task<string> SendMessages([MaxLength(3)]string[] contents)
    {
        if (contents.Length > 3)
        {
            return $"[发送失败：消息不能超过3条，当前条数：{contents.Length}，请合并后重发]";
        }
        var eventSource = Get<ChatEventSource>();
        foreach (var content in contents)
        {
            await eventSource.EmitAsync("message", new { text = content });
        }
        Get<MaidAgent>().LastSentTime = DateTime.Now;

        if (!File.Exists(DataPaths.TimestampFile))
        {
            File.Create(DataPaths.TimestampFile).Close();
        }
        else
        {
            File.SetLastWriteTimeUtc(DataPaths.TimestampFile, DateTime.UtcNow);
        }

        return "[已发送]";
    }
}
