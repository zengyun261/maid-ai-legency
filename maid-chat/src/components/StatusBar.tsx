import { useChatStore } from '../lib/chatStore';

export default function StatusBar({ onOpenHistory }: { onOpenHistory: () => void }) {
  const connected = useChatStore((s) => s.connected);
  const running = useChatStore((s) => s.running);
  const waiting = useChatStore((s) => s.waiting);
  const compacting = useChatStore((s) => s.compacting);
  const startLoop = useChatStore((s) => s.startLoop);
  const stopLoop = useChatStore((s) => s.stopLoop);
  const compact = useChatStore((s) => s.compact);
  const saveSession = useChatStore((s) => s.saveSession);
  const restoreSession = useChatStore((s) => s.restoreSession);

  const compactAndRestore = useChatStore((s) => s.compactAndRestore);

  const statusText = !connected
    ? '未连接'
    : compacting
      ? '压缩中...'
      : running
        ? waiting
          ? '等待中'
          : '运行中'
        : '已就绪';

  const btnBase =
    'rounded-md px-3 py-1.5 text-xs font-medium transition-colors disabled:opacity-40 disabled:cursor-not-allowed';
  const btnSecondary = `${btnBase} border border-border bg-secondary text-secondary-foreground hover:bg-accent`;
  const btnPrimary = `${btnBase} bg-primary text-primary-foreground hover:bg-primary/90`;
  const btnDestructive = `${btnBase} border border-destructive/40 bg-destructive/10 text-destructive hover:bg-destructive/20`;

  return (
    <div className="flex items-center gap-3 px-4 py-3 border-b border-border">
      <span className="text-base font-semibold text-foreground">没有感情的聊天机器人</span>
      <div className="flex items-center gap-2">
        <span
          className={`inline-block w-2 h-2 rounded-full ${
            connected ? (running ? 'bg-emerald-400' : 'bg-amber-400') : 'bg-red-400'
          }`}
        />
        <span className="text-xs text-muted-foreground">{statusText}</span>
      </div>

      <div className="ml-auto flex items-center gap-2">
        <button
          onClick={onOpenHistory}
          className={btnSecondary}
        >
          历史
        </button>
        {running ? (
          <>
            <button
              onClick={compact}
              disabled={!waiting || compacting}
              className={btnSecondary}
            >
              {compacting ? '压缩中...' : '压缩'}
            </button>
            <button
              onClick={saveSession}
              className={btnSecondary}
            >
              保存
            </button>
            <button
              onClick={stopLoop}
              className={btnDestructive}
            >
              停止
            </button>
          </>
        ) : (
          <>
            <button
              onClick={compactAndRestore}
              disabled={!connected || compacting}
              className={btnSecondary}
            >
              {compacting ? '压缩中...' : '压缩并恢复'}
            </button>
            <button
              onClick={restoreSession}
              disabled={!connected}
              className={btnSecondary}
            >
              恢复
            </button>
            <button
              onClick={startLoop}
              disabled={!connected}
              className={btnPrimary}
            >
              启动（不恢复）
            </button>
          </>
        )}
      </div>
    </div>
  );
}
