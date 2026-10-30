import { useState, useEffect, useRef } from 'react';
import {
  AuiIf,
  MessagePrimitive,
  MessagePartPrimitive,
  ThreadPrimitive,
  useMessagePartText,
  useMessagePartImage,
} from '@assistant-ui/react';
import { Group, Panel, Separator } from 'react-resizable-panels';
import { MaidRuntimeProvider } from './lib/RuntimeProvider';
import StatusBar from './components/StatusBar';
import TokenBar from './components/TokenBar';
import { useChatStore, fetchSnapshots, restoreSnapshot, type SessionSnapshot } from './lib/chatStore';
import type { ThreadMessageLike } from '@assistant-ui/react';

type MessageKind = 'message' | 'thought' | 'tool' | 'error';

function getMessageKind(msg: ThreadMessageLike): MessageKind | undefined {
  return (msg as any).metadata?.custom?.kind;
}

function getMessageText(msg: ThreadMessageLike): string {
  const content = typeof msg.content === 'string' ? [{ type: 'text' as const, text: msg.content }] : msg.content;
  const part = content.find((c) => c.type === 'text');
  return part && 'text' in part ? part.text : '';
}

// 用户消息
function UserMessage() {
  return (
    <MessagePrimitive.Root className="flex justify-end px-4 py-1.5">
      <div className="max-w-[75%]">
        <div className="rounded-2xl rounded-tr-sm bg-primary text-primary-foreground px-4 py-2.5 text-sm leading-relaxed shadow-sm">
          <MessagePrimitive.Content
            components={{
              Image: UserImage,
            }}
          />
        </div>
      </div>
    </MessagePrimitive.Root>
  );
}

// 用户消息中的图片（从服务端加载）
function UserImage() {
  const part = useMessagePartImage();
  if (!part?.image) return null;
  const imageSrc = String(part.image);
  return (
    <div className="relative mb-1">
      <img
        src={imageSrc}
        alt="图片消息"
        className="max-w-full rounded-lg max-h-48 object-contain"
      />
    </div>
  );
}

// 女仆消息（气泡）
function AssistantMessage() {
  return (
    <MessagePrimitive.Root className="flex justify-start px-4 py-1.5">
      <div className="max-w-[80%] rounded-2xl rounded-tl-sm bg-muted px-4 py-2.5">
        <MessagePrimitive.Content
          components={{
            Text: AssistantText,
          }}
        />
      </div>
    </MessagePrimitive.Root>
  );
}

// 文本内容（带流式光标）
function AssistantText() {
  const part = useMessagePartText();
  return (
    <span className="text-sm text-foreground leading-relaxed">
      {part.text}
      <MessagePrimitive.If lastOrHover>
        <MessagePartPrimitive.InProgress>
          <span className="inline-block w-0.5 h-4 bg-foreground/70 ml-0.5 align-text-bottom animate-pulse" />
        </MessagePartPrimitive.InProgress>
      </MessagePrimitive.If>
    </span>
  );
}

// 内心独白（非气泡，默认展开）
function ThoughtMessage({ text }: { text: string }) {
  return (
    <div className="px-4 py-0.5">
      <p className="text-xs text-muted-foreground leading-relaxed whitespace-pre-wrap">{text}</p>
    </div>
  );
}

// 工具调用（特殊小字）
function ToolCallMessage({ text }: { text: string }) {
  return (
    <div className="px-4 py-1">
      <span className="inline-flex items-center gap-1.5 text-xs text-muted-foreground leading-relaxed bg-muted rounded-full px-2.5 py-0.5">
        <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
          <path strokeLinecap="round" strokeLinejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
        </svg>
        {text}
      </span>
    </div>
  );
}

// 等待状态（倒计时，固定底部）
function WaitingIndicator() {
  const waiting = useChatStore((s) => s.waiting);
  const waitingEndAt = useChatStore((s) => s.waitingEndAt);
  const waitingRemark = useChatStore((s) => s.waitingRemark);
  const skipWait = useChatStore((s) => s.skipWait);
  const [remaining, setRemaining] = useState(0);

  useEffect(() => {
    if (!waiting || !waitingEndAt) {
      setRemaining(0);
      return;
    }
    const update = () => {
      const left = Math.max(0, Math.ceil((waitingEndAt - Date.now()) / 1000));
      setRemaining(left);
    };
    update();
    const timer = setInterval(update, 1000);
    return () => clearInterval(timer);
  }, [waiting, waitingEndAt]);

  if (!waiting) return null;

  const remark = waitingRemark ? `（${waitingRemark}）` : '';
  // 格式化为 m:ss 或 h:mm:ss
  const h = Math.floor(remaining / 3600);
  const m = Math.floor((remaining % 3600) / 60);
  const s = remaining % 60;
  const timeStr = h > 0
    ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
    : `${m}:${String(s).padStart(2, '0')}`;

  return (
    <div className="px-4 py-1.5 border-t border-border bg-muted/50 flex items-center gap-2">
      <span className="inline-flex items-center gap-1.5 text-xs text-amber-500 leading-relaxed bg-amber-500/10 rounded-full px-2.5 py-0.5">
        <span className="inline-block w-1.5 h-1.5 rounded-full bg-amber-500 animate-pulse" />
        <span>💤 等待回复中{remark}</span>
      </span>
      <div className="ml-auto flex items-center gap-2 shrink-0">
        <span className="tabular-nums text-right text-xs text-amber-500">~{timeStr}</span>
        <button
          onClick={skipWait}
          className="text-xs text-muted-foreground hover:text-foreground transition-colors px-2 py-0.5 rounded-full border border-border hover:bg-accent"
        >
          跳过
        </button>
      </div>
    </div>
  );
}

// 错误（非气泡）
function ErrorMessage({ text }: { text: string }) {
  return (
    <div className="px-4 py-0.5">
      <span className="text-xs text-destructive leading-relaxed">{text}</span>
    </div>
  );
}

// 待消费消息（pending 状态，显示在 TokenBar 上方）
function PendingMessages() {
  const pending = useChatStore((s) => s.pendingMessages);
  if (pending.length === 0) return null;
  return (
    <div className="px-4 py-2 border-t border-border bg-muted/50 space-y-1">
      {pending.map((msg) => (
        <div key={msg.id} className="flex items-center gap-2 text-xs text-muted-foreground">
          <svg className="w-3 h-3 animate-spin text-muted-foreground" fill="none" viewBox="0 0 24 24">
            <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
            <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
          </svg>
          {msg.imageId && (
            <img
              src={`http://localhost:5162/images/${msg.imageId}`}
              alt="待发送图片"
              className="w-6 h-6 rounded object-cover"
            />
          )}
          <span>{msg.text}</span>
        </div>
      ))}
    </div>
  );
}

// 输入框（支持图片发送和粘贴）
function Composer() {
  const [inputText, setInputText] = useState('');
  const [pendingImage, setPendingImage] = useState<string | null>(null);
  const [pendingImageThumb, setPendingImageThumb] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);
  const sendMessage = useChatStore((s) => s.sendMessage);

  // 从 File 对象加载图片（选择文件和粘贴共用）
  const loadImage = (file: File) => {
    if (!file.type.startsWith('image/')) return;
    const thumbUrl = URL.createObjectURL(file);
    setPendingImageThumb(thumbUrl);
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string;
      const base64 = result.split(',')[1];
      setPendingImage(base64);
    };
    reader.readAsDataURL(file);
  };

  const handleFileChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0];
    if (file) loadImage(file);
    e.target.value = '';
  };

  const handlePaste = (e: React.ClipboardEvent) => {
    const items = e.clipboardData?.items;
    if (!items) return;
    for (const item of items) {
      if (item.type.startsWith('image/')) {
        e.preventDefault();
        const file = item.getAsFile();
        if (file) loadImage(file);
        return;
      }
    }
  };

  const handleSend = () => {
    const text = inputText.trim();
    if (!text && !pendingImage) return;
    sendMessage(text, pendingImage ?? undefined);
    setInputText('');
    setPendingImage(null);
    if (pendingImageThumb) {
      URL.revokeObjectURL(pendingImageThumb);
      setPendingImageThumb(null);
    }
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  };

  const clearImage = () => {
    setPendingImage(null);
    if (pendingImageThumb) {
      URL.revokeObjectURL(pendingImageThumb);
      setPendingImageThumb(null);
    }
  };

  return (
    <div className="border-t border-border bg-background">
      {/* 图片预览 */}
      {pendingImageThumb && (
        <div className="px-4 pt-2 flex items-start gap-2">
          <div className="relative inline-block">
            <img
              src={pendingImageThumb}
              alt="待发送图片"
              className="max-h-24 rounded-lg border border-border"
            />
            <button
              onClick={clearImage}
              className="absolute -top-1.5 -right-1.5 w-5 h-5 rounded-full bg-muted-foreground text-background text-xs flex items-center justify-center hover:bg-foreground"
            >
              x
            </button>
          </div>
        </div>
      )}
      <div className="flex items-center gap-2 px-4 py-3">
        {/* 图片按钮 */}
        <button
          onClick={() => fileInputRef.current?.click()}
          className="shrink-0 w-9 h-9 rounded-full flex items-center justify-center text-muted-foreground hover:text-foreground hover:bg-accent transition-colors"
        >
          <svg className="w-5 h-5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M2.25 15.75l5.159-5.159a2.25 2.25 0 013.182 0l5.159 5.159m-1.5-1.5l1.409-1.409a2.25 2.25 0 013.182 0l2.909 2.909M3.75 21h16.5A2.25 2.25 0 0022.5 18.75V5.25A2.25 2.25 0 0020.25 3H3.75A2.25 2.25 0 001.5 5.25v13.5A2.25 2.25 0 003.75 21z" />
          </svg>
        </button>
        <input
          ref={fileInputRef}
          type="file"
          accept="image/*"
          className="hidden"
          onChange={handleFileChange}
        />
        <input
          className="flex-1 rounded-full border border-border bg-input px-4 py-2.5 text-sm text-foreground placeholder:text-muted-foreground outline-none focus:border-ring focus:ring-2 focus:ring-ring/50 transition-all"
          placeholder="输入消息..."
          value={inputText}
          onChange={(e) => setInputText(e.target.value)}
          onKeyDown={handleKeyDown}
          onPaste={handlePaste}
        />
        <button
          onClick={handleSend}
          disabled={!inputText.trim() && !pendingImage}
          className="shrink-0 rounded-full bg-primary px-5 py-2.5 text-sm font-medium text-primary-foreground hover:bg-primary/90 active:bg-primary/80 transition-colors disabled:opacity-40 disabled:cursor-not-allowed"
        >
          发送
        </button>
      </div>
    </div>
  );
}

// 关机弹窗
function ShutdownAlert() {
  const shutdownAlert = useChatStore((s) => s.shutdownAlert);
  if (!shutdownAlert) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm">
      <div className="bg-popover border border-border rounded-2xl shadow-2xl px-8 py-6 max-w-sm mx-4 text-center">
        <div className="text-3xl mb-3">⚡</div>
        <p className="text-sm text-popover-foreground mb-5">{shutdownAlert}</p>
        <button
          onClick={() => useChatStore.setState({ shutdownAlert: null })}
          className="rounded-full bg-primary px-5 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90 transition-colors"
        >
          知道了
        </button>
      </div>
    </div>
  );
}

// 拉黑弹窗
function BlockAlert() {
  const blockAlert = useChatStore((s) => s.blockAlert);
  if (!blockAlert) return null;
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm">
      <div className="bg-popover border border-border rounded-2xl shadow-2xl px-8 py-6 max-w-sm mx-4 text-center">
        <div className="text-3xl mb-3">🚫</div>
        <p className="text-sm text-popover-foreground mb-5">{blockAlert}</p>
        <button
          onClick={() => useChatStore.setState({ blockAlert: null })}
          className="rounded-full bg-primary px-5 py-2 text-sm font-medium text-primary-foreground hover:bg-primary/90 transition-colors"
        >
          知道了
        </button>
      </div>
    </div>
  );
}

// 历史快照列表弹窗
function HistoryModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const [snapshots, setSnapshots] = useState<SessionSnapshot[]>([]);
  const [loading, setLoading] = useState(false);
  const [restoring, setRestoring] = useState<string | null>(null);

  useEffect(() => {
    if (!open) return;
    setLoading(true);
    fetchSnapshots()
      .then(setSnapshots)
      .catch((e) => console.error('加载快照列表失败:', e))
      .finally(() => setLoading(false));
  }, [open]);

  const handleRestore = async (name: string) => {
    setRestoring(name);
    try {
      const ok = await restoreSnapshot(name);
      if (ok) {
        useChatStore.setState({ tokenCount: 0 });
        // 刷新列表以更新当前指针标记
        const fresh = await fetchSnapshots();
        setSnapshots(fresh);
        onClose();
      }
    } catch (e) {
      console.error('恢复快照失败:', e);
    } finally {
      setRestoring(null);
    }
  };

  if (!open) return null;

  const formatSize = (bytes: number) => {
    if (bytes < 1024) return `${bytes}B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)}KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)}MB`;
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 backdrop-blur-sm" onClick={onClose}>
      <div
        className="bg-popover border border-border rounded-2xl shadow-2xl max-w-md w-full mx-4 max-h-[80vh] flex flex-col"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-3 border-b border-border">
          <h3 className="text-sm font-semibold text-popover-foreground">历史快照</h3>
          <button
            onClick={onClose}
            className="text-muted-foreground hover:text-foreground text-sm w-6 h-6 rounded-full hover:bg-accent flex items-center justify-center"
          >
            x
          </button>
        </div>
        <div className="flex-1 overflow-y-auto p-2">
          {loading ? (
            <div className="text-center text-xs text-muted-foreground py-8">加载中...</div>
          ) : snapshots.length === 0 ? (
            <div className="text-center text-xs text-muted-foreground py-8">暂无快照</div>
          ) : (
            <ul className="space-y-1">
              {snapshots.map((s) => (
                <li
                  key={s.name}
                  className={`flex items-center gap-3 px-3 py-2 rounded-lg transition-colors ${
                    s.isCurrent ? 'bg-primary/10' : 'hover:bg-accent/50'
                  }`}
                >
                  <div className="flex-1 min-w-0">
                    <div className="flex items-center gap-1.5">
                      <span className="text-xs font-medium text-popover-foreground truncate">
                        {new Date(s.createdAt).toLocaleString('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' })}
                      </span>
                      {s.isCurrent && (
                        <span className="text-[10px] text-primary shrink-0">当前</span>
                      )}
                    </div>
                    <div className="text-[10px] text-muted-foreground">{formatSize(s.size)}</div>
                  </div>
                  <button
                    onClick={() => handleRestore(s.name)}
                    disabled={restoring !== null || s.isCurrent}
                    className="shrink-0 text-xs text-primary hover:bg-primary/10 px-2.5 py-1 rounded-md transition-colors disabled:opacity-40"
                  >
                    {restoring === s.name ? '恢复中...' : s.isCurrent ? '当前' : '恢复'}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </div>
  );
}

// AssistantSidebar 布局：左侧控制面板（宽），右侧聊天（窄）
function AssistantSidebar({ onOpenHistory }: { onOpenHistory: () => void }) {
  return (
    <Group orientation="horizontal" className="h-full">
      <Panel
        defaultSize={70}
        minSize={30}
        className="flex flex-col bg-card border-r border-border"
      >
        <StatusBar onOpenHistory={onOpenHistory} />
        <div className="mt-auto border-t border-border">
          <TokenBar />
        </div>
      </Panel>
      <Separator className="w-1 bg-border hover:bg-muted-foreground/50 transition-colors" />
      <Panel defaultSize={40} minSize={30}>
        <ThreadPrimitive.Root className="flex-1 flex flex-col h-full overflow-hidden">
          <ThreadPrimitive.Viewport className="flex-1 overflow-y-auto py-4">
            <AuiIf condition={(s) => s.thread.isEmpty}>
              <div className="flex items-center justify-center h-full text-muted-foreground text-sm">
                点击左侧「启动」开始对话
              </div>
            </AuiIf>
            <ThreadPrimitive.Messages>
              {({ message }) => {
                if (message.role === 'user') return <UserMessage />;
                const kind = getMessageKind(message);
                const text = getMessageText(message);
                switch (kind) {
                  case 'thought':
                    return <ThoughtMessage text={text} />;
                  case 'tool':
                    return <ToolCallMessage text={text} />;
                  case 'error':
                    return <ErrorMessage text={text} />;
                  default:
                    return <AssistantMessage />;
                }
              }}
            </ThreadPrimitive.Messages>
          </ThreadPrimitive.Viewport>
          <WaitingIndicator />
          <PendingMessages />
          <Composer />
        </ThreadPrimitive.Root>
      </Panel>
    </Group>
  );
}

export default function App() {
  const [historyOpen, setHistoryOpen] = useState(false);
  return (
    <MaidRuntimeProvider>
      <div className="h-screen flex bg-background text-foreground">
        <AssistantSidebar onOpenHistory={() => setHistoryOpen(true)} />
        <ShutdownAlert />
        <BlockAlert />
        <HistoryModal open={historyOpen} onClose={() => setHistoryOpen(false)} />
      </div>
    </MaidRuntimeProvider>
  );
}
