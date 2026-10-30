using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AI;

public class TimeProvider(IServiceProvider sp): AIContextProvider(null, FilterMessages)
{
    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, CancellationToken cancellationToken = new CancellationToken())
    {
        var now = DateTime.Now.ToString("yyyy年MM月dd日 HH时mm分");
        
        return new AIContext()
        {
            Messages = [
                new ChatMessage(ChatRole.User, $"[时间：{now}]"),
            ],
            Tools = [
                AIFunctionFactory.Create(GetCurrentTime)
            ]
        };
    }

    static IEnumerable<ChatMessage> FilterMessages(IEnumerable<ChatMessage> messages)
    {
        return messages.Where(v => v.GetAgentRequestMessageSourceId() == nameof(TimeProvider));
    }
    
    
    [Description("获取当前的日期和时间")]
    async Task<string> GetCurrentTime()
    {
        var now = DateTime.Now.ToString("yyyy年MM月dd日 HH时mm分");
        await sp.GetRequiredService<ChatEventSource>().EmitAsync("tool", new { name = "GetTime", label = "查看了时间", result = now });
        return now;
    }
}