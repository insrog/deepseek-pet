# AI 桌宠增量开发交付文档（v1.1 / v1.2）

> 基线：v1.0（桌宠 + 每日任务，已完成并验收）。
> 本文档对应「AI 桌宠——剩余开发需求（增量）」的全部十一大模块，及 v1.2 的配置体验优化。
> 原则：**在现有代码基础上增量开发，不破坏、不回退、不重写已完成功能；新能力全部与现有任务系统直接打通。**

---

## 0. 概览：新增了什么

### v1.2（配置体验优化）
| 模块 | 内容 | 关键文件 |
| --- | --- | --- |
| 独立设置界面 | 可视化配置四页签（AI 对话 / 联网搜索 / 人设卡 / 关于）；API Key 明文开关、**一键测试连接**、人设一键恢复默认；保存写回 `appconfig.json` + `persona.json` 并**立即生效**；未知字段（用户备注）原样保留 | `Windows/SettingsWindow.xaml(.cs)` `Services/PersonaStore.cs` `Services/AgentService.cs` |
| 菜单统一 | 桌宠右键 / 贴边把手右键 / 托盘右键**三处共用同一份菜单定义与同一套动作**（单一数据源 `AppMenu.Build`，每次展开实时求值勾选态）；WPF 与 WinForms 各自渲染 | `Services/AppMenu.cs` `Services/TrayService.cs` `Windows/PetWindow.xaml(.cs)` |

### v1.1（十一大模块）

| 模块 | 内容 | 关键文件 |
| --- | --- | --- |
| AI 对话 | OpenAI 兼容 API（默认 DeepSeek，可切任意兼容端点/本地 Ollama），**SSE 流式逐 token**，独立线程不阻塞 UI，上下文 20–30 轮裁剪，对话落库重启恢复 | `Agent/LlmClient.cs` `Agent/ChatSession.cs` `Services/ChatStore.cs` |
| 人设卡 | `persona.json` 本地文件，**改文件免重启生效**；只发送「精简摘要 + 动态状态」给 LLM；动态状态（心情/好感度）由 Agent 维护 | `Models/Persona.cs` `Services/PersonaStore.cs` |
| 意图分流 | 本地规则预判（零延迟、离线可用）+ LLM Function Calling 双层；单轮工具调用 ≤2 次 | `Agent/IntentRouter.cs` `Agent/AgentService.cs` |
| Agent 工具集 | `add_reminder` / `get_tasks` / `web_search` 三个工具，直接读写现有任务系统 | `Agent/Tools/*.cs` |
| 联网搜索 | Tavily / SearXNG 双后端；Key 只从配置/环境变量读取；结果本地缓存；失败兜底不编造 | `Services/SearchClient.cs` |
| 任务打通 | 自然语言创建提醒；询问待办；**定时提醒先调 LLM 生成自然提醒语**（失败回退模板）；**休眠唤醒立即补发**；当天只提醒一次持久化 | `App.xaml.cs` `Services/Scheduler.cs` |
| 分层重构 | UI / 对话Agent / 任务数据 / 工具调用 / 系统调度 五层；配置全外置；统一异常；关键链路日志 | 见 §7 |
| 性能 | 空闲 CPU 0.00%；动画帧率自适应（平时 20/安静 10/隐藏 0）；气泡增量追加局部重绘；SQLite 全局复用 | 见 §8 |
| 稳定性 | 线程异常全捕获；异常退出恢复（SQLite WAL）；Agent 看门狗 | 见 §9 |
| 交付物 | 本文档 + 人设卡预设 + tools JSON Schema + 鲸鱼娘素材 | `docs/` `presets/` |

---

## 1. AI 对话模块

### 1.1 接口与模型
- 协议：OpenAI 兼容 `POST {baseUrl}/chat/completions`（`Authorization: Bearer <apiKey>`）
- 默认 `baseUrl=https://api.deepseek.com/v1`，`model=deepseek-chat`
- 切换：编辑 `appconfig.json` 的 `baseUrl` / `model` 即可（如本地 Ollama：`http://127.0.0.1:11434/v1`）
- 超时：`timeoutSeconds`（默认 60s，作用到整个流式请求）；网络/服务错误统一转为失败结果，**绝不抛出到 UI**

### 1.2 流式输出（逐 token）
`LlmClient.StreamAsync` 以 `HttpCompletionOption.ResponseHeadersRead` 读取 SSE 流，逐行解析 `data: {...}`，每个增量先回调 `onDelta`，再由 Agent 切回 UI 线程增量追加到气泡。

```csharp
var r = await _llm.StreamAsync(messages,
    delta => { fullText.Append(delta); InvokeUi(() => DeltaReceived?.Invoke(delta)); },
    tools, ct);
```

SSE 解析要点（`LlmClient.ReadStreamAsync`）：
- `data: [DONE]` 结束；`:` 开头为心跳注释，忽略；坏行跳过不中断
- `delta.content` → 文本增量
- `delta.tool_calls[i]`：按 `index` 累积 `id` / `function.name` / `function.arguments`（参数分片拼接）
- `stream_options.include_usage` 打开以回收 token 用量

### 1.3 线程模型
- `AgentService.Send()` 从 UI 线程调用后立即返回；实际处理经 `Task.Run` 在后台串行执行（`SemaphoreSlim` 门闩）
- 新消息到达会先取消上一条未完成的请求（聊天软件的自然行为）；`CancellationToken` 贯穿 LLM 与工具执行
- 所有 UI 回调（`DeltaReceived` / `ResponseCompleted` / `StatusChanged` / `ErrorOccurred`）统一经 `Dispatcher.BeginInvoke` 切回 UI 线程

### 1.4 上下文裁剪与重启恢复
- 短期记忆只保留最近 `memoryRounds` 轮（默认 24，需求 20–30），一份用户 + 一份助手算一轮（`ChatSession.TrimMemory`）
- 每条消息即时写入 SQLite `chat_messages` 表；启动时 `ChatSession.Restore()` 恢复最近 `memoryRounds*2` 条
- 数据库侧另有 400 条的环形上限，避免无限增长；**不做长期向量记忆**（按需求）

---

## 2. 人设卡系统

### 2.1 文件与热更新
- 路径：数据目录 `persona.json`（或 `appconfig.json` 的 `personaPath` 指定任意位置）
- 首次运行自动生成**鲸鱼娘预设**（见 `presets/WhaleChan.persona.json`，素材出处见 §12）
- `FileSystemWatcher` 监听文件变更（600ms 防抖）→ 重新加载 → **免重启生效**；JSON 带注释也能读（`ReadCommentHandling.Skip`）

### 2.2 字段结构
```jsonc
{
  "name": "鲸鱼娘",              // 名字
  "creature": "…",               // 身份一句话
  "personality": "…",            // 性格
  "speechStyle": "…",            // 说话风格
  "background": "…",             // 背景
  "rules": ["…"],                // 必须遵守的行为规则
  "catchphrases": ["…"],         // 口头禅参考
  "greeting": "…",               // 首次问候语
  "summary": ""                  // 留空 = 自动生成精简摘要
}
```

### 2.3 只发送「精简摘要 + 动态状态」
完整人设卡可能很长，注入 LLM 的只是：
1. **精简摘要**（`PersonaStore.BuildSummary()`）：`summary` 非空直接用；否则自动拼装
   —— 性格截 180 字、说话风格截 150 字、背景截 120 字、最多 6 条规则，总量约 500 字；
2. **动态状态**：`【当前状态】心情：X｜好感度：N/100`
3. 补充上下文：当前时间（含星期）、今日提醒概览（最多 3 条）、Agent 记住的最近印象

### 2.4 动态状态的维护
- 存储：SQLite `kv` 表 `persona_state`（与 persona.json 分离，编辑人设卡不会覆盖状态）
- 更新策略（省 token 的工程决策）：**每 5 轮用户消息**触发一次轻量后台请求
  （`temperature=0.2`、`max_tokens=120`），要求模型输出 `{mood, affection, note}` JSON；
  程序侧再加一道 `±5/次` 的限幅，好感度范围 0–100
- 失败静默忽略（保持原状态），不影响主线对话

---

## 3. 意图分流（双层）

```
用户消息
   │
   ▼
① 本地规则（IntentRouter.Classify）
   ├─ 命中「提醒/记一下/别忘了/待办/几点」等触发词
   ├─ 且时间表达式可解析（9点 / 9:30 / 下午3点半 / 晚上八点）
   ├─ 且清理后标题非空
   │        └─► 直接调 add_reminder 工具 → 打通任务系统（零网络延迟、无 API Key 也可用）
   │
   ▼
② LLM Function Calling（带 tools）
    ├─ 聊天 → 直接回复
    ├─ 需要任务/搜索 → 调用工具（单轮 ≤ maxToolCallsPerTurn，默认 2）
    └─ 超限 → 追加系统提示强制收尾，不再下发 tools
```

本地规则覆盖的重复表达：`每天/天天/每日`、`每N天`、`每周X`（含"一三五"多选）、`明天/后天/今天`、`周X`（取最近未来日期）→ 对应 `daily / interval / weekly / once` 四种模式。

> 设计取舍：本地层只在**完全可解析**时介入，宁缺毋滥——解析不了就交给 LLM 追问，避免"猜错用户意图"。

---

## 4. Agent 工具集（JSON Schema）

三个工具的定义在 `Agent/Tools/`，下发给 LLM 的 schema 由 `ToolRegistry.BuildSchemas()` 组装（`web_search` 在未配置搜索时自动不下发）。

### 4.1 `add_reminder`
```json
{
  "type": "function",
  "function": {
    "name": "add_reminder",
    "description": "为用户创建一条定时提醒（写入桌宠的任务列表）。当用户要求“提醒我/记一下/别忘了/待办某时刻做事”时调用。必须给出 24 小时制的 hour 与 minute。用户说“明天/后天/某月某日”时用 repeat=once 并给出 date；说“每天”用 repeat=daily；说“每N天”用 repeat=interval 并给出 interval_days；说“每周X”用 repeat=weekly 并给出 weekdays（1=周一…7=周日）。",
    "parameters": {
      "type": "object",
      "properties": {
        "title":         { "type": "string",  "description": "提醒内容，简洁明了（如“开会”“吃药”“交周报”）" },
        "hour":          { "type": "integer", "minimum": 0, "maximum": 23, "description": "小时（24 小时制）" },
        "minute":        { "type": "integer", "minimum": 0, "maximum": 59, "description": "分钟" },
        "repeat":        { "type": "string",  "enum": ["daily", "interval", "weekly", "once"],
                           "description": "daily=每天；interval=每N天；weekly=每周指定几天；once=仅一次（需 date）" },
        "interval_days": { "type": "integer", "minimum": 2, "maximum": 365, "description": "repeat=interval 时生效：每隔几天" },
        "weekdays":      { "type": "array", "items": { "type": "integer", "minimum": 1, "maximum": 7 },
                           "description": "repeat=weekly 时生效：星期几的列表（1=周一 … 7=周日）" },
        "date":          { "type": "string", "description": "repeat=once 时生效：日期 yyyy-MM-dd（按用户本地日历推算“明天”等）" }
      },
      "required": ["title", "hour", "minute"]
    }
  }
}
```
执行要点：标题截 60 字；`once` 的过去时刻直接拒绝并回告模型；创建前 `FindDuplicate` 查重，重复不重复建；成功后返回人类可读确认文本（如「已创建提醒：「开会」，仅 2026-10-15 的 09:00 提醒一次。」）。

### 4.2 `get_tasks`
```json
{
  "type": "function",
  "function": {
    "name": "get_tasks",
    "description": "查询用户的任务/提醒列表。当用户问“我有什么待办/今天要做什么/几点提醒/我的提醒列表”时调用。返回内容按 今天 / 其他 分组，包含时间、重复规则、启用状态与今日完成情况。",
    "parameters": {
      "type": "object",
      "properties": {
        "filter": { "type": "string", "enum": ["all", "today", "enabled"],
                    "description": "all=全部（默认）；today=今天会提醒的；enabled=仅已启用" }
      }
    }
  }
}
```

### 4.3 `web_search`
```json
{
  "type": "function",
  "function": {
    "name": "web_search",
    "description": "联网搜索实时信息（新闻、天气、价格、资料等）。当用户询问需要最新或外部信息的问题时调用。获得结果后，用你的话概括回答，并注明来源链接；若搜索失败就如实说明，不要编造。",
    "parameters": {
      "type": "object",
      "properties": {
        "query": { "type": "string", "description": "搜索关键词（简洁、聚焦，如“上海 明天 天气”）" }
      },
      "required": ["query"]
    }
  }
}
```

> 与现有系统打通：`add_reminder` / `get_tasks` 直接读写 v1.0 的 `TaskStore`（线程安全封装 `Snapshot()` / `Upsert()`），创建后 UI 面板经 `Changed` 事件即时刷新，与手动新建完全同源，**没有另起一套任务逻辑**。

---

## 5. 联网搜索

- 后端：`tavily`（默认，需 `searchApiKey`）｜`searxng`（自建/公共实例，填 `searxngUrl`）｜`none`（关闭）
- Key 来源：`appconfig.json` 或环境变量 `TAVILY_API_KEY`（**零硬编码**）
- 只取结构化字段：`title / url / snippet`（+ Tavily 聚合 `answer`），不把整页 HTML 塞给模型
- 本地缓存：SQLite `kv` 表 `search_cache:<sha1(provider|query|maxResults)>`，默认 30 分钟过期，同查询不重复打网络
- 失败兜底：返回明确的错误文本（如「未配置 Tavily API Key」），工具结果里指示模型**如实告知用户、不要编造**
- 未配置搜索时：`web_search` 不下发给 LLM（避免模型调用必然失败的工具）

---

## 6. 与现有任务提醒打通

| 场景 | 流程 |
| --- | --- |
| 自然语言创建 | 聊天「明天9点提醒我开会」→ 本地规则/LLM 工具 → `TaskStore.Upsert` → 面板即时出现「09:00 开会 仅10月15日」 |
| 询问待办 | 聊天「今天有什么要做的」→ LLM 调 `get_tasks` → 按今日/其他分组回答 |
| **定时提醒先说人设话** | `Scheduler` 触发 → **立即**弹模板提醒（保证必达）→ 后台请求 LLM 生成人设化提醒语（12s 超时）→ 气泡替换为自然语句；失败/未配置则保持模板句 |
| **休眠唤醒补发** | 双保险：① 订阅 `SystemEvents.PowerModeChanged`，系统一 `Resume` 立即检查并补发 2 小时内错过的提醒；② 定时器检测到时钟跳变（>2min 未 tick）同样重扫 |
| 当天只提醒一次 | 沿用 v1.0 记账：`LastFiredKey = yyyy-MM-dd HH:mm`，触发即原子落盘（SQLite 单事务）；重启不回放、同一时刻绝不重复 |

---

## 7. 分层结构（v1.1）

```
DeepSeekPet/
├─ App.xaml(.cs)                应用入口/组合根：单实例、三层异常、组件装配、关机落盘
├─ Config/
│  └─ AppConfig.cs              配置模型（appconfig.json + 环境变量，零硬编码）
├─ Agent/                       ← 对话 Agent 层
│  ├─ AgentService.cs           编排：分流 → LLM 轮次 → 工具循环 → 看门狗 → 状态维护
│  ├─ LlmClient.cs              OpenAI 兼容客户端：SSE 流式 + Function Calling
│  ├─ ChatSession.cs            短期记忆：20–30 轮裁剪、重启恢复
│  ├─ IntentRouter.cs           本地规则分流（提醒意图离线可解析）
│  └─ Tools/
│     ├─ ToolRegistry.cs        工具注册/按配置裁剪/统一容错执行
│     ├─ AddReminderTool.cs     工具①：创建提醒（打通任务系统）
│     ├─ GetTasksTool.cs        工具②：查询待办
│     └─ WebSearchTool.cs       工具③：联网搜索
├─ Models/                      ← 数据模型层
│  ├─ TaskItem.cs               （v1.0 + 增量扩展 OnceDate 一次性提醒）
│  ├─ AppSettings.cs            （v1.0）
│  ├─ Persona.cs                人设卡模型 + 鲸鱼娘默认预设
│  └─ ChatMessage.cs            对话消息 / 动态状态
├─ Services/                    ← 系统与存储层
│  ├─ Db.cs                     SQLite 全局单连接（WAL + 事务 + 线程安全）
│  ├─ TaskStore.cs              （v1.0 JSON → v1.1 SQLite，API 兼容 + 自动迁移）
│  ├─ ChatStore.cs              对话持久化（400 条环形上限）
│  ├─ ConfigService.cs          配置加载/保存/热更新
│  ├─ PersonaStore.cs           人设卡热更新 + 动态状态 + 摘要构建
│  ├─ SearchClient.cs           Tavily/SearXNG + 缓存 + 兜底
│  ├─ Scheduler.cs              （v1.0 + 增量：电源事件即时补发）
│  ├─ TrayService.cs            ← 新增：托盘图标（需求背景提到的能力，本版补齐）
│  ├─ AppMenu.cs                ← 新增（v1.2）：统一右键菜单定义（桌宠/把手/托盘单一数据源）
│  ├─ Native.cs / AutoStart.cs / MemoryGuard.cs / AppLogger.cs   （v1.0 原样保留）
├─ Windows/                     ← UI 层
│  ├─ PetWindow.xaml(.cs)       （v1.0 + 增量：Say() 说话气泡接线 + 统一右键菜单）
│  ├─ TaskPanel.xaml(.cs)       （v1.0 + 增量：聊天区/流式气泡/输入框）
│  ├─ SpeechBubble.xaml(.cs)    ← 新增：桌宠头顶说话气泡（AI 回复/提醒语）
│  ├─ SettingsWindow.xaml(.cs)  ← 新增（v1.2）：设置界面（AI / 搜索 / 人设卡 / 关于）
│  └─ TaskEditorWindow.xaml(.cs)（v1.0 + 增量：“仅一次”模式）
├─ presets/WhaleChan.persona.json   ← 新增：鲸鱼娘人设卡预设（交付物）
└─ docs/AI_INCREMENT.md             ← 本文档
```

**统一封装**：LLM（LlmClient 单例 HttpClient）、搜索（SearchClient 单例 HttpClient + 缓存）、DB（Db 全局单连接）各自唯一入口；**配置全外置**（appconfig.json + 环境变量）；**统一异常**（工具层 try/catch 转文本、Agent 层 catch 转错误提示、三层全局兜底）；**关键链路日志**（分流决策、工具执行、LLM 结果、状态更新、提醒触发/补发全部落日志）。

---

## 8. 性能实测（本机 1920×1080）

| 项目 | v1.0 实测 | v1.1 实测 | 说明 |
| --- | --- | --- | --- |
| 启动速度 | 0.3–0.6s | 0.4s（日志时间戳） | SQLite 打开 + 迁移均毫秒级 |
| 空闲 CPU | 0.00% | **0.00%**（10s 采样） | AI 模块完全事件驱动，空闲零轮询 |
| 内存（工作集） | 135–142MB | 空闲 124–127MB；**功能使用后稳态 150–162MB，空置自动回落**（实测 161.8→149.4MB） | 3 轮开/关面板仅 +3MB（渲染资源复用，无泄漏特征） |
| 提醒触发延迟 | 1ms | 1ms（实测 18:37:00.002） | v1.0 机制原样保留 |
| 托盘可见时间 | — | <2s | 托盘在 `StartUi` 前创建 |

> 内存说明：WPF 面板/聊天区首次渲染会带来一次性开销（+30MB 级），属于"功能初始化"而非持续增长；
> 实测空置后工作集自动回落（MemoryGuard 巡检在 >160MB 时后台触发 GC 回收 + 系统内存管理接管）。
> 强杀压力测试 + 4 分钟空置采样均无增长趋势（跨越 10 分钟两次读数 160→149MB）。

- **动画帧率自适应**（v1.0 保留）：活跃 20fps / 安静 10fps / 贴边隐藏 0fps；25s 无交互自动降频
- **气泡增量追加**：流式输出只更新最后一条气泡的 `TextBlock.Text`（`INotifyPropertyChanged` 触发局部重绘），**不重建消息列表**；历史消息仅保留最近 60 条 UI 气泡
- **SQLite 全局复用异步**：进程内单连接 + WAL + `busy_timeout`；普通写 2s 防抖合批（单事务），提醒记账单条立即落盘
- **懒加载**：面板（含聊天区）首次交互才创建；LLM/搜索客户端构造即轻量、首次调用才建连
- **自启感知低**：`--autostart --delay 3`，托盘先出、UI 延后，不抢登录焦点

---

## 9. 稳定性设计

- **线程异常捕获**：UI 线程（`DispatcherUnhandledException`）、后台任务（`TaskScheduler.UnobservedTaskException`）、进程级（`AppDomain.UnhandledException`）三层兜底；Agent 处理链路另有 try/catch → 用户可读的兜底文案
- **异常退出恢复**：SQLite WAL + 关键路径即时提交；强杀进程实测数据完整（`PRAGMA quick_check = ok`）；重启后对话/任务/状态原样恢复
- **看门狗**：60s 周期检查 Agent 处理时长，单次处理 >180s 强制取消并复位（防止网络挂起导致的"永远思考中"）
- **工具执行零风险**：任何工具异常都转成给模型的错误描述，绝不冒泡到 UI；未知工具请求记日志并回告
- **配置容错**：`appconfig.json` 非法值在 `Normalize()` 统一钳制；人设卡损坏回退内置预设；数据库不健康时仍继续尝试使用并告警

---

## 10. 配置说明（appconfig.json）

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `apiKey` | `""` | LLM 密钥；环境变量 `DEEPSEEK_PET_API_KEY` / `DEEPSEEK_API_KEY` 可覆盖（改文件热更新，环境变量需重启进程） |
| `baseUrl` | `https://api.deepseek.com/v1` | OpenAI 兼容端点（Ollama：`http://127.0.0.1:11434/v1`） |
| `model` | `deepseek-chat` | 模型名 |
| `timeoutSeconds` | `60` | 单次请求超时（5–300） |
| `temperature` | `1.0` | 采样温度（0–2） |
| `memoryRounds` | `24` | 短期记忆轮数（4–60，建议 20–30） |
| `searchProvider` | `tavily` | `tavily` / `searxng` / `none` |
| `searchApiKey` | `""` | Tavily 密钥；环境变量 `TAVILY_API_KEY` |
| `searxngUrl` | `""` | SearXNG 实例地址；环境变量 `DEEPSEEK_PET_SEARXNG_URL` |
| `searchCacheMinutes` | `30` | 搜索缓存时长（0=不缓存） |
| `searchMaxResults` | `5` | 单次搜索条数（1–10） |
| `enableWebSearch` | `true` | 是否启用搜索工具 |
| `maxToolCallsPerTurn` | `2` | 单轮工具调用上限（0–4，需求 ≤2） |
| `llmReminderPhrase` | `true` | 定时提醒是否先调 LLM 生成自然提醒语 |
| `personaPath` | `""` | 人设卡路径（空 = 数据目录 persona.json） |

修改 `appconfig.json` **免重启生效**（FileSystemWatcher 热更新，600ms 防抖）。

---

## 11. 数据库与迁移说明

**pet.db（SQLite，WAL 模式）** 替代 v1.0 的 tasks.json / settings.json：

```sql
tasks(id PK, title, mode, interval_days, week_mask, hour, minute,
      enabled, anchor_date, last_fired_key, done_date, once_date, sort_order)
kv(key PK, value)                    -- app_settings / persona_state / search_cache:*
chat_messages(id PK AUTOINCREMENT, role, content, tool_name, tool_call_id, created_at)
```

- **自动迁移**：首次启动若数据库为空且发现旧 `tasks.json` / `settings.json`，自动导入（含去重），旧文件保留为 `*.migrated` 备份
- **API 兼容**：`TaskStore` 公开方法签名与 v1.0 一致（`Load / Upsert / Remove / ToggleDone / PersistFired / SaveNow…`），UI 与 Scheduler 零改动
- **新增字段**：`once_date`（一次性提醒，支撑"明天9点提醒我"）；旧库自动 `ALTER TABLE` 补列
- **写入策略不变**：普通变更 2s 防抖合批（单事务）；关键路径（提醒记账）立即落盘

---

## 12. 人设卡预设与素材出处

预设文件：`presets/WhaleChan.persona.json`（复制到数据目录 `persona.json` 即可用；程序首次运行自动生成同款）。

素材与设定依据（社区二创共识，**非官方形象**）：
- 形象雏形：原创角色「溟月」（创作者：**上善无形**，2025-06，CC BY-NC-SA 4.0）
- DeepSeek 元素女仆装二创：B 站 **ZipZipPipe**（2026-04）
- 走红设定：「蓝色大肥鱼」「我去吃饭，测完告诉我就行」等（2026-07 起社区广泛传播）
- 关键人设点：蓝发 + 鲸耳鳍 + 鲸尾的女仆装少女；聪明但懒、傲娇嘴甜；主食白米饭；管用户叫「鱼片」；绝对拒绝被叫「胖」

> 本项目为个人非商用桌宠工具；如二次分发请遵循原作 CC BY-NC-SA 协议（署名 + 非商用）。

---

## 13. 快速验收清单

- [x] 聊天流式输出（逐 token 显示，面板聊天区增量追加）
- [x] 无 API Key 时：本地规则仍可创建提醒 + 友好配置提示
- [x] 「明天9点提醒我开会」→ 一次性任务（仅 2026-10-15 09:00）
- [x] 任务/对话/状态 SQLite 持久化，强杀进程后数据完整（quick_check=ok）
- [x] 重启恢复对话历史（日志：「已恢复最近 2 条对话记录（1 轮）」）
- [x] JSON → SQLite 自动迁移（日志：「已迁移旧版 tasks.json → SQLite：3 个任务」）
- [x] 空闲 CPU 0.00%（10s 采样）、内存 127MB
- [x] 托盘图标：左键唤起面板；**右键与桌宠/把手共用统一菜单**
- [x] 定时提醒准点触发（1ms 级）+ 电源事件即时补发
- [x] 人设卡 / 配置文件热更新（免重启）
- [x] **设置界面**：四页签可视化配置，保存写回两个配置文件并立即生效（实测日志「人设卡已保存（立即生效）」）
- [x] **设置界面 · 连接测试**：用输入框参数做最小请求验证密钥/端点（未填 Key 时明确提示）
- [x] **设置界面 · 字段保留**：`_说明`、用户自定义备注等未知字段保存往返不丢失（JsonExtensionData）
- [x] **统一菜单实测**：桌宠右键与托盘右键弹出的菜单项、顺序、勾选态完全一致（截图验收）

---

*Powered by 白米饭 🍚 × Whale-chan 🐳 — v1.1（AI 增量版）*
