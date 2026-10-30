using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AI;

public class TimelineProvider : AIContextProvider
{
    public const string TimelineProperty = "_is_timeline";

    public class TimelineItem
    {
        public int Id { get; set; }
        public DateTimeOffset Time { get; set; }
        public string Summary { get; set; }
        public string Content { get; set; }
    }

    public class State
    {
        public List<TimelineItem> Items { get; set; } = [];
        public bool Loaded { get; set; }
    }

    private readonly ProviderSessionState<State> _sessionState;

    public TimelineProvider()
    {
        _sessionState = new ProviderSessionState<State>((session) =>
        {
            if (!File.Exists(DataPaths.TimestampFile))
            {
                return new State();
            }

            var text = File.ReadAllText(DataPaths.TimelineFile);
            var items = JsonSerializer.Deserialize<List<TimelineItem>>(text);
            return new State() { Items = items };
        }, "TimelineProvider");
    }

    public override IReadOnlyList<string> StateKeys => [this._sessionState.StateKey];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context,
        CancellationToken cancellationToken = new CancellationToken())
    {
        var state = this._sessionState.GetOrInitializeState(context.Session);
        ChatMessage[] messages;
        if (state.Items.Count == 0 || state.Loaded)
        {
            messages = [];
        }
        else
        {
            messages = state.Items.Select(v =>
                {
                    var content = $"摘要：{v.Summary}";
                    return new ChatMessage(ChatRole.System,
                        @$"[时间线事件 id:{v.Id}] {v.Time.ToLocalTime().ToString("yyyy年MM月dd日 HH时mm分")}
{content}
")
                    {
                        AdditionalProperties = new()
                        {
                            [TimelineProperty] = true
                        }
                    };
                })
                .ToArray();
        }

        if (!state.Loaded)
        {
            state.Loaded = true;
            _sessionState.SaveState(context.Session, state);
        }

        return new AIContext
        {
            Instructions = @"#时间线系统
历史消息会被提炼为时间线事件，每个事件包含 id、时间、摘要、详情 4个属性。`详情`需要按需加载。
",
            Messages = messages,
            Tools =
            [
                AIFunctionFactory.Create(GetTimelineContent)
            ]
        };
    }

    [Description("加载时间线事件详情，越近的记录id值越大。")]
    private async Task<string> GetTimelineContent(JsonElement id)
    {
        var state = this._sessionState.GetOrInitializeState(AIAgent.CurrentRunContext.Session);
        id.TryGetInt32(out var idInt);
        var item = state.Items.FirstOrDefault(v => v.Id == idInt);
        if (item == null)
        {
            return "未找到时间线" + id;
        }

        return $"[已加载事件{id}详情] " + item.Time.ToLocalTime().ToString("yyyy年MM月dd日 HH时mm分") + "\n" + item.Content;
    }
}