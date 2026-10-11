# WheatStook v1.4.4 — follow 修好 + 剧情对白不再弹面板

> 发布包已带编译好的 `WheatStook.dll`，解压即可用，**无需自己编译**。
>
> 本版发布 **两个压缩包**：
> - **`WheatStook-v1.4.4.zip`** —— 模组本体，解压后整个 `WheatStook/` 文件夹放进 `Stardew Valley/Mods/`。
> - **`WheatStook-v1.4.4-mcp_bridge.zip`** —— 联机 AI 用的 **Python 桥**（`server.py` + `client.py` + `launcher.bat` + `gen_token.bat` + `scripts/mods_keybinds.json`），本地解压即可，**不是装进 Mods**，跑在电脑上。

---

## v1.4.4 修复：`follow` 跟不动玩家（两个 bug 叠在一起）

以前让 farmhand 跟随房主，它会回 `{"ok": true, "status": "tracking"}`，然后**一步都不走**。根因有两层：

- **不解析前缀**：桥的工具说明写着支持 `player:名字` / `npc:名字` / `x,y`，但 mod 只拿整串去比名字 —— `player:Es_099` 这种写法必然「找不到目标」。
- **从来不给玩家寻路**：真正寻路的那段只查 `loc.characters`（NPC 列表），玩家根本不在里面，所以对玩家目标永远是空转。

现在：

- 按前缀解析，三种写法都认（`player:` / `npc:` / `x,y`）。
- 对玩家**真的寻路**，并在 `Tick()` 里加了跟随循环 —— 玩家一直在动，一条路走完就重新规划。这是"跟随"和"走一段过去"的区别。
- 目标不在同一地点时**明确报错**：「我在 Mine，你在 Farm：先 warp 再 follow」，不再假装在跟。
- `follow` 传空 = 停止跟随（以前 `/stop` 清了路，下一帧跟随又把它接回来，停不下来）。
- `move_to` 会自动取消跟随，两者不抢路。
- `GET /state` 新增 `following` 字段，能直接看出它在跟谁。

## v1.4.4 改进：剧情对白不再弹进聊天面板

- 新增配置 **`panelShowStoryDialogue`（默认 `false`）**：节日/剧情里那些**没有选项**的 NPC 对白，不再被塞进聊天面板 —— 它本来就不需要做决定，而且会把面板弹开，看剧情时碍事。
- 文本**仍照旧写进 SMAPI 日志**，没有丢。
- **要选择的对白完全不受影响**：`【对话】选项`、`【升级】职业二选一`、`选了「…」` 照旧显示。它只在你要做决定时出现。
- GMCM 里多了一个开关「剧情对白也弹进面板」，想开回来随时开。

---

## 跳过的两个版本

`v1.4.2`（联机夜晚卡死修复 + MCP 桥默认端口 8000 → 14159）和 `v1.4.3`（节日死锁：自动关闭无选项对话框 + 节日中禁止强制传送）的说明在各自的 GitHub Release 页面。

---

# WheatStook v1.4.1 — 日结自动化 + AI 临时决定 + Operit 转发修复

> 发布包已带编译好的 `WheatStook.dll`，解压即可用，**无需自己编译**。
>
> 本版发布 **两个压缩包**：
> - **`WheatStook-v1.4.1.zip`** —— 模组本体，解压后整个 `WheatStook/` 文件夹放进 `Stardew Valley/Mods/`。
> - **`WheatStook-v1.4.1-mcp_bridge.zip`** —— 联机 AI 用的 **Python 桥**（`server.py` + `client.py` + `launcher.bat` + `gen_token.bat` + `scripts/mods_keybinds.json`），本地解压即可，**不是装进 Mods**，跑在电脑上。

---

## v1.4.1 修复：不再掐断 Operit 的回复流

- `HttpClient.Timeout` 原来写死 **60 秒**，而且它连读取 SSE body 一起计时 —— AI 还在写就被 mod 自己切断，回复直接丢。
- 改为：**连接超时 15 秒**（服务器不通快速失败），**回复流不限时**；新增 `operitReplyTimeoutSeconds`（`0` = 不限，默认）。
- 新增 `operitHistoryFallback`（默认开）：流没给出 `assistant_done` 时，轮询 `GET /api/web/chats/{id}/messages` 把新回复捞回来，并去掉 `<think>` 块。
- `OperitChatClient` 之前**静默吞掉** `event:error`，读回复失败时完全无声；现在记警告。

## v1.4.0 对话选项交给 AI 临时决定

- `dialogueChoiceMode = "ai"`（默认）：带选项的对话（NPC 提问、商店问答）把选项报给 AI，等 `professionTimeoutSeconds` 秒。
- 超时默认第一项，**保证对话不卡死**；也支持 `first` / `random` / `off`（留给真人点）。
- 新增 `GET/POST /dialogue` 端点 + `wheatstook_dialogue` 工具。

## v1.3.0 5/10 级职业二选一交给 AI 临时决定

- `professionMode = "ai"`（默认）：把两个选项连同技能/等级报给 AI，等 `professionTimeoutSeconds` 秒。
- **超时兜底随机选一个**，保证夜晚绝不卡死；也支持 `left` / `right` / `random` / `off`。
- 新增 `GET/POST /profession` 端点 + `wheatstook_profession` 工具 + `AiNotify` 推送通道。
- 日志与面板都会写明**选了哪个、是谁决定的**。

## v1.2.0 日结界面自动点

farmhand 睡下后，技能升级框 / 职业二选一 / 卖货结算界面会卡住夜晚，而**房主无法替另一个玩家点**。

- `LevelUpMenu`（确定 / 职业选择）、`ShippingMenu`（多页结算）、`ConfirmationDialog`、`DialogueBox`。
- 只在**日结窗口内**动作（`isInBed`、`/sleep` 后 30 秒，或界面出现时自动续期），20 tick 冷却防连点 —— 平时开箱子、逛商店的菜单绝不会被误点。
- 新增 `autoConfirmDayEnd` 配置（默认开）+ `POST /autoconfirm` + `wheatstook_autoconfirm` 工具（可随时开关，不用重启）。

---

## 自 v1.1.0 以来的其他修复

（当时 manifest 版本号仍是 1.1.0，未单独发版）

- **过夜/睡觉**改走真正的床交互（修「晚上都睡了没渡过夜晚」）
- `/chat` 改走原版玩家聊天广播（修「消息只在 CMD 显示」）
- 游戏内聊天面板**支持中文输入（IME）**
- **`ForceWarp`**：手工换 location —— 实测 `Game1.warpFarmer` 对 farmhand 完全无效
- `/warp` 支持 `location=home` 别名（小屋真名是 UUID）
- `inBed` 时拒绝 `/move`；`/awake` 先下床再清标记
- NPC 对话、`area` 批量收割/浇水、地面掉落物识别
- 记忆（`/memory`）、送礼反应（`/react`）、事件包装、N 网尾号→链接
- `/status` 版本号改读 manifest（不再硬编码）

---

## 新增配置项（`config.json`）

```jsonc
"autoConfirmDayEnd": true,          // 日结界面自动点
"professionMode": "ai",             // 职业二选一: ai / left / right / random / off
"professionTimeoutSeconds": 45,     // 等 AI 回话的秒数
"dialogueChoiceMode": "ai",         // 对话选项: ai / first / random / off
"operitReplyTimeoutSeconds": 0,     // 等 Operit 回复: 0 = 不限
"operitHistoryFallback": true,      // 流断了去聊天记录捞回复
```

完整说明见 `config.example.json`（每个字段都带 `_说明_xxx` 中文注释）。

## 安装

1. 装 **SMAPI 4.x**。
2. 解压 `WheatStook-v1.4.1.zip`，把 `WheatStook/` 整个文件夹放进 `Stardew Valley/Mods/`。
3. 启动游戏，模组会在 `Mods/WheatStook/config.json` 生成默认配置。
4. 需要联机 AI 时，再解压 `-mcp_bridge.zip` 并跑 `mcp_bridge/launcher.bat`。

## 许可

v1.4.1 发布时为 AGPL-3.0（含 §13 网络条款）；**此后本项目改用 MIT**。本项目为**干净重写**，不含上游代码；详见 `NOTICE`。
