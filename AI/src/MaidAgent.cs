using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace AI;

/// <summary>
/// Agent 配置：模型连接、system prompt、工具注册
/// </summary>
public class MaidAgent
{
    public AIAgent Agent { get; private set; }
    public int MaxContextTokens { get; }
    [Experimental("MAAI001")]
    public MessageInjectingChatClient MessageInjector { get; }

    public DateTime LastSentTime { get; set; } = DateTime.Now;

    private readonly OpenAIConfig _openAIConfig;
    private readonly ILogger<MaidAgent> _logger;
    private readonly IConfiguration _config;
    private readonly IServiceProvider _serviceProvider;
    // 昂贵资源只创建一次：工具反射创建
    private readonly IList<AIFunction> _memoryTools;
    private readonly IList<AIFunction> _tools;

    /// <summary>手动压缩上下文</summary>
    [Experimental("MAAI001")]
    public async Task CompactSessionAsync(AgentSession session, int minimumPreservedGroups)
    {
        if (session is not ChatClientAgentSession chatSession) return;
        if (!chatSession.TryGetInMemoryChatHistory(out var history) || history.Count == 0) return;
        var strategy = new MyCompactionStrategy(CreateChatClient(), CompactionTriggers.Always, minimumPreservedGroups);

        var compacted = await CompactionProvider.CompactAsync(strategy, history, _logger);
        chatSession.SetInMemoryChatHistory(compacted.ToList());
    }

    [Experimental("MAAI001")]
    public MaidAgent(
        IConfiguration config,
        IServiceProvider serviceProvider, ILogger<MaidAgent> logger)
    {
        _logger = logger;
        _config = config;
        _serviceProvider = serviceProvider;
        _openAIConfig = OpenAIConfig.Load(config);

        // 尝试从 API 获取模型上下文长度，失败则用配置或默认值
        MaxContextTokens = FetchMaxContextTokens(_openAIConfig) ?? _openAIConfig.MaxContextTokens ?? 32768;

        MessageInjector = new MessageInjectingChatClient(CreateChatClient(enableThinking: false));

        // 昂贵资源只创建一次，ReloadAgent 时复用
        _memoryTools = new MemoryStore(serviceProvider, DataPaths.MemoryFile).Build();
        _tools = new MaidTools(serviceProvider).Build();

        Agent = BuildAgent();
    }

    /// <summary>
    /// 重新加载 Character.md 和 Engine.md 并重建 Agent。
    /// 复用已有的 chat client（MessageInjector）和工具，只刷新 Instructions。
    /// </summary>
    [Experimental("MAAI001")]
    public void ReloadAgent()
    {
        Agent = BuildAgent();
        _logger.LogInformation("Agent 已重新加载，Instructions 已刷新");
    }

    [Experimental("MAAI001")]
    private AIAgent BuildAgent()
    {
        return MessageInjector.AsAIAgent(new ChatClientAgentOptions()
        {
            Name = "女仆",
            EnableMessageInjection = true,
            RequirePerServiceCallChatHistoryPersistence = true,
            AIContextProviders = [new TimeProvider(_serviceProvider)],
            ChatOptions = new ChatOptions
            {
                Instructions = BuildInstructions(),
                AllowMultipleToolCalls = true,
                PresencePenalty = 0.3f,
                Temperature = 0.8f,
                TopP = 0.95f,
                Tools = [
                    .._memoryTools,
                    .._tools],
            },
        }, null, _serviceProvider);
    }

    public IChatClient CreateChatClient(bool enableThinking = false)
    {
        var proxyConfig = _config["Proxy"];
        HttpClient httpClient;
        if (!string.IsNullOrEmpty(proxyConfig)) 
        {
            var proxy = new WebProxy(proxyConfig);
            var handler = new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true
            };
            httpClient = new HttpClient(handler);
        }
        else
        {
            httpClient = new HttpClient();
        }
        var openAIClient = new OpenAI.OpenAIClient(
                new ApiKeyCredential(_openAIConfig.ApiKey),
                new OpenAI.OpenAIClientOptions
                {
                    Endpoint = new Uri(_openAIConfig.Endpoint),
                    Transport = new HttpClientPipelineTransport(httpClient)
                })
            .GetChatClient(_openAIConfig.Model);
        return openAIClient.AsIChatClient().AsBuilder()
            // .UseFunctionInvocation(configure: options =>
            // {
            //     options.MaximumIterationsPerRequest = 0x7fffffff;
            // })
            .ConfigureOptions(opt =>
            {
                opt.MaxOutputTokens = 4096 * 10;
                opt.RawRepresentationFactory = _ =>
                {
#pragma warning disable SCME0001
                    var o = new ChatCompletionOptions();
                    o.Patch.Set("$.chat_template_kwargs"u8.Slice(0), BinaryData.FromObjectAsJson(new
                    {
                        enable_thinking = enableThinking,
                    }));
                    if (!enableThinking)
                    {
                        o.Patch.Set("$.thinking"u8.Slice(0), BinaryData.FromObjectAsJson(new
                        {
                            type = "disabled",
                        }));
                    }
                    return o;
#pragma warning restore SCME0001
                };
            })
            .Build();
    }

    /// <summary>从 API 获取模型的上下文长度，失败返回 null</summary>
    private static int? FetchMaxContextTokens(OpenAIConfig config)
    {
        try
        {
            var url = $"{config.Endpoint.TrimEnd('/')}/models";
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", config.ApiKey);
            var response = http.GetAsync(url).Result;
            if (!response.IsSuccessStatusCode) return null;

            using var stream = response.Content.ReadAsStream();
            var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(stream);

            if (!json.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var id) || id.GetString() != config.Model) continue;

                if (item.TryGetProperty("context_length", out var cl) && cl.TryGetInt32(out var val)) return val;
                if (item.TryGetProperty("max_model_len", out var ml) && ml.TryGetInt32(out var val2)) return val2;
                if (item.TryGetProperty("max_context_length", out var mcl) && mcl.TryGetInt32(out var val3)) return val3;
                break;
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildInstructions()
    {
        var character = File.ReadAllText("Character.md");
        var engine = File.ReadAllText("Engine.md");
        return character + '\n' + engine;
    }
}
