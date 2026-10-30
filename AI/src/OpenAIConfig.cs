namespace AI;

/// <summary>
/// OpenAI 兼容 API 的配置，绑定 appsettings.json 的 "OpenAI" 节
/// </summary>
public class OpenAIConfig
{
    public string Endpoint { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";
    public int? MaxContextTokens { get; set; }

    /// <summary>从 IConfiguration 读取并验证</summary>
    public static OpenAIConfig Load(IConfiguration config)
    {
        var endpoint = config["OpenAI:Endpoint"]
            ?? throw new InvalidOperationException("Missing config: OpenAI:Endpoint");
        var apiKey = config["OpenAI:ApiKey"]
            ?? throw new InvalidOperationException("Missing config: OpenAI:ApiKey");
        var model = config["OpenAI:Model"]
            ?? throw new InvalidOperationException("Missing config: OpenAI:Model");
        int.TryParse(config["OpenAI:MaxContextTokens"], out var maxCtx);

        return new OpenAIConfig
        {
            Endpoint = endpoint,
            ApiKey = apiKey,
            Model = model,
            MaxContextTokens = maxCtx > 0 ? maxCtx : null,
        };
    }
}
