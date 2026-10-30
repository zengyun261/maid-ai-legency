namespace AI;

/// <summary>
/// 统一管理数据文件路径，所有持久化文件放在 data/ 目录下
/// </summary>
public static class DataPaths
{
    public static string DataDir { get; } = Path.Combine(Directory.GetCurrentDirectory(), "data");
    public static string ImageDir { get; } = Path.Combine(DataDir, "images");
    public static string SessionHistoryDir { get; } = Path.Combine(DataDir, "sessions", "history");
    public static string SessionCursorFile { get; } = Path.Combine(DataDir, "sessions", "current");
    public static string MemoryFile { get; } = Path.Combine(DataDir, "memory.jsonl");
    public static string TimelineFile { get; } = Path.Combine(DataDir, "timeline.json");
    
    public static string TimestampFile { get; } = Path.Combine(DataDir, "timestamp");

    /// <summary>确保 data/ 及子目录存在</summary>
    public static void EnsureDirs()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ImageDir);
        Directory.CreateDirectory(SessionHistoryDir);
        // current 指针文件在 sessions/ 目录，确保父目录存在
        Directory.CreateDirectory(Path.GetDirectoryName(SessionCursorFile)!);
    }
}
