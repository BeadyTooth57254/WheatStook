# 麦垛 WheatStook

An AI chat + farmhand-control mod for Stardew Valley. This version is **fully original clean-room code** and does not depend on the unlicensed source of `anqinou-art/NagiBridge`.
> Tribute: A **clean-room rewrite** inspired by [anqinou-art/NagiBridge](https://github.com/anqinou-art/NagiBridge). The implementation is original and borrows no upstream code. The upstream repo has no LICENSE (default all rights reserved); this work is not bound by it. `README.md` is the main English doc; see [README.zh-CN.md](README.zh-CN.md) for Chinese.
> Authorship: The code was written by an **AI agent (垛口 / a sentinel)** under the direction and ownership of **BeadyTooth57254**, who holds the copyright. This is an honest disclosure: the code here is AI-authored.
> License: Released under **GNU AGPL-3.0** (strong copyleft). Commercial use is permitted, but **derivative works and network running** must **stay open source and provide source** (§13 network clause). Copyright © BeadyTooth57254. See [LICENSE](LICENSE).

## Build / Deploy

```pwsh
dotnet build -c Release
```

The build auto-deploys the DLL to the game mods folder (`[AI聊天]麦垛 WheatStook` folder — close the game first or files will be locked and the copy will report a harmless failure; copy `bin\Release\net6.0\WheatStook.dll` manually in that case), and also produces a zip.

## Config

Copy `config.example.json` into `config.json` in-game. Every field has a `_说明_*` Chinese comment next to it (the mod ignores `_`-prefixed fields; they are documentation only). You can also use `wheatstook_help` or the optional GMCM menu.

**Fields** (defaults): `Mode`(operit), `HostPort`(58331), `FarmhandPort`(58332), `OperitBridgeUrl`(`http://127.0.0.1:14159`), `OperitBridgeToken`, `forwardToOperitChat`(false), `operitWebUrl`/`operitWebChatId`/`operitWebToken`, `forwardReadOperitReply`(false), `operitForwardFormat`(`【星露谷·{sender}】{message}`), `chunkSize`(5), `readWindow`(tool), `stateOutput`(text), `enableModKnowledge`(true), `sourceReadDepth`(intro), `modWhitelist`/`modBlacklist`, `cacheModUsage`(true), `includeMemoryInForward`(false), `reactionEnabled`(false), `enableAutoCompat`(false), `compatOverrides`(null), `keybindChatPanel`(OemTilde), `keybindBridgeToggle`(F8), `keybindHelp`(F1).

> Real addresses (Operit web URL, tokens) are private and change over time — only placeholders, **never commit real ones**.

## Auto-compat (off by default; activates when detected)

Many mods **change the game world** (more ring slots, bigger backpack, custom crops/bushes, new regions, profession loops, automation, etc.). So WheatStook ships an **auto-compat layer**:

- **Off by default** (`enableAutoCompat: false`); you decide.
- When on, it detects mods at startup by **UniqueId** (not folder name), **auto-activates the profile for whichever one is installed**, and writes the **active list into `/state`'s `compat` field**, so the AI knows this farm isn't vanilla.
- To force a specific one on/off: `compatOverrides` uses the UniqueId with `true`/`false` (takes priority over auto-detection). E.g. `{"bcmpinc.WearMoreRings": true, "spacechase0.BiggerBackpack": false}`.
- Brand-new content mods (custom crops / new professions / new machines) need an adapter per mod; the detect + toggle framework is in place, and adapters are added per mod you name.

For the full picture — which mods are auto-covered, which need a hand-written adapter, plus the current 41 profiles grouped by handling — see **[COMPAT.md](COMPAT.md)**. In-game, `wheatstook_compat` prints a concise version.

### Data-driven adapters: `CompatRules.json` (no code needed; anyone can fill it)

To teach the AI about "something that doesn't exist in vanilla," edit `CompatRules.json` in the mod folder (**JSON with comments; SMAPI reads comments**). Each rule tags **some thing** of **some mod**:

```json
{
  "Mod": "TntDove.PBB",         // that mod's UniqueID
  "Label": "可摘的浆果灌木",      // what the AI sees
  "MatchIdContains": "berry",   // object ID contains "berry"
  "MatchName": "berry",         // display name contains "berry"
  "Category": "berryBush",      // semantic tag (AI understands)
  "Harvestable": true,          // marked harvestable
  "Collectible": true           // marked a pick-up forage
}
```

- Applies only if that mod is installed AND its compat profile is **active** (`enableAutoCompat` on, or forced on via `compatOverrides`).
- Matching: `MatchIdContains` against `QualifiedItemId`, `MatchName` against display name; empty = don't check; both filled = must satisfy both.
- Real ids/names: use `wheatstook_selftest` or look at `/state` and `/surroundings` to see what an object actually reads as, then fill it accurately.
- A sample `CompatRules.json` ships with the repo (a Pokémon berry bush + a custom cask rule); copy the pattern.

### Built-in mod recognition (source-aligned; e.g. Tractor Mod)

For popular mods WheatStook has **built-in recognition** (aligned to their real implementation; you don't have to fill in a rule):

**Map rendering (`/ctx`)**: centers on the **AI-controlled farmhand** (not the host player — they're on another instance). Default is a **block-by-block ASCII map**: `P` player, `C` crop, `M` machine, `T` tree, `N` NPC/tractor, `o` object, `#` obstacle, `.` empty, plus **structured grid data** for the same region (each cell's `x/y`, walkable, contents). Range is set by the `radius` parameter (default 8, i.e. 17×17). For **image rendering** just set `stateOutput` to `image` for a base64 BMP (default `text`).

- **Tractor Mod**: `/state` reports `ridingTractor` (whether you're on the tractor) and `mountName`; `/surroundings` recognizes the tractor as a **tractor** (`"tractor": true`) instead of an ordinary NPC. New `/tractor` endpoint: `{op: "state"}` for status, `{op: "dismiss"}` to hop off.
  > Honest note: Tractor Mod's **summon** is host-coupled on the farmhand side (it verifies the message sender is itself; I can't substitute), so `/tractor` explicitly reports "needs host to summon" rather than pretending it works.
- **Better Crystalarium** (CP expansion of gem-duplicator range): `/machines` now **recognizes** crystalariums (`"isCrystalarium": true`), and reports the gem being copied (`heldObject`/`heldObjectId`), whether it's done (`readyForHarvest`), and remaining minutes (`minutesUntilReady`). Because it reads the **actual gem in the machine**, any kind added by CP shows up too.
  > General rule: **CP mods change game data, so the compat key is "read real runtime data, don't hardcode a vanilla list"** — object/crop name resolution earlier, and this machine read, follow that.
- **MoreGreenhouses** (CP adds multiple greenhouses): `/state`'s `location.isGreenhouse` marks whether the current location is a greenhouse (grows all year — the AI knows to plant here); `/state.buildings` lists the farm's buildings (including CP-added greenhouse buildings, `isGreenhouse: true`). Still reads actual state/building data, not a hardcoded list.
- **BuildMoreCellars** (CP more cellars): `/state`'s `location.isCellar` marks whether this is a cellar (where oak casks age drinks/cheese); `/state.buildings` marks CP-added cellar doors/buildings with `isCellar: true`; casks in the cellar are recognized by `/machines` and report what's aging (`heldObject`) and whether it's done.
- **Super Massive Greenhouse** (CP replaces `Maps/Greenhouse`): because it replaces the **same-position map**, the location name stays `Greenhouse`, so `isGreenhouse` hits naturally; `mapWidth`/`mapHeight` reflect the oversized map.
- **Shop reading (generic; covers selling/trading mods)**: `/menu` now reads an **open shop** — what's for sale, unit price, stock (`shop.items`, each with `name`/`price`/`stock`). Because it reads the open `ShopMenu`, **Marnie's Auto-Petters** (CP lets Marnie sell auto-petters), **Robin Sells Big Craftables** (CP lets Robin sell big craftables), and **Shop Tabs** (C# adds tabs; underlying item data unchanged) are all covered automatically — no per-mod list needed.
  > **Buy `POST /buy {index, count}`**: buys `count` of item `index` (`/menu`'s `shop.items` index) from the **currently open shop**. It does read-only validation first (in stock, enough money, normal item), then **queues into the game tick** to deduct money + add to inventory, returning `queued: true`; the AI later verifies delivery via `/state` (money / backpack). **Building-type items** (e.g. Robin building a house) don't go through this — buy manually in the shop UI.
  > The three C# hotkey mods' toggle keys (read from their config defaults): **Joja Express press `J`** to open the online catalog, **Auto Break Geode hold `F`** to auto-break geodes. If you rebound them in their config, tell me.
- **Enchantment reading (generic; covers enchantment mods)**: `/state` now reports the **`enchantments`** on the currently-held tool (all enchantments on one tool). Because it reads `tool.enchantments`, **Pick Forge Enchantment** (`Dragoon23.ForgeEnchantment`; directed enchants — forge auto-selects your configured one) and **Many Enchantments** (`Stari.ManyEnchantments`; conflict fixing — lets one tool stack multiple enchants) are both reflected — the AI can see "this pickaxe is Swift" or "this weapon has several enchants."
  > `/state` also reports **`forgeEnchantments`** — reads Pick Forge Enchantment's config.json, listing **each tool → what the forge will auto-enchant** (e.g. `{tool:"WaterCan", enchant:"Reaching"}`), so the AI can predict what forging a given tool will do.

## Console commands

- `wheatstook_operit <text>`: send text straight to Operit's native chat and read back (fastest verification channel).
- `wheatstook_mods [keyword]`: list installed mods (index built at startup).
- `wheatstook_mem add|list|del|clear`: manage long-term memory (stored in `wheatstook_memory.txt` in the mod folder).
- `wheatstook_selftest`: one-command check — config / Operit enabled / forward toggles / mod-knowledge count / memory count / server binding, to quickly find problems.
- `wheatstook_help`: show commands and hotkeys.

## Hotkeys (in-game)

- Backtick `` ` ``: chat panel. Open to type (letters/numbers/space/enter/backspace); enter submits → forwards to Operit, reply shows in panel.
- **F8**: toggle Operit forwarding (on by default).
- **F1**: pop the panel and show help.

## Test checklist

Preparation: close the game first, then build + deploy; open a session (or a multiplayer farmhand).

1. **Install/deploy**: enter the game; console should have no errors, and the log should show `WheatStook clean-room build is ready.` and `Mod knowledge base built: N mods indexed.`.
2. **Operit channel**: console `wheatstook_operit 你好` → should return Operit's full reply (in-game log / panel).
3. **Chat panel**: press backtick to open the panel, type a line + enter → panel shows "我: …", then an "Operit: …" reply appears.
4. **Forward toggle**: press F8 to turn off, send again → panel shows only "我:…", no forward; press F8 again to resume.
5. **Mod library**: `wheatstook_mods` (empty = all), `wheatstook_mods <keyword>`.
6. **Memory**: `wheatstook_mem add 我喜欢吃土豆` → `list` shows it; if `includeMemoryInForward` is on, the next forward carries it as `[回忆]`.
7. **Help**: F1 or `wheatstook_help`.
8. **Multiplayer farmhand control** (needs a multiplayer session + MCP bridge running):
   - `GET http://localhost:58332/state` → should return `ok:true, worldReady:true` + player/location/time + `chunk`.
   - `GET http://localhost:58332/surroundings` → chunked tiles.
   - `POST http://localhost:58332/move` (body `{"x":..,"y":..}`) → farmhand should walk there.
   - `POST http://localhost:58332/interact` / `/tool` / `/chat` etc. as needed.

## Known limits

- Chat panel input is **ASCII** (English/numbers/spaces); Chinese IME and multi-channel (MCP) forwarding are pending.
- GMCM menu is **best-effort**: if GenericModConfigMenu is installed, you can change config in-game; if the version interface doesn't match it silently skips and uses the manual `config.json`.
