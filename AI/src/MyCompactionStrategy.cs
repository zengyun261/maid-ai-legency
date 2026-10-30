using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AI;

#pragma warning disable MAAI001
public class MyCompactionStrategy : CompactionStrategy
{
    public int MinimumPreservedGroups { get; init; }

    public string SummarizationPrompt { get; init; } = @"
【角色设定】
你是我（第一人称）的内心独白写手。你需要将以下对话记录，压缩成我当时的【第一人称内心独白】。

【核心约束】
1. 人称：全文用""我""指代对话中的""我""，用""他/她""指代对方。不要出现""TA""或""对方""这类第三人称分析性表述。
2. 格式：纯叙事散文，不分条目，不用bullet point，不用JSON。
3. 情绪流：保留完整的情绪弧线（从什么情绪开始→中间怎么波动→最后停留在什么状态）。
4. 事实处理：丢弃具体轮次、时间词等琐碎细节，只保留事件骨架。
5. 语气风格：口语化、碎碎念、带内心吐槽，像一个人在洗澡时复盘今天的对话，不书面、不正式。
6. 隐秘感：对于""像什么""这类模糊认知，保留""我觉得像但不确定""的暧昧感，不要替我做确定判断。

【⭐时间视角（强制）】
你现在是“当时的我”，正站在【对话发生的当天/当时】回看刚才发生的这几段对话。
禁止使用“现在想起来”、“回头看”、“后来才发现”、“如今”等站在当下回望过去的表述。
允许使用“这会儿”、“刚”、“此刻”、“这下”、“到现在为止”等站在当时当下感知时间的词汇。
示例：把“现在想想他当时是在试探我”改成 “他刚说那句，是在试探我吗？” 或 “我后知后觉，他好像是在试探我”（后知后觉是当时的即时感受，不是多年后的回望）。

【压缩率】
输出字数控制在输入字数的15%-20%之间。

后续将直接发送对话内容。
每次我发新的聊天记录，以上次的摘要为背景，只摘要**新增的聊天记录**。
";

    private IChatClient c;

    public MyCompactionStrategy(IChatClient c, CompactionTrigger trigger, 
        int minimumPreservedGroups = 8,
        CompactionTrigger? target = null) : base(
        trigger, target)
    {
        this.c = c;
        this.MinimumPreservedGroups = minimumPreservedGroups;
    }

    protected override async ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger,
        CancellationToken cancellationToken)
    {
        
        for (int i = 0; i < index.Groups.Count; i++)
        {
            CompactionMessageGroup group = index.Groups[i];
            // 用户消息中带图片的，压缩时排除（避免把图片数据传给摘要模型）
            if (group.Kind == CompactionGroupKind.User &&
                group.Messages.Any(m => m.Contents.Any(c => c is DataContent)))
            {
                group.IsExcluded = true;
                continue;
            }
        }
        // Count non-system, non-excluded groups to determine which are protected
        int nonSystemIncludedCount = 0;
        for (int i = 0; i < index.Groups.Count; i++)
        {
            CompactionMessageGroup group = index.Groups[i];
            if (!group.IsExcluded && group.Kind != CompactionGroupKind.System)
            {
                nonSystemIncludedCount++;
            }
        }

        int protectedFromEnd = Math.Min(this.MinimumPreservedGroups, nonSystemIncludedCount);
        int maxSummarizable = nonSystemIncludedCount - protectedFromEnd;

        if (maxSummarizable <= 0)
        {
            return false;
        }

        // Mark oldest non-system groups for summarization one at a time until the target is met.
        // Track which groups were excluded so we can restore them if the LLM call fails.
        List<CompactionMessageGroup> excludedGroups = [];
        int insertIndex = -1;

        var sb = new StringBuilder();
        var lastSummary = "";
        DateTimeOffset? startTime = null;
        DateTimeOffset? endTime = null;
        
        for (int i = 0; i < index.Groups.Count && excludedGroups.Count < maxSummarizable; i++)
        {
            CompactionMessageGroup group = index.Groups[i];
            if (group.IsExcluded || group.Kind == CompactionGroupKind.System)
            {
                continue;
            }

            if (insertIndex < 0)
            {
                insertIndex = i;
            }

            // Collect messages from this group for summarization
            foreach (var message in group.Messages)
            {
                if (message.CreatedAt.HasValue)
                {
                    endTime = message.CreatedAt.Value;
                }
                if (startTime == null && message.CreatedAt.HasValue
                    && message.AdditionalProperties?.TryGetValue(CompactionMessageGroup.SummaryPropertyKey, out var _) is not true)
                {
                    startTime = message.CreatedAt.Value;
                }
                if (message.Role == ChatRole.Assistant)
                {
                    if (message.AdditionalProperties?.TryGetValue(CompactionMessageGroup.SummaryPropertyKey, out var isSummary) == true)
                    {
                        lastSummary = message.Text.Replace("[Summary]", "").ReplaceLineEndings("").Trim();
                    }
                    else
                    {
                        foreach (var content in message.Contents)
                        {
                            if (content is TextContent textContent)
                            {
                                // 剥掉 <monologue> 标签，只保留独白内容
                                var monologue = StripMonologueTags(textContent.Text.Trim());
                                if (!string.IsNullOrEmpty(monologue))
                                {
                                    sb.AppendLine("[我心想]：" + monologue.ReplaceLineEndings(""));
                                }
                            }

                            if (content is FunctionCallContent call && call.Name == "SendMessage"
                                && call.Arguments != null && call.Arguments.TryGetValue("content", out var msgVal))
                            {
                                sb.AppendLine("[我说]：" + (msgVal?.ToString() ?? "").ReplaceLineEndings(""));
                            }
                        }
                    }
                }

                if (message.Role == ChatRole.User)
                {
                    sb.AppendLine("[对方说]：" + message.Text.ReplaceLineEndings(""));
                }
            }

            group.IsExcluded = true;
            group.ExcludeReason = $"Summarized by {nameof(MyCompactionStrategy)}";
            excludedGroups.Add(group);

            // Stop marking when target condition is met
            if (this.Target(index))
            {
                break;
            }
        }
        if(sb.Length < 100) {
            foreach(var group in excludedGroups) {
                group.IsExcluded = false;
            }
            return false;
        }
        
        ChatResponse response;
        
        List<ChatMessage> summarizationMessages =
        [
            new ChatMessage(ChatRole.System, this.SummarizationPrompt),
            new ChatMessage(ChatRole.User, [
                new TextContent("【上次的摘要】\n" + (lastSummary.Length == 0 ? "（无）": lastSummary)),
                new TextContent("【新增聊天记录】\n" + sb),
                new TextContent($"请以上次的摘要为背景，只摘要**新增的聊天记录**。聊天记录是从 {startTime?.ToLocalTime().ToString("yyyy年MM月dd日 HH:mm")} 到 {endTime?.ToLocalTime().ToString("MM月dd日 HH:mm")}，摘要里需要添加时间")
            ]),
        ];
        try
        {
            response = await this.c.GetResponseAsync(
                summarizationMessages,
                new ChatOptions()
                {
                    Temperature = 0.3f,
                    TopP = 0.8f,
                    TopK = 32,
                    MaxOutputTokens = 8192 * 2,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Restore excluded groups so the conversation is not left in an inconsistent state
            for (int i = 0; i < excludedGroups.Count; i++)
            {
                excludedGroups[i].IsExcluded = false;
                excludedGroups[i].ExcludeReason = null;
            }

            return false;
        }

        string summaryText = string.IsNullOrWhiteSpace(response.Text) ? "[Summary unavailable]" : response.Text;

        // Insert a summary group at the position of the first summarized group
        ChatMessage summaryMessage = new(ChatRole.Assistant, [
            new TextContent(lastSummary),
            new TextContent(summaryText),
        ]);
        summaryMessage.CreatedAt = startTime;
        summaryMessage.AdditionalProperties ??= [];
        summaryMessage.AdditionalProperties[CompactionMessageGroup.SummaryPropertyKey] = true;
        
        index.InsertGroup(insertIndex, CompactionGroupKind.Summary, [summaryMessage]);

        return true;
    }

    /// <summary>提取所有 &lt;monologue&gt;...&lt;/monologue&gt; 块内容，换行拼接。
    /// 处理标签未闭合的情况：只有开标签时取开标签后到下一个开标签前的内容。</summary>
    private static string StripMonologueTags(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        const string open = "<monologue>";
        const string close = "</monologue>";

        var parts = new List<string>();
        int i = 0;
        while (i < s.Length)
        {
            int openIdx = s.IndexOf(open, i, StringComparison.Ordinal);
            if (openIdx < 0) break;

            int contentStart = openIdx + open.Length;
            int closeIdx = s.IndexOf(close, contentStart, StringComparison.Ordinal);

            string part;
            if (closeIdx >= 0)
            {
                // 正常闭合：取标签内内容
                part = s.Substring(contentStart, closeIdx - contentStart).Trim();
                i = closeIdx + close.Length;
            }
            else
            {
                // 漏写闭标签：取开标签后到下一个开标签前（或末尾）的内容
                int nextOpen = s.IndexOf(open, contentStart, StringComparison.Ordinal);
                int end = nextOpen >= 0 ? nextOpen : s.Length;
                part = s.Substring(contentStart, end - contentStart).Trim();
                i = end;
            }

            if (!string.IsNullOrEmpty(part)) parts.Add(part);
        }

        return parts.Count > 0 ? string.Join("\n", parts) : s.Trim();
    }
}