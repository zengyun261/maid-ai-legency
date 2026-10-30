import { type ReactNode, useEffect } from 'react';
import {
  useExternalStoreRuntime,
  AssistantRuntimeProvider,
  type ThreadMessageLike,
} from '@assistant-ui/react';
import { useChatStore, connectSSE } from '../lib/chatStore';

export function MaidRuntimeProvider({ children }: { children: ReactNode }) {
  const messages = useChatStore((s) => s.messages);
  const sendMessage = useChatStore((s) => s.sendMessage);

  // 连接 SSE
  useEffect(() => {
    const es = connectSSE();
    return () => es.close();
  }, []);

  const runtime = useExternalStoreRuntime({
    messages,
    isRunning: false,
    convertMessage: (msg: ThreadMessageLike) => msg,
    onNew: async (message) => {
      const textPart = message.content.find((c) => c.type === 'text');
      const imagePart = message.content.find((c) => c.type === 'image');
      const text = textPart && 'text' in textPart ? textPart.text : '';
      const imageBase64 = imagePart && 'image' in imagePart ? (imagePart as any).image : undefined;
      // 图片部分是 URL（来自 /images/{id}），不再作为 base64 发送
      await sendMessage(text, imageBase64 && !imageBase64.startsWith('http') ? imageBase64 : undefined);
    },
  });

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      {children}
    </AssistantRuntimeProvider>
  );
}
