using System.Diagnostics.CodeAnalysis;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace AI;

[Experimental("MAAI001")]
public class SimpleCompactionStrategy: CompactionStrategy
{
    public SimpleCompactionStrategy(CompactionTrigger trigger, CompactionTrigger? target = null) : base(trigger, target)
    {
    }

    protected override async ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
    {
        return true;
    }
}