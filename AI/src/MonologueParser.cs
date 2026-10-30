using System.Text;

namespace AI;

/// <summary>
/// 解析 LLM 流式文本中的 &lt;monologue&gt; 标签。
/// 在标签起始、关闭、内容追加时触发回调。
/// 换行缓冲：末尾换行先缓存不触发，等下一个非换行字符到达后一并触发，避免输出末尾换行。
/// 同时支持解析完整非流式字符串（通过 <see cref="Parse"/> 方法）。
/// </summary>
public class MonologueParser
{
    private const string StartTag = "<monologue>";
    private const string EndTag = "</monologue>";

    /// <summary>普通文本（标签外部），已应用换行缓冲</summary>
    public Action<string>? OnText { get; set; }

    /// <summary>monologue 标签起始</summary>
    public Action? OnMonologueStart { get; set; }

    /// <summary>monologue 内容追加，已应用换行缓冲</summary>
    public Action<string>? OnMonologueContent { get; set; }

    /// <summary>monologue 标签关闭</summary>
    public Action? OnMonologueEnd { get; set; }

    private bool _inMonologue;
    private readonly StringBuilder _pending = new();   // 可能是标签前缀的待定字符
    private readonly StringBuilder _newlines = new();  // 缓存的末尾换行符

    /// <summary>喂入一段流式文本，解析后触发相应回调</summary>
    public void Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        _pending.Append(chunk);
        ProcessBuffer();
    }

    /// <summary>流结束：未完成的标签前缀作为普通文本处理，丢弃缓存的末尾换行</summary>
    public void End()
    {
        if (_pending.Length > 0)
        {
            FlushContent(_pending.ToString());
            _pending.Clear();
        }
        _newlines.Clear();
    }

    /// <summary>
    /// 解析完整非流式字符串，返回所有 &lt;monologue&gt; 标签内的内容。
    /// 容错：未闭合的标签在文本结束时自动收尾，已累积的内容作为最后一个结果返回。
    /// 每个 monologue 内容末尾的换行会被去掉。
    /// </summary>
    public static string[] Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var results = new List<string>();
        var current = new StringBuilder();

        var parser = new MonologueParser
        {
            OnMonologueStart = () => current.Clear(),
            OnMonologueContent = t => current.Append(t),
            OnMonologueEnd = () =>
            {
                if (current.Length > 0)
                {
                    results.Add(current.ToString());
                    current.Clear();
                }
            }
        };
        parser.Feed(text);
        parser.End();

        // 容错：未关闭的标签，把累积的内容作为最后一个 monologue 返回
        if (current.Length > 0)
        {
            results.Add(current.ToString());
        }

        return results.ToArray();
    }

    private void ProcessBuffer()
    {
        var buffer = _pending.ToString();
        _pending.Clear();

        int i = 0;
        while (i < buffer.Length)
        {
            if (buffer[i] != '<')
            {
                // 累积到下一个 '<' 为止，批量处理
                int lt = buffer.IndexOf('<', i);
                int end = lt < 0 ? buffer.Length : lt;
                FlushContent(buffer.AsSpan(i, end - i));
                i = end;
                continue;
            }

            // 当前字符是 '<'，尝试匹配标签
            var (type, len) = TryMatchTag(buffer, i);
            if (type != TagMatch.None)
            {
                // 标签边界：丢弃缓存的末尾换行
                _newlines.Clear();
                if (type == TagMatch.Start)
                {
                    _inMonologue = true;
                    OnMonologueStart?.Invoke();
                }
                else
                {
                    _inMonologue = false;
                    OnMonologueEnd?.Invoke();
                }
                i += len;
            }
            else if (CouldBeTagPrefix(buffer, i))
            {
                // 可能是标签前缀，暂存等待更多输入
                _pending.Append(buffer, i, buffer.Length - i);
                return;
            }
            else
            {
                // 不是标签，'<' 作为普通字符
                FlushContent("<");
                i++;
            }
        }
    }

    /// <summary>处理一段不含 '&lt;' 的内容，应用换行缓冲后触发回调</summary>
    private void FlushContent(ReadOnlySpan<char> content)
    {
        if (content.IsEmpty) return;

        // 剥离末尾换行符
        int end = content.Length;
        while (end > 0 && IsNewline(content[end - 1]))
            end--;

        var safe = content[..end];
        var trailing = content[end..];

        if (!safe.IsEmpty)
        {
            // 先刷新缓存的换行，再追加安全内容
            string combined = _newlines.Length > 0
                ? string.Concat(_newlines.ToString(), safe.ToString())
                : safe.ToString();
            _newlines.Clear();

            if (_inMonologue)
                OnMonologueContent?.Invoke(combined);
            else
                OnText?.Invoke(combined);
        }

        // 缓存末尾换行，等下一个非换行字符到达时再触发
        if (!trailing.IsEmpty)
            _newlines.Append(trailing);
    }

    /// <summary>尝试在 buffer[start] 处匹配完整标签</summary>
    private (TagMatch type, int length) TryMatchTag(string buffer, int start)
    {
        string target = _inMonologue ? EndTag : StartTag;
        int remaining = buffer.Length - start;

        if (remaining >= target.Length &&
            buffer.AsSpan(start, target.Length).SequenceEqual(target))
        {
            return (_inMonologue ? TagMatch.End : TagMatch.Start, target.Length);
        }

        return (TagMatch.None, 0);
    }

    /// <summary>buffer[start..] 是否可能是标签前缀（需更多输入才能确认）</summary>
    private bool CouldBeTagPrefix(string buffer, int start)
    {
        string target = _inMonologue ? EndTag : StartTag;
        var sub = buffer.AsSpan(start);
        return target.AsSpan().StartsWith(sub);
    }

    private static bool IsNewline(char c) => c == '\n' || c == '\r';

    private enum TagMatch { None, Start, End }
}
