# WheatStook v1.4.1 — Day-end automation, AI-driven choices, Operit forward fix

> The release ships a **pre-built `WheatStook.dll`** — unzip and play, no build step.
>
> Two archives:
> - **`WheatStook-v1.4.1.zip`** — the mod. Drop the whole `WheatStook/` folder into `Stardew Valley/Mods/`.
> - **`WheatStook-v1.4.1-mcp_bridge.zip`** — the **Python bridge** for the co-op AI (`server.py` + `client.py` + `launcher.bat` + `gen_token.bat` + `scripts/mods_keybinds.json`). Unzip anywhere on the PC; it does **not** go into `Mods/`.

---

## v1.4.1 — stop cutting off Operit's reply stream

- `HttpClient.Timeout` was a hard-coded **60 s**, and it covers reading the SSE body too — the mod disconnected while the AI was still writing, losing the reply.
- Now: **15 s connect timeout** (fail fast when the server is down), **no limit on the reply stream**; new `operitReplyTimeoutSeconds` (`0` = unlimited, default).
- New `operitHistoryFallback` (on by default): if the stream ends without `assistant_done`, poll `GET /api/web/chats/{id}/messages` and recover the reply, stripping the `<think>` block.
- `OperitChatClient` used to swallow `event:error` silently; it now logs a warning.

## v1.4.0 — dialogue options decided by the AI

- `dialogueChoiceMode = "ai"` (default): a dialogue with options (an NPC question, a shop prompt) is published to the AI, which has `professionTimeoutSeconds` to answer.
- Falls back to the first option so a conversation never wedges; `first` / `random` / `off` also supported.
- New `GET/POST /dialogue` endpoint and `wheatstook_dialogue` tool.

## v1.3.0 — level 5/10 profession choice decided by the AI

- `professionMode = "ai"` (default): both options plus the skill/level are published, the AI answers, and the choice falls back to random so the night never hangs.
- `left` / `right` / `random` / `off` also supported.
- New `GET/POST /profession` endpoint, `wheatstook_profession` tool and `AiNotify` push channel.
- The log and the in-game panel state which option was taken and who decided it.

## v1.2.0 — day-end screens are clicked automatically

After a farmhand sleeps, the skill level-up boxes, the profession choice and the shipping summary block the night, and the host **cannot click them for another player**.

- Handles `LevelUpMenu`, `ShippingMenu`, `ConfirmationDialog` and `DialogueBox`.
- Only acts inside the day-end window (`isInBed`, 30 s after `/sleep`, or re-armed while a screen is up) with a 20-tick cooldown — ordinary menus are never touched.
- New `autoConfirmDayEnd` setting (on by default), `POST /autoconfirm` endpoint and `wheatstook_autoconfirm` tool, toggleable without a restart.

---

## Other fixes since v1.1.0

(shipped under the 1.1.0 manifest version, never released separately)

- **Passing the night** now uses the real bed interaction (fixes "everyone slept but the night never passed")
- `/chat` uses the vanilla player chat broadcast (fixes "the message only shows in the console")
- The in-game chat panel **accepts Chinese input (IME)**
- **`ForceWarp`**: change location by hand — `Game1.warpFarmer` was measured to be a no-op for this farmhand
- `/warp` accepts the `location=home` alias (a cabin's real name is a UUID)
- `/move` is refused while in bed; `/awake` steps off the bed before clearing the flag
- NPC dialogue, `area` batch harvest/water, dropped-item detection
- Memory (`/memory`), gift reactions (`/react`), event packaging, Nexus-id → link
- `/status` reads the version from the manifest instead of a hard-coded string

---

## New settings (`config.json`)

```jsonc
"autoConfirmDayEnd": true,          // click day-end screens automatically
"professionMode": "ai",             // ai / left / right / random / off
"professionTimeoutSeconds": 45,     // seconds to wait for the AI
"dialogueChoiceMode": "ai",         // ai / first / random / off
"operitReplyTimeoutSeconds": 0,     // 0 = never cut off the reply
"operitHistoryFallback": true,      // recover the reply from the chat history
```

Every field is documented in `config.example.json`.

## Install

1. Install **SMAPI 4.x**.
2. Unzip `WheatStook-v1.4.1.zip` and drop the `WheatStook/` folder into `Stardew Valley/Mods/`.
3. Launch the game; defaults are written to `Mods/WheatStook/config.json`.
4. For the co-op AI, unzip `-mcp_bridge.zip` and run `mcp_bridge/launcher.bat`.

## License

AGPL-3.0 (including the §13 network clause). This project is a **clean-room rewrite** with no upstream code; see `NOTICE`.
