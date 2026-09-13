# 麦垛 WheatStook

[English](README.md) | **中文**

> 编写：代码由 **AI 代理（垛口 / 哨兵）** 编写，由 **BeadyTooth57254** 执导并持有版权。本项目为**干净重写**，实现代码为**原创、未借上游代码**（详见文末开源许可）。
> 自动兼容（哪些模组被自动识别、哪些需手写适配器）：详见 **[COMPAT.md](COMPAT.md)**；游戏内 `wheatstook_compat` 打印精简版。

适用于 **联机(co-op)** 的星露谷物语 SMAPI 模组：它把你的游戏变成一台 HTTP/MCP 可控的实例，
让一个 AI 助手（比如手机上的 **operit**）能 **在游戏里跟你聊天**，并 **操控一个 farmhand 农夫**。

- **游戏内 AI 聊天** — 作为 host 玩家，按 `` ` `` 打开聊天面板和 AI 说话。AI 读到你的消息后，直接在聊天面板里回你。
- **AI 控制 farmhand** — AI 通过 MCP 桥控制 farmhand（移动、使用工具、交互、种田、传送等）。
- **联机感知** — 当 host 和 farmhand **都加载了这个模组**时，角色自动隔离：**host 只负责把聊天转发到桥**，**farmhand 只是被 AI 控制的角色**（由 `Game1.player.IsMainPlayer` 决定角色）。

## 工作原理

```
[手机: operit]
     │  streamable HTTP /mcp (Bearer token)
     ▼
[Python MCP 桥 :14159]  ──WS 隧道──▶  [client.py]  ──HTTP──▶  [星露谷游戏]
        server.py                          │                 host :58331
                                            └── 游戏内聊天 ──▶  farmhand :58332
```

- 模组（在 **每个游戏实例** 里）都会在 `http://localhost:<port>` 上起一个小的 HTTP 服务器：
  - **host 实例** → `HostPort`（默认 `58331`）
  - **farmhand 实例** → `FarmhandPort`（默认 `58332`）
- `mcp_bridge/` 里的 **Python MCP 桥** 把游戏暴露成 **streamable HTTP** 的 MCP 工具，端点在 `/mcp`，用 Bearer token 保护。
- **operit**（或任何 MCP 客户端）连上桥后，就能调用 `read_ingame` / `send_ingame`（聊天）和 farmhand 控制工具。

## 安装（玩家）

> 发布包里已带编译好的 `WheatStook.dll`——**不需要自己编译**。

1. 安装 [SMAPI](https://smapi.io/)。
2. 下载最新发布包并解压。
3. 把 `麦垛 WheatStook` 文件夹放进 `Stardew Valley/Mods/`。
4. 用 SMAPI 启动游戏。

支持 **Stardew Valley 1.6+ / SMAPI 4.0+**。同一个 `.dll` 在 Windows、macOS、Linux 上都能用。

## 编译（开发者）

```bash
dotnet build -c Release
```

用了 [`Pathoschild.Stardew.ModBuildConfig`](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md)，
会自动探测你的游戏目录，并把编译好的模组复制到 `Stardew Valley/Mods/<ModFolderName>/`。
如果自动探测失败，设置 `GamePath` 属性或 `GAME_PATH` 环境变量。

## 联机设置

host 和 farmhand 都加载 麦垛；角色由"是否为主玩家"自动决定。

`config.json`（两个实例共用同一份）：

```json
{
  "Mode": "operit",
  "HostPort": 58331,
  "FarmhandPort": 58332,
  "OperitBridgeUrl": "http://127.0.0.1:14159",
  "OperitBridgeToken": "<共享 token>"
}
```

### 启动 MCP 桥

```bash
cd mcp_bridge
gen_token.bat        # 先跑一次：写入 token.txt（共享密钥）
launcher.bat         # 启动桥(server.py 在 :14159) + 隧道 client(client.py)
```

或者手动：

```bash
set NAGI_BRIDGE_TOKEN=<token>
python server.py     # 桥, 监听 0.0.0.0:14159, 提供 /mcp + /ingame-in + /health
python client.py     # 隧道 client -> farmhand localhost:58332, host localhost:58331
```

### 连接 operit（手机）

- URL：`http://<电脑局域网IP>:14159/mcp`
- 鉴权：`Authorization: Bearer <token>` —— 和 `token.txt` 一致

手机必须用电脑的 **局域网 IP**（比如 `192.168.100.236`），不能用 `127.0.0.1` / `localhost`。

## 游戏内聊天

按 `` ` ``（键盘左上）打开聊天面板。当 `Mode: "operit"` 时，host 的消息会被转发到桥（`POST /ingame-in`）；
operit 用 `read_ingame` 读取，用 `send_ingame` 回复，桥再通过 `/chat/push` 推回 host 的面板。

## HTTP API（游戏）

每个游戏实例都提供一个小的 HTTP API（供桥的 client.py 使用）：

| 端点 | 方法 | 用途 |
|------|------|------|
| `/status` | GET | 角色、world-ready、联机状态 |
| `/chat/push` | POST | 把 AI 消息注入面板 |
| `/chat` | POST | 给所有玩家发聊天消息 |
| `/move`、`/tool`、`/interact` | POST | farmhand 控制 |
| `/warp`、`/use`、`/select`、`/face` | POST | farmhand 控制 |
| `/state`、`/surroundings`、`/ctx`、`/map` | GET | 游戏状态 |
| `/sleep`、`/wakeup`、`/stop`、`/pause`、`/resume` | POST | 会话控制 |

完整端点列表：见 [AGENTS.md](AGENTS.md)

## 聊天面板按键

| 按键 | 作用 |
|------|------|
| `` ` `` | 打开 / 关闭聊天面板 |
| `Enter` | 发送消息 |
| `Ctrl+V` | 粘贴 |

## Config 对照表

| 字段 | 含义 | 默认值 |
|------|------|--------|
| `Mode` | 聊天后端：`operit`（MCP 桥） | `cc` |
| `HostPort` | host 实例绑定的端口 | `58331` |
| `FarmhandPort` | farmhand 实例绑定的端口 | `58332` |
| `OperitBridgeUrl` | 用于游戏内聊天转发的桥地址 | `http://127.0.0.1:14159` |
| `OperitBridgeToken` | 转发聊天时发给桥的共享 token | 空 |
| `ApiProvider`/`ApiUrl`/`ApiKey`/`Model` | 旧的直连 LLM 聊天选项 | — |
| `ChannelServerUrl` | 旧的 channel-server 聊天选项 | `http://localhost:9000/chat` |

## 开源许可

基于 [GNU AGPL-3.0](LICENSE) 发布（强 copyleft）：商用允许，但**衍生作品与网络运行**都须**保持开源并给出源码**（§13 网络条款）。版权归 **BeadyTooth57254**。

> 致敬：本作是对 [anqinou-art/NagiBridge](https://github.com/anqinou-art/NagiBridge) 的**干净重写**，实现代码为**原创、未借上游代码**，不是从上游衍生的"适配版"。上游仓库无 LICENSE（默认 all rights reserved），本作不受其约束，也未使用其代码。
