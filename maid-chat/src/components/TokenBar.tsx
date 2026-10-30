import { useState } from 'react';
import { useChatStore } from '../lib/chatStore';

export default function TokenBar() {
  const tokenCount = useChatStore((s) => s.tokenCount);
  const maxTokens = useChatStore((s) => s.maxTokens);
  const percentage = Math.min((tokenCount / maxTokens) * 100, 100);
  const [expanded, setExpanded] = useState(false);

  const getColor = () => {
    if (percentage < 50) return 'bg-emerald-500';
    if (percentage < 80) return 'bg-amber-500';
    return 'bg-red-500';
  };

  const getTextColor = () => {
    if (percentage < 50) return 'text-emerald-500';
    if (percentage < 80) return 'text-amber-500';
    return 'text-red-500';
  };

  return (
    <div
      className="shrink-0 px-4 py-2.5 bg-card border-b border-border cursor-pointer select-none"
      onClick={() => setExpanded(!expanded)}
    >
      <div className="flex items-center gap-3">
        <span className="text-xs text-muted-foreground whitespace-nowrap">上下文</span>
        <div className="flex-1 h-1.5 bg-muted rounded-full overflow-hidden">
          <div
            className={`h-full rounded-full transition-all duration-500 ${getColor()}`}
            style={{ width: `${percentage}%` }}
          />
        </div>
        <span className={`text-xs font-medium whitespace-nowrap ${getTextColor()}`}>
          {tokenCount.toLocaleString()} / {maxTokens.toLocaleString()}
        </span>
        <span className="text-xs text-muted-foreground">{expanded ? '▲' : '▼'}</span>
      </div>
      {expanded && (
        <div className="mt-1.5 flex items-center gap-4 text-xs text-muted-foreground">
          <span>已使用 <strong className="text-foreground">{tokenCount.toLocaleString()}</strong> tokens</span>
          <span>剩余 <strong className="text-foreground">{Math.max(0, maxTokens - tokenCount).toLocaleString()}</strong> tokens</span>
          <span>占用 <strong className="text-foreground">{percentage.toFixed(1)}%</strong></span>
        </div>
      )}
    </div>
  );
}
