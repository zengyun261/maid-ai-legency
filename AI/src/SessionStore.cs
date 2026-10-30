using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AI;

/// <summary>
/// 会话持久化：快照 + 游标模式。
/// 快照存于 history/，游标 current 指向当前活动快照。
/// 支持 Undo/Redo 风格的分支切换：恢复到旧快照后保存，会剪掉该位置之后的所有快照。
/// </summary>
#pragma warning disable MAAI001
public class SessionStore
{
    private readonly MaidAgent _maidAgent;
    private const int MaxHistorySnapshots = 50;

    public SessionStore(MaidAgent maidAgent)
    {
        _maidAgent = maidAgent;
    }

    /// <summary>保存当前 session 到新快照（去除末尾的非 User 消息）</summary>
    public async Task SaveAsync(AgentSession session)
    {
        if (!session.TryGetInMemoryChatHistory(out var history)) return;

        var agent = _maidAgent.Agent;
        var list = history.ToList();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].Role == ChatRole.Assistant && list[i].Contents.Any(v => v is FunctionCallContent))
            {
                list.RemoveAt(i);
            }
            else
            {
                break;
            }
        }

        list.RemoveAll(m => m.AdditionalProperties?.TryGetValue(TimelineProvider.TimelineProperty, out var _) is true);

        var newSession = await agent.CreateSessionAsync();
        newSession.SetInMemoryChatHistory(list);

        var json = await agent.SerializeSessionAsync(newSession);
        var doc = JsonSerializer.Serialize(json, new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true
        });

        // 内容比对：与游标指向的当前快照相同则跳过
        var cursor = await GetCursorAsync();
        if (!string.IsNullOrEmpty(cursor))
        {
            var curPath = Path.Combine(DataPaths.SessionHistoryDir, cursor!);
            if (File.Exists(curPath))
            {
                var current = await File.ReadAllTextAsync(curPath);
                if (current == doc) return;
            }
        }

        // 剪掉游标之后的快照（Undo 后修改丢失 Redo），写新快照，更新游标
        if (!string.IsNullOrEmpty(cursor))
        {
            PruneAfter(cursor!);
        }
        var name = await WriteSnapshotFileAsync(doc);
        await SetCursorAsync(name);
    }

    /// <summary>加载游标指向的当前快照，无快照返回 null</summary>
    public async Task<AgentSession?> LoadAsync()
    {
        var cursor = await GetCursorAsync();
        if (string.IsNullOrEmpty(cursor)) return null;
        var path = Path.Combine(DataPaths.SessionHistoryDir, cursor!);
        if (!File.Exists(path)) return null;
        var doc = await File.ReadAllTextAsync(path);
        var json = JsonSerializer.Deserialize<JsonElement>(doc);
        return await _maidAgent.Agent.DeserializeSessionAsync(json);
    }

    public bool Exists => File.Exists(DataPaths.SessionCursorFile) &&
                          !string.IsNullOrEmpty(File.ReadAllText(DataPaths.SessionCursorFile));

    // ---------- 快照与游标管理 ----------

    /// <summary>当前游标指向的快照文件名（不含路径），null 表示无快照</summary>
    public static async Task<string?> GetCursorAsync()
    {
        if (!File.Exists(DataPaths.SessionCursorFile)) return null;
        return (await File.ReadAllTextAsync(DataPaths.SessionCursorFile)).Trim();
    }

    /// <summary>写入快照文件，返回文件名。同秒内覆盖。不负责剪枝和游标更新。</summary>
    private static async Task<string> WriteSnapshotFileAsync(string content)
    {
        var name = $"{DateTime.Now:yyyyMMdd_HHmmss}.json";
        var path = Path.Combine(DataPaths.SessionHistoryDir, name);
        await File.WriteAllTextAsync(path, content);

        // 清理超过上限的旧快照（按文件名降序保留前 N 个）
        var snapshots = Directory.GetFiles(DataPaths.SessionHistoryDir, "*.json")
            .OrderByDescending(f => f)
            .Skip(MaxHistorySnapshots)
            .ToList();
        foreach (var old in snapshots)
        {
            try { File.Delete(old); } catch { }
        }

        return name;
    }

    /// <summary>删除文件名严格大于 cursor 的所有快照（即游标之后的分支）</summary>
    private static void PruneAfter(string cursor)
    {
        if (!Directory.Exists(DataPaths.SessionHistoryDir)) return;
        foreach (var f in Directory.GetFiles(DataPaths.SessionHistoryDir, "*.json"))
        {
            var name = Path.GetFileName(f);
            if (string.Compare(name, cursor, StringComparison.Ordinal) > 0)
            {
                try { File.Delete(f); } catch { }
            }
        }
    }

    private static async Task SetCursorAsync(string name)
    {
        await File.WriteAllTextAsync(DataPaths.SessionCursorFile, name);
    }

    /// <summary>列出所有历史快照，按时间倒序</summary>
    public static async Task<IReadOnlyList<SessionSnapshot>> ListSnapshotsAsync()
    {
        if (!Directory.Exists(DataPaths.SessionHistoryDir)) return [];
        var cursor = await GetCursorAsync();
        var di = new DirectoryInfo(DataPaths.SessionHistoryDir);
        return di.GetFiles("*.json")
            .OrderByDescending(f => f.Name)
            .Select(f => new SessionSnapshot
            {
                Name = f.Name,
                CreatedAt = f.CreationTime,
                Size = f.Length,
                IsCurrent = f.Name == cursor
            })
            .ToList();
    }

    /// <summary>
    /// 切换游标到指定快照。不删除任何快照（允许继续往回切换）。
    /// </summary>
    public static async Task RestoreSnapshotAsync(string name)
    {
        var path = Path.Combine(DataPaths.SessionHistoryDir, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"快照不存在: {name}");
        await SetCursorAsync(name);
    }
}

public class SessionSnapshot
{
    public string Name { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public long Size { get; set; }
    /// <summary>是否为当前游标指向的快照</summary>
    public bool IsCurrent { get; set; }
}
