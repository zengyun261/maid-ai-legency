import { create } from 'zustand';
import type { ThreadMessageLike } from '@assistant-ui/react';

const API_BASE = 'http://localhost:5162';

interface PendingMessage {
  id: string;
  text: string;
  imageId?: string;
  createdAt: Date;
}

interface ChatState {
  messages: ThreadMessageLike[];
  pendingMessages: PendingMessage[];
  connected: boolean;
  running: boolean;
  waiting: boolean;
  waitingEndAt: number | null;
  waitingRemark: string;
  tokenCount: number;
  maxTokens: number;
  compacting: boolean;
  shutdownAlert: string | null;
  blockAlert: string | null;

  addMessage: (msg: ThreadMessageLike) => void;
  appendToLastAssistantText: (text: string) => void;
  setConnected: (v: boolean) => void;
  setRunning: (v: boolean) => void;
  setWaiting: (v: boolean, seconds?: number, remark?: string, endAt?: number) => void;
  setTokenCount: (v: number) => void;
  setMaxTokens: (v: number) => void;
  sendMessage: (text: string, imageBase64?: string) => void;
  startLoop: () => Promise<void>;
  stopLoop: () => Promise<void>;
  fetchStatus: () => Promise<void>;
  compact: () => Promise<void>;
  compactAndRestore: () => Promise<void>;
  skipWait: () => Promise<void>;
  saveSession: () => Promise<void>;
  restoreSession: () => Promise<void>;
}

let idCounter = 0;
const nextId = () => String(++idCounter);

export const useChatStore = create<ChatState>(set => ({
    messages: [],
    pendingMessages: [],
  connected: false,
  running: false,
  waiting: false,
  waitingEndAt: null,
  waitingRemark: '',
  tokenCount: 0,
  maxTokens: 32768,
  compacting: false,
  shutdownAlert: null,
  blockAlert: null,

  addMessage: (msg) =>
    set((s) => ({ messages: [...s.messages, msg] })),

  appendToLastAssistantText: (text) =>
    set((s) => {
      const msgs = [...s.messages];
      for (let i = msgs.length - 1; i >= 0; i--) {
        if (msgs[i].role === 'assistant') {
          const lastContent = msgs[i].content;
          const lastPart = lastContent[lastContent.length - 1];
          if (lastPart?.type === 'text') {
            const newContent = [...lastContent];
            newContent[newContent.length - 1] = { ...lastPart, text: lastPart.text + text };
            msgs[i] = { ...msgs[i], content: newContent, status: { type: 'running' as const } };
          } else {
            msgs[i] = { ...msgs[i], content: [...lastContent, { type: 'text' as const, text }], status: { type: 'running' as const } };
          }
          break;
        }
      }
      return { messages: msgs };
    }),

  setConnected: (v) => set({ connected: v }),
  setRunning: (v) => set({ running: v }),
  setWaiting: (v, seconds, remark, endAt) => set((s) => {
    const base = {
      waiting: v,
      waitingEndAt: endAt ?? (v && seconds ? Date.now() + seconds * 1000 : null),
      waitingRemark: remark ?? '',
    };
    // 退出等待时，删除"等待回复"工具消息
    if (!v && s.waiting) {
      return {
        ...base,
        messages: s.messages.filter((m) => {
          if (m.role !== 'assistant') return true;
          const kind = (m as any).metadata?.custom?.kind;
          if (kind !== 'tool') return true;
          const part = m.content.find((c) => c.type === 'text');
          const text = part && 'text' in part ? part.text : '';
          return !text.includes('等待回复');
        }),
      };
    }
    return base;
  }),
  setTokenCount: (v) => set({ tokenCount: v }),
  setMaxTokens: (v) => set({ maxTokens: v }),

  sendMessage: (text: string, imageBase64?: string) => {
    const pendingId = nextId();
    const isImage = !!imageBase64;
    // 先加 pending，文本暂时用占位
    set((s) => ({
      pendingMessages: [...s.pendingMessages, { id: pendingId, text: isImage && !text ? '' : text, imageId: undefined, createdAt: new Date() }],
    }));
    const body = imageBase64
      ? JSON.stringify({ text, image: imageBase64 })
      : text;
    fetch(`${API_BASE}/new_msg`, {
      method: 'POST',
      headers: imageBase64 ? { 'Content-Type': 'application/json' } : {},
      body,
    })
      .then((res) => res.json())
      .then((data: { imageId?: string }) => {
        // 后端返回 imageId，更新 pending 消息
        if (data.imageId) {
          set((s) => ({
            pendingMessages: s.pendingMessages.map((m) =>
              m.id === pendingId ? { ...m, imageId: data.imageId } : m
            ),
          }));
        }
      })
      .catch((e) => {
        console.error('发送消息失败:', e);
        set((s) => ({ pendingMessages: s.pendingMessages.filter((m) => m.id !== pendingId) }));
      });
  },

  startLoop: async () => {
    try {
      const res = await fetch(`${API_BASE}/loop/start`, { method: 'POST' });
      const data = await res.json();
      set({ running: data.running, messages: [], pendingMessages: [] });
    } catch (e) {
      console.error('启动循环失败:', e);
    }
  },

  stopLoop: async () => {
    try {
      const res = await fetch(`${API_BASE}/loop/stop`, { method: 'POST' });
      const data = await res.json();
      set({ running: data.running });
    } catch (e) {
      console.error('停止循环失败:', e);
    }
  },

  fetchStatus: async () => {
    try {
      const res = await fetch(`${API_BASE}/loop/status`);
      const data = await res.json();
      set({ running: data.running });
    } catch (e) {
      console.error('获取状态失败:', e);
    }
  },

  skipWait: async () => {
    try {
      await fetch(`${API_BASE}/skip_wait`, { method: 'POST' });
    } catch (e) {
      console.error('跳过等待失败:', e);
    }
  },
  compact: async () => {
    try {
      set({ compacting: true });
      await fetch(`${API_BASE}/compact`, { method: 'POST' });
      set({ tokenCount: 0, compacting: false });
    } catch (e) {
      console.error('压缩上下文失败:', e);
      set({ compacting: false });
    }
  },
  compactAndRestore: async () => {
    try {
      set({ compacting: true });
      await fetch(`${API_BASE}/session/compact_and_restore`, { method: 'POST' });
      set({ tokenCount: 0, compacting: false });
    } catch (e) {
      console.error('压缩并恢复失败:', e);
      set({ compacting: false });
    }
  },

  saveSession: async () => {
    try {
      await fetch(`${API_BASE}/session/save`, { method: 'POST' });
    } catch (e) {
      console.error('保存会话失败:', e);
    }
  },

  restoreSession: async () => {
    try {
      await fetch(`${API_BASE}/session/restore`, { method: 'POST' });
      set({ tokenCount: 0 });
    } catch (e) {
      console.error('恢复会话失败:', e);
    }
  },
}));

export interface SessionSnapshot {
  name: string;
  createdAt: string;
  size: number;
  isCurrent: boolean;
}

export async function fetchSnapshots(): Promise<SessionSnapshot[]> {
  const res = await fetch(`${API_BASE}/sessions/history`);
  return res.json();
}

export async function restoreSnapshot(name: string): Promise<boolean> {
  const res = await fetch(`${API_BASE}/sessions/history/${name}/restore`, { method: 'POST' });
  return res.ok;
}

// SSE 连接管理
export function connectSSE() {
  let currentAssistantId: string | null = null;

  const es = new EventSource(`${API_BASE}/chat/events`);

  es.onopen = () => {
    useChatStore.getState().setConnected(true);
  };

  es.onerror = () => {
    useChatStore.getState().setConnected(false);
  };

  es.addEventListener('loop_status', (e) => {
    const data = JSON.parse(e.data) as { running: boolean };
    useChatStore.getState().setRunning(data.running);
  });

  es.addEventListener('model_info', (e) => {
    const data = JSON.parse(e.data) as { maxTokens: number };
    if (data.maxTokens) {
      useChatStore.getState().setMaxTokens(data.maxTokens);
    }
  });

  es.addEventListener('thought', (e) => {
    const data = JSON.parse(e.data) as { text: string };
    if (!data.text) return;
    const state = useChatStore.getState();
    if (state.waiting) {
      useChatStore.getState().setWaiting(false);
    }

    if (currentAssistantId) {
      state.appendToLastAssistantText(data.text);
    } else {
      currentAssistantId = nextId();
      state.addMessage({
        role: 'assistant',
        content: [{ type: 'text', text: data.text }],
        id: currentAssistantId,
        createdAt: new Date(),
        status: { type: 'running' },
        metadata: { custom: { kind: 'thought' } },
      });
    }
  });

  es.addEventListener('message', (e) => {
    const data = JSON.parse(e.data) as { text: string };
    if (!data.text) return;

    const state = useChatStore.getState();
    if (state.waiting) {
      useChatStore.getState().setWaiting(false);
    }

    const msgs = [...state.messages];
    for (let i = msgs.length - 1; i >= 0; i--) {
      if (msgs[i].role === 'assistant') {
        msgs[i] = { ...msgs[i], status: { type: 'complete' as const, reason: 'stop' as const } };
        break;
      }
    }
    useChatStore.setState({ messages: msgs });

    const id = nextId();
    useChatStore.getState().addMessage({
      role: 'assistant',
      content: [{ type: 'text', text: data.text }],
      id,
      createdAt: new Date(),
      status: { type: 'complete', reason: 'stop' },
      metadata: { custom: { kind: 'message' } },
    });
    currentAssistantId = null;
  });

  es.addEventListener('tool', (e) => {
    const data = JSON.parse(e.data) as { name: string; label: string; result?: string };

    const state = useChatStore.getState();
    if (state.waiting) {
      state.setWaiting(false);
    }

    const msgs = [...state.messages];
    for (let i = msgs.length - 1; i >= 0; i--) {
      if (msgs[i].role === 'assistant') {
        msgs[i] = { ...msgs[i], status: { type: 'complete' as const, reason: 'stop' as const } };
        break;
      }
    }
    useChatStore.setState({ messages: msgs });

    const id = nextId();
    const text = data.result ? `${data.label} → ${data.result}` : data.label;
    useChatStore.getState().addMessage({
      role: 'assistant',
      content: [{ type: 'text', text }],
      id,
      createdAt: new Date(),
      status: { type: 'complete', reason: 'stop' },
      metadata: { custom: { kind: 'tool' } },
    });
    currentAssistantId = null;
  });

  es.addEventListener('waiting', (e) => {
    const data = JSON.parse(e.data) as { seconds: number; remark: string; endAt?: number };

    const state = useChatStore.getState();
    const msgs = [...state.messages];
    for (let i = msgs.length - 1; i >= 0; i--) {
      if (msgs[i].role === 'assistant') {
        msgs[i] = { ...msgs[i], status: { type: 'complete' as const, reason: 'stop' as const } };
        break;
      }
    }
    useChatStore.setState({ messages: msgs });
    useChatStore.getState().setWaiting(true, data.seconds, data.remark, data.endAt);

    currentAssistantId = null;
  });

  // 用户消息（Agent 消费后回显）
  es.addEventListener('user_message', (e) => {
    const data = JSON.parse(e.data) as { text: string; imageId?: string };
    const state = useChatStore.getState();
    if (state.waiting) {
      useChatStore.getState().setWaiting(false);
    }
    const pendingIdx = state.pendingMessages.findIndex((m) =>
      data.imageId
        ? (m.imageId === data.imageId || (!m.imageId && (m.text ? data.text.startsWith(m.text) : true)))
        : m.text === data.text
    );
    if (pendingIdx !== -1) {
      const pending = state.pendingMessages[pendingIdx];
      const content: any[] = [{ type: 'text' as const, text: data.text }];
      if (data.imageId) {
        content.push({ type: 'image' as const, image: `${API_BASE}/images/${data.imageId}` });
      }
      useChatStore.setState((s) => ({
        messages: [...s.messages, {
          role: 'user' as const,
          content,
          id: pending.id,
          createdAt: pending.createdAt,
        }],
        pendingMessages: s.pendingMessages.filter((_, i) => i !== pendingIdx),
      }));
    } else {
      const content: any[] = [{ type: 'text' as const, text: data.text }];
      if (data.imageId) {
        content.push({ type: 'image' as const, image: `${API_BASE}/images/${data.imageId}` });
      }
      state.addMessage({
        role: 'user',
        content,
        id: nextId(),
        createdAt: new Date(),
      });
    }
  });

  // 压缩完成
  es.addEventListener('compacted', () => {
    useChatStore.setState({ tokenCount: 0, compacting: false });
  });

  // 关机
  es.addEventListener('shutdown', () => {
    useChatStore.setState({ shutdownAlert: '对方调用了\'关机\'' });
  });

  // 拉黑
  es.addEventListener('block', () => {
    useChatStore.setState({ blockAlert: '对方调用了\'拉黑\'' });
  });

  es.addEventListener('turn_start', () => {
    currentAssistantId = null;
    useChatStore.getState().setWaiting(false);
  });

  es.addEventListener('turn_end', () => {
    currentAssistantId = null;
    const state = useChatStore.getState();
    const msgs = [...state.messages];
    for (let i = msgs.length - 1; i >= 0; i--) {
      if (msgs[i].role === 'assistant') {
        msgs[i] = { ...msgs[i], status: { type: 'complete' as const, reason: 'stop' as const } };
        break;
      }
    }
    useChatStore.setState({ messages: msgs });
  });

  es.addEventListener('loop_started', () => {
    useChatStore.getState().setRunning(true);
  });

  es.addEventListener('loop_stopped', () => {
    useChatStore.getState().setRunning(false);
  });

  es.addEventListener('agent_error', (e) => {
    const data = JSON.parse(e.data) as { text: string };
    currentAssistantId = nextId();
    useChatStore.getState().addMessage({
      role: 'assistant',
      content: [{ type: 'text', text: `❌ ${data.text}` }],
      id: currentAssistantId,
      createdAt: new Date(),
      metadata: { custom: { kind: 'error' } },
    });
  });

  es.addEventListener('usage', (e) => {
    const data = JSON.parse(e.data) as { totalTokens: number; inputTokens: number; outputTokens: number };
    useChatStore.getState().setTokenCount(data.inputTokens);
  });

  return es;
}
