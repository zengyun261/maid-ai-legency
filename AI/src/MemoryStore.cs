using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AI;

/// <summary>实体：知识图谱中的节点</summary>
public class MemoryEntity
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("entityType")] public string EntityType { get; set; } = "";
    [JsonPropertyName("observations")] public List<string> Observations { get; set; } = [];
}

/// <summary>关系：连接两个实体</summary>
public class MemoryRelation
{
    [JsonPropertyName("from")] public string From { get; set; } = "";
    [JsonPropertyName("to")] public string To { get; set; } = "";
    [JsonPropertyName("relationType")] public string RelationType { get; set; } = "";
}

/// <summary>知识图谱：实体 + 关系</summary>
public class KnowledgeGraph
{
    [JsonPropertyName("entities")] public List<MemoryEntity> Entities { get; set; } = [];
    [JsonPropertyName("relations")] public List<MemoryRelation> Relations { get; set; } = [];
}

public class AddObservationInput
{
    [JsonPropertyName("entityName")] public string EntityName { get; set; } = "";
    [JsonPropertyName("contents")] public List<string> Contents { get; set; } = [];
}

public class AddObservationResult
{
    [JsonPropertyName("entityName")] public string EntityName { get; set; } = "";
    [JsonPropertyName("addedObservations")] public List<string> AddedObservations { get; set; } = [];
}

public class DeleteObservationInput
{
    [JsonPropertyName("entityName")] public string EntityName { get; set; } = "";
    [JsonPropertyName("observations")] public List<string> Observations { get; set; } = [];
}

/// <summary>
/// 知识图谱存储 + 工具：JSONL 持久化，与 @modelcontextprotocol/server-memory 格式兼容。
/// 每行一个 JSON 对象，type 为 "entity" 或 "relation"。
/// Build() 返回的 AIFunction 列表可直接注册到 Agent。
/// </summary>
#pragma warning disable MAAI001
public class MemoryStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IServiceProvider _sp;
    private ILogger<MemoryStore> _logger;
    public MemoryStore(IServiceProvider sp, string filePath)
    {
        _filePath = filePath;
        _sp = sp;
        _logger = sp.GetRequiredService<ILogger<MemoryStore>>();
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
    }

    /// <summary>构建可注册到 Agent 的工具列表</summary>
    public IList<AIFunction> Build()
    {
        return
        [
            AIFunctionFactory.Create(CreateEntities),
            AIFunctionFactory.Create(CreateRelations),
            AIFunctionFactory.Create(AddObservations),
            AIFunctionFactory.Create(DeleteEntities),
            AIFunctionFactory.Create(DeleteObservations),
            AIFunctionFactory.Create(DeleteRelations),
            AIFunctionFactory.Create(ReadGraph),
            AIFunctionFactory.Create(SearchNodes),
            AIFunctionFactory.Create(OpenNodes),
        ];
    }

    // ---------- 持久化 ----------

    private async Task<KnowledgeGraph> LoadGraphAsync()
    {
        if (!File.Exists(_filePath)) return new KnowledgeGraph();

        var graph = new KnowledgeGraph();
        var lines = await File.ReadAllLinesAsync(_filePath);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "entity")
            {
                graph.Entities.Add(JsonSerializer.Deserialize<MemoryEntity>(doc.RootElement.GetRawText())!);
            }
            else if (type == "relation")
            {
                graph.Relations.Add(JsonSerializer.Deserialize<MemoryRelation>(doc.RootElement.GetRawText())!);
            }
        }
        return graph;
    }

    private async Task SaveGraphAsync(KnowledgeGraph graph)
    {
        var jsonOpts = new JsonSerializerOptions { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
        var lines = new List<string>();

        foreach (var e in graph.Entities)
        {
            var obj = new { type = "entity", name = e.Name, entityType = e.EntityType, observations = e.Observations };
            lines.Add(JsonSerializer.Serialize(obj, jsonOpts));
        }
        foreach (var r in graph.Relations)
        {
            var obj = new { type = "relation", from = r.From, to = r.To, relationType = r.RelationType };
            lines.Add(JsonSerializer.Serialize(obj, jsonOpts));
        }

        await File.WriteAllLinesAsync(_filePath, lines);
    }

    // ---------- 工具方法 ----------

    [Description("Create multiple new entities in the knowledge graph")]
    public async Task<List<MemoryEntity>> CreateEntities(
        [Description("Entities to create")] MemoryEntity[] entities)
    {
        await _lock.WaitAsync();
        try
        {
            var graph = await LoadGraphAsync();
            var newEntities = entities
                .Where(e => !graph.Entities.Any(x => x.Name == e.Name))
                .ToList();
            graph.Entities.AddRange(newEntities);
            await SaveGraphAsync(graph);
            return newEntities;
        }
        finally { _lock.Release(); }
    }

    [Description("Create multiple new relations between entities in the knowledge graph. Relations should be in active voice")]
    public async Task<List<MemoryRelation>> CreateRelations(
        [Description("Relations to create")] MemoryRelation[] relations)
    {
        await _lock.WaitAsync();
        try
        {
            var graph = await LoadGraphAsync();
            var newRelations = relations
                .Where(r => !graph.Relations.Any(x =>
                    x.From == r.From && x.To == r.To && x.RelationType == r.RelationType))
                .ToList();
            graph.Relations.AddRange(newRelations);
            await SaveGraphAsync(graph);
            return newRelations;
        }
        finally { _lock.Release(); }
    }

    [Description("Add new observations to existing entities in the knowledge graph")]
    public async Task<List<AddObservationResult>> AddObservations(
        [Description("Observations to add")] AddObservationInput[] observations)
    {
        await _lock.WaitAsync();
        try
        {
            var graph = await LoadGraphAsync();
            var results = new List<AddObservationResult>();

            foreach (var o in observations)
            {
                _logger.LogInformation($"Add observations {o.EntityName}: {JsonSerializer.Serialize(o.Contents, JsonSerializerOptions.Web)}");
                var entity = graph.Entities.FirstOrDefault(e => e.Name == o.EntityName)
                    ?? throw new InvalidOperationException($"Entity with name {o.EntityName} not found");
                var newObs = o.Contents.Where(c => !entity.Observations.Contains(c)).ToList();
                entity.Observations.AddRange(newObs);
                results.Add(new AddObservationResult { EntityName = o.EntityName, AddedObservations = newObs });
            }

            await SaveGraphAsync(graph);
            return results;
        }
        finally { _lock.Release(); }
    }

    [Description("Delete multiple entities and their associated relations from the knowledge graph")]
    public async Task DeleteEntities(
        [Description("Entity names to delete")] string[] entityNames)
    {
        await _lock.WaitAsync();
        try
        {
            var graph = await LoadGraphAsync();
            graph.Entities.RemoveAll(e => entityNames.Contains(e.Name));
            graph.Relations.RemoveAll(r => entityNames.Contains(r.From) || entityNames.Contains(r.To));
            await SaveGraphAsync(graph);
        }
        finally { _lock.Release(); }
    }

    [Description("Delete specific observations from entities in the knowledge graph")]
    public async Task<string> DeleteObservations(
        [Description("Observations to delete")] DeleteObservationInput[] deletions)
    {
        await _lock.WaitAsync();
        try
        {
            var deleted = 0;
            var graph = await LoadGraphAsync();
            foreach (var d in deletions)
            {
                _logger.LogInformation($"Delete observations {d.EntityName}: {JsonSerializer.Serialize(d.Observations, JsonSerializerOptions.Web)}");
                var entity = graph.Entities.FirstOrDefault(e => e.Name == d.EntityName);
                if (entity != null)
                {
                    deleted += entity.Observations.RemoveAll(o => d.Observations.Contains(o));
                }
            }
            await SaveGraphAsync(graph);
            return $"Deleted: {deleted}";
        }
        finally { _lock.Release(); }
    }

    [Description("Delete multiple relations from the knowledge graph")]
    public async Task DeleteRelations(
        [Description("Relations to delete")] MemoryRelation[] relations)
    {
        await _lock.WaitAsync();
        try
        {
            var graph = await LoadGraphAsync();
            graph.Relations.RemoveAll(r => relations.Any(d =>
                r.From == d.From && r.To == d.To && r.RelationType == d.RelationType));
            await SaveGraphAsync(graph);
        }
        finally { _lock.Release(); }
    }

    [Description("Read the entire knowledge graph")]
    public async Task<KnowledgeGraph> ReadGraph()
    {
        await _lock.WaitAsync();
        try { return await LoadGraphAsync(); }
        finally { _lock.Release(); }
    }

    [Description("Search for nodes in the knowledge graph based on a query")]
    public async Task<KnowledgeGraph> SearchNodes(
        [Description("Search query")] string query)
    {
        await _lock.WaitAsync();
        try
        {
            var graph = await LoadGraphAsync();
            var q = query.ToLowerInvariant();
            var filteredEntities = graph.Entities.Where(e =>
                e.Name.ToLowerInvariant().Contains(q) ||
                e.EntityType.ToLowerInvariant().Contains(q) ||
                e.Observations.Any(o => o.ToLowerInvariant().Contains(q))).ToList();

            var names = new HashSet<string>(filteredEntities.Select(e => e.Name));
            var filteredRelations = graph.Relations
                .Where(r => names.Contains(r.From) || names.Contains(r.To)).ToList();

            return new KnowledgeGraph { Entities = filteredEntities, Relations = filteredRelations };
        }
        finally { _lock.Release(); }
    }

    [Description("Open specific nodes in the knowledge graph by their names")]
    public async Task<KnowledgeGraph> OpenNodes(
        [Description("Entity names to open")] string[] names)
    {
        await _lock.WaitAsync();
        try
        {
            var graph = await LoadGraphAsync();
            var nameSet = new HashSet<string>(names);
            var filteredEntities = graph.Entities.Where(e => nameSet.Contains(e.Name)).ToList();
            var filteredNames = new HashSet<string>(filteredEntities.Select(e => e.Name));
            var filteredRelations = graph.Relations
                .Where(r => filteredNames.Contains(r.From) || filteredNames.Contains(r.To)).ToList();

            return new KnowledgeGraph { Entities = filteredEntities, Relations = filteredRelations };
        }
        finally { _lock.Release(); }
    }
}
