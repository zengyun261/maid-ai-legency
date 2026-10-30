# maid-rem-ai（legency）

一个实验性AI聊天机器人小项目，旨在探索AI聊天的“真人”感。

C# 后端跑 Agent 循环，React 前端通过 SSE 实时显示内心独白、工具调用和消息。

## 主要功能

* 无Reasoning的模拟思考（极不稳定，容易循环、自我模仿）
* 主动消息循环
* 自定义人设

## 其它功能

* 长期记忆（基于图谱）
* 历史记录压缩
* 快照保存与恢复

## 项目结构

```
AI-old/
├── AI/          # 后端：ASP.NET Core (.NET 10)，监听 5162
├── maid-chat/   # 前端：Vite + React 19 + @assistant-ui，默认 5173
└── AI.sln
```

## 环境要求

| 依赖 | 版本 | 说明 |
| --- | --- | --- |
| .NET SDK | 10.x | `dotnet --version` 检查 |
| Node.js / npm | 20+ | Vite 8 要求 |
| 模型服务 | OpenAI 兼容 API | 默认 `http://127.0.0.1:8100/v1` |

## 启动方式

先起后端，再起前端。前端是纯前端直连后端 SSE，后端没起来时页面会显示「未连接」。

### 1. 后端

```bash
cd AI
dotnet run
```

- 监听 `http://localhost:5162`（由 `AI/appsettings.json` 的 `Urls` 决定）。
- **必须在 `AI/` 目录下执行**：`Character.md`、`Engine.md`、`appsettings.json` 以及 `data/` 目录都是按「当前工作目录」解析的，换目录启动会报找不到文件或连配置都读不到。
- 首次运行会自动创建 `AI/data/`：
  - `images/` 图片
  - `sessions/history/` 会话快照，`sessions/current` 是当前游标
  - `memory.jsonl` 知识图谱（记忆）
  - `timestamp` 上次交互时间
- 模型配置在 `AI/appsettings.json` 的 `OpenAI` 节：

  ```json
  "OpenAI": {
    "Endpoint": "http://127.0.0.1:8100/v1",
    "ApiKey": "1234",
    "Model": "Qwen3.8-27B-oQ4e-mtp"
  }
  ```

  需要走代理时，把 `"~Proxy"` 改名为 `"Proxy"` 并填地址。

### 2. 前端

```bash
cd maid-chat
npm install     # 首次；node_modules 已存在时可跳过
npm run dev
```

然后打开 http://localhost:5173 。

- 后端地址硬编码在 `maid-chat/src/lib/chatStore.ts` 的 `API_BASE`（默认 `http://localhost:5162`），后端换端口时改这里。
- 项目里同时有 `bun.lock`，用 `bun install` / `bun run dev` 也可以，两种包管理器选一种即可。

## 使用流程

1. 点左侧「启动」，后端开始跑 Agent 循环（会重建会话、清空上下文）。
2. 下方输入框发消息：`Enter` 发送，`Shift+Enter` 换行，支持粘贴或选择图片。
3. 顶部按钮：
   - 「压缩」：把较早的对话压成摘要，保留最近几轮。
   - 「保存」：把当前会话存成一个快照。
   - 「恢复」/「压缩并恢复」：从当前快照恢复会话并继续跑。
   - 「历史」：查看并切换到任意历史快照。
4. 底部「上下文」条显示 token 占用，超过阈值会变色，点一下可展开明细。

## 常见问题

**前端报 `Maximum update depth exceeded. The result of getSnapshot should be cached to avoid an infinite loop.`**

这是 `@assistant-ui/*` 的版本问题（0.14.25+ 的 `tap` 包和 `core` 的缓存策略互相触发死循环）。`maid-chat/package.json` 已用 `overrides` 把四个包钉在验证过的组合上：

```
@assistant-ui/react 0.14.24
@assistant-ui/core  0.2.19
@assistant-ui/store 0.2.19
@assistant-ui/tap   0.9.3
```

不要随手升级这几个包；确实要升（比如 0.15.x）就先打开页面确认不报错。

**后端报 `Address already in use`**

5162 已被占用，多半是已经有一个实例在跑，先关掉它。

**后端报 `Could not find file 'Character.md'`**

当前工作目录不对，`cd AI` 后再 `dotnet run`。

**前端一直显示「未连接」**

后端没启动，或者端口不是 5162（检查 `API_BASE`）。

**改了 `Character.md` / `Engine.md` 想立即生效**

点一次「启动」或「恢复」会重新加载这两个文件（`MaidAgent.ReloadAgent()`），不用重启后端。
