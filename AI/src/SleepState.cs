namespace AI;

/// <summary>
/// 共享睡眠状态：SetSleep 工具写入，循环读取
/// </summary>
public class SleepState
{
    public int DelaySeconds { get; set; } = 3;
    public string Remark { get; set; } = "";
}
