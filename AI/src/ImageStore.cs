namespace AI;

/// <summary>
/// 图片存储：将图片保存到本地文件夹，分配唯一 ID，按需读取
/// </summary>
public class ImageStore
{
    private readonly string _imageDir;
    private int _counter = 0;

    public ImageStore()
    {
        _imageDir = DataPaths.ImageDir;
        Directory.CreateDirectory(_imageDir);
    }

    /// <summary>保存 base64 图片到本地，返回分配的 ID</summary>
    public string Save(string base64)
    {
        var id = Interlocked.Increment(ref _counter).ToString();
        var bytes = Convert.FromBase64String(base64);
        File.WriteAllBytes(GetPath(id), bytes);
        return id;
    }

    /// <summary>根据 ID 读取图片字节</summary>
    public byte[]? Load(string id)
    {
        var path = GetPath(id);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>获取图片文件路径</summary>
    public string GetPath(string id) => Path.Combine(_imageDir, $"{id}.png");

    /// <summary>清空所有图片</summary>
    public void Clear()
    {
        if (Directory.Exists(_imageDir))
        {
            foreach (var file in Directory.GetFiles(_imageDir))
            {
                File.Delete(file);
            }
        }
    }
}
