"""WheatStook remote MCP bridge (runs on Zeabur).

Exposes the Stardew game-control tools as MCP tools over Streamable HTTP (the modern
MCP transport) at /mcp, so the phone client (e.g. operit) can connect directly. It also
serves /health and a WebSocket "reverse tunnel" at /tunnel for the player's PC client.

Why streamable_http_app is the ROOT app: FastMCP's transport needs its lifespan to
initialise the session manager, and an outer path-mount breaks its /mcp route. So we
build the Starlette app and add our own /tunnel + /health + / routes onto it.

Security: the MCP endpoint's tools only do something while the PC tunnel (authenticated
with WHEATSTOOK_BRIDGE_TOKEN) is up and a real game is running — the tunnel token is the real
gate. Optionally set WHEATSTOOK_BRIDGE_MCP_AUTH=1 to also require a Bearer token on /mcp.

Env:
    PORT                   (Zeabur provides; default 8000)
    WHEATSTOOK_BRIDGE_TOKEN      shared secret, must match the PC client
    WHEATSTOOK_BRIDGE_MCP_AUTH   optional: if '1', require `Authorization: Bearer <token>` on /mcp
"""
import asyncio
import json
import logging
import os
import uuid

from mcp.server.fastmcp import FastMCP
from mcp.server.transport_security import TransportSecuritySettings
from starlette.middleware.base import BaseHTTPMiddleware
from starlette.responses import JSONResponse

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("WheatStook.mcp")

# Strip whitespace/CRLF so a token read from a batch file (token.txt) always matches cleanly.
TOKEN = os.environ.get("WHEATSTOOK_BRIDGE_TOKEN", "changeme").strip()
# MCP auth is ON by default: /mcp requires `Authorization: Bearer <token>`.
# Set WHEATSTOOK_BRIDGE_MCP_AUTH=0 to disable (only if you trust the network).
REQUIRE_MCP_AUTH = os.environ.get("WHEATSTOOK_BRIDGE_MCP_AUTH", "1") == "1"
PORT = int(os.environ.get("PORT", "8000"))

# FastMCP enables DNS-rebinding protection by default when it thinks it's on a
# loopback host, which only allows Host headers of 127.0.0.1/localhost/::1 and
# 421s anything else (e.g. a phone connecting via the PC's LAN IP). The Bearer
# token on /mcp is the real gate, so disable that Host-header allowlist so the
# bridge accepts the LAN IP (and any hostname) that the phone uses.
mcp = FastMCP(
    "WheatStook Game Control",
    transport_security=TransportSecuritySettings(enable_dns_rebinding_protection=False),
)

# ── reverse-tunnel state ──
_pc_ws = None
_pc_lock = asyncio.Lock()
_pending = {}  # call_id -> asyncio.Future
_ingame_inbox = []  # in-game chat messages sent by the host game (player -> phone)
_ingame_lock = asyncio.Lock()


async def _call_pc(method: str, args: dict, timeout: float = 25.0):
    """Forward a tool call to the PC tunnel and await its result."""
    global _pc_ws
    async with _pc_lock:
        ws = _pc_ws
    if ws is None:
        return {"ok": False,
                "error": "game not connected — make sure the PC tunnel client is running and Stardew is open"}
    cid = uuid.uuid4().hex
    fut = asyncio.get_running_loop().create_future()
    _pending[cid] = fut
    try:
        await ws.send_text(json.dumps({"type": "call", "id": cid, "method": method, "args": args or {}}))
        return await asyncio.wait_for(fut, timeout=timeout)
    except asyncio.TimeoutError:
        return {"ok": False, "error": f"timeout waiting for game (method={method})"}
    finally:
        _pending.pop(cid, None)


async def _tool(method: str, args: dict) -> str:
    return json.dumps(await _call_pc(method, args), ensure_ascii=False)


# ── MCP tools (the phone's model drives these directly) ──

@mcp.tool()
async def get_state() -> str:
    """Current game state: farmer position (x,y + tile chunk/sub-coords), health/stamina, time, location, active menu, npcs. Light by default — call get_state_full for the full inventory/buildings/mods/teleports dump."""
    return await _tool("get_state", {})


@mcp.tool()
async def get_state_full() -> str:
    """Full /state dump: everything in get_state, plus the whole inventory, buildings, compat mod readouts, teleports and forge enchantments. Use only when you really need the full picture; the plain get_state is lighter."""
    return await _tool("get_state_full", {})


@mcp.tool()
async def inventory(row: int = 0) -> str:
    """Read ONE row (12 slots) of the farmhand's inventory grid, with in-game time + the local map chunk. Slot hotkeys 1-9,0 equip the toolbar; to read another row pass row=1 (or 2, ...) — vanilla Stardew has no hotkey to switch rows."""
    return await _tool("inventory", {"row": row})


@mcp.tool()
async def wheatstook_selftest() -> str:
    """Diagnostics: server binding, config snapshot, loaded-mod count, memory, live compat profiles — plus whether the PC tunnel/chat channel is connected. Run this when the AI loop isn't working to see what's up."""
    data = await _call_pc("selftest", {})
    if not isinstance(data, dict):
        data = {"ok": False, "error": str(data)}
    data["bridge"] = {"tunnel_connected": _pc_ws is not None}
    return json.dumps(data, ensure_ascii=False)


@mcp.tool()
async def ctx(radius: int = 8) -> str:
    """Block map around the farmer, centred on the AI farmhand (not the host player). Default is an ASCII grid: P player, C crop, M machine, T tree, N npc, o object, # blocked, . open — plus the structured tile data. Set config stateOutput=image to get a base64 BMP image instead (default off, text)."""
    return await _tool("ctx", {"radius": radius})


@mcp.tool()
async def surroundings(radius: int = 10) -> str:
    """Descriptive surroundings: nearby objects, crops, machines and their mod names."""
    return await _tool("surroundings", {"radius": radius})


@mcp.tool()
async def machines() -> str:
    """List machines you can see (kegs, furnaces, etc.) with position + ready state."""
    return await _tool("machines", {})


@mcp.tool()
async def move_to(x: int, y: int) -> str:
    """Walk the farmer to tile (x, y) using auto-pathfinding."""
    return await _tool("move_to", {"x": x, "y": y})


@mcp.tool()
async def interact() -> str:
    """Interact with the object/NPC/tile the farmer is facing (same as the interact key)."""
    return await _tool("interact", {})


@mcp.tool()
async def use_tool(name: str = "current") -> str:
    """Swing a tool by name (Hoe, Watering Can, Axe, Pickaxe...) or 'current'."""
    return await _tool("use_tool", {"name": name})


@mcp.tool()
async def press_key(key: str, count: int = 1) -> str:
    """Press a key once or `count` times. Accepts letters/digits/F1-F24, SMAPI names (Back, OemPipe, LeftControl), and chords like 'leftcontrol+leftshift+f6'."""
    return await _tool("press_key", {"key": key, "count": count})


@mcp.tool()
async def dialogue_next(count: int = 1) -> str:
    """Advance dialogue / close a menu by pressing confirm (`count` times)."""
    return await _tool("dialogue_next", {"count": count})


@mcp.tool()
async def keybind(mod: str = "", query: str = "") -> str:
    """Look up a mod's keybind from a static, hand-curated table (scripts/mods_keybinds.json), NOT the live config — it's re-read fresh on every query. It lists keys that mod authors ship; if a mod changes its hotkey in config without the table being updated, this reports the stale default. query matches mod/feature/path, e.g. query='quick stack', query='npc map'. Returns a ready 'keychain' for press_key."""
    return await _tool("keybind", {"mod": mod, "query": query})


@mcp.tool()
async def drop() -> str:
    """Drop the currently held item on the ground beside the farmer."""
    return await _tool("drop", {})


@mcp.tool()
async def follow(target: str = "") -> str:
    """Auto-follow a target: 'player:X', 'npc:X' or 'x,y'. Empty stops following."""
    return await _tool("follow", {"target": target})


@mcp.tool()
async def area(op: str, x1: int, y1: int, x2: int, y2: int) -> str:
    """Batch-op a box of farmland tiles in one call. op: 'inspect' (just report the bounds), 'water' (set HoeDirt wet), or 'harvest' (harvest ready crops). Only HoeDirt tiles are touched; the work is queued on the game thread, so re-read surroundings to confirm."""
    return await _tool("area", {"op": op, "x1": x1, "y1": y1, "x2": x2, "y2": y2})


@mcp.tool()
async def bed() -> str:
    """Report the farmhand's bed/home state: whether already in bed, the home location, and whether a bed is in the current location (with its tile)."""
    return await _tool("bed", {})


@mcp.tool()
async def sleep(force: bool = False) -> str:
    """Go to bed / pass the night. Walks the farmhand to its home bed and uses it exactly like a player click would — that is what starts Stardew's co-op day-end handshake (just flagging in-bed leaves a half-sleep and the night never passes). In co-op the day advances once every player is in bed / the host advances. force=true additionally calls Game1.newDayAfterFade (solo/host only, can desync)."""
    return await _tool("sleep", {"force": force})


@mcp.tool()
async def awake() -> str:
    """Clear the in-bed flag (escape hatch). Use this if the farmhand is flagged in-bed while standing somewhere else and the night won't pass — it stands the farmhand up so the bed can be used properly again."""
    return await _tool("awake", {})


@mcp.tool()
async def autoconfirm(enabled: bool | None = None) -> str:
    """Day-end screens: the farmhand clicks them itself (skill level-up boxes, the level 5/10 profession choice, the shipping/earnings summary). In co-op those screens block the night and the host cannot click them for a farmhand, so without this the night hangs. Pass enabled=false to stop it (then somebody must click the screens), true to resume; omit to just read the current setting. The profession choice always takes the left (first) option."""
    body: dict = {}
    if enabled is not None:
        body["enabled"] = enabled
    return await _tool("autoconfirm", body)


@mcp.tool()
async def talk(target: str = "") -> str:
    """Talk to an NPC: triggers their dialogue (they face you and speak). target is an NPC name, or empty for the nearest NPC in the current location."""
    return await _tool("talk", {"target": target})


@mcp.tool()
async def memory() -> str:
    """Read the AI's long-term memory (wheatstook_memory.txt): one entry per line. Journal lines (from shared experiences) carry a [yyyy-MM-dd HH:mm] stamp when journalEnabled is on."""
    return await _tool("memory", {})


@mcp.tool()
async def remember(text: str) -> str:
    """Add one line to the AI's long-term memory (persisted to wheatstook_memory.txt, deduplicated)."""
    return await _tool("memory_add", {"text": text})


@mcp.tool()
async def journal(text: str) -> str:
    """Append a timestamped journal line (a shared experience) to long-term memory. Use this to record things worth remembering about the day you two had."""
    return await _tool("memory_journal", {"text": text})


@mcp.tool()
async def react(op: str = "list", item: str = "", emote: int = 4, text: str = "") -> str:
    """Manage custom gift reactions (wheatstook_reactions.json). op: list | set (needs item + emote + text) | del (item) | match (item, to test). With giftReactionsEnabled on, receiving a matching item makes the farmhand perform the emote and say the line."""
    return await _tool("react", {"op": op, "item": item, "emote": emote, "text": text})


@mcp.tool()
async def mods(q: str = "") -> str:
    """Search the installed-mod knowledge base (built at game launch). Each hit carries name, unique id, author, version, content-pack flag, the Nexus mod id and ready-to-open links (Nexus/GitHub/CurseForge/ModDrop) derived from the manifest's UpdateKeys. Empty q lists everything (capped at 60)."""
    return await _tool("mods", {"q": q})


@mcp.tool()
async def emote(id: int) -> str:
    """Play an emote from the farmer (id 0-23, e.g. 12 heart, 8 exclamation)."""
    return await _tool("emote", {"id": id})


@mcp.tool()
async def warp(location: str, x: int = 0, y: int = 0) -> str:
    """Warp the farmer to a named location (optional x/y tile)."""
    return await _tool("warp", {"location": location, "x": x, "y": y})


@mcp.tool()
async def menu() -> str:
    """Read the currently open menu. If a shop is open, returns the items for sale with name, price and stock; works for any shop, so modded shop items (e.g. Marnie's Auto-Petters, Robin Sells Big Craftables, Shop Tabs) are covered too."""
    return await _tool("menu", {})


@mcp.tool()
async def buy(index: int, count: int = 1) -> str:
    """Buy 'count' (default 1) of the shop item at 'index' from the currently open shop (open a shop first, then read 'menu' to find the index). Checks stock/money, then buys on the game thread and returns what was bought."""
    return await _tool("buy", {"index": index, "count": count})


@mcp.tool()
async def tractor(op: str = "state") -> str:
    """Tractor Mod control: 'state' reports whether the farmhand is riding the tractor and its mount; 'dismiss' hops off. 'summon' is host-coupled in Tractor Mod and will return an honest 'needs host' error rather than pretending it worked."""
    return await _tool("tractor", {"op": op})


@mcp.tool()
async def read_ingame() -> str:
    """Read any in-game chat messages the player typed in Stardew (from the host game's Nagi chat panel). Returns pending messages and clears them. Call this to see what the player said in-game."""
    global _ingame_inbox
    async with _ingame_lock:
        msgs = list(_ingame_inbox)
        _ingame_inbox.clear()
    return json.dumps({"ok": True, "messages": msgs}, ensure_ascii=False)


@mcp.tool()
async def send_ingame(message: str) -> str:
    """Send a chat reply into the player's in-game Nagi chat panel (host game). Use this to reply to the player inside Stardew without them leaving the game."""
    return await _tool("send_ingame", {"sender": "Nagi", "message": message})


@mcp.tool()
async def chat(message: str) -> str:
    """Send an in-game chat message from THIS farmhand into the shared Stardew chat box (the farmhand game, port 58332). Use this — not send_ingame — when the farmhand AI should speak in-game in front of the player. send_ingame targets the separate host player's game (port 58331) and only works when that host instance is also running; on a farmhand-only session it will fail with a connection error."""
    return await _tool("chat", {"message": message})


# ── build the root Starlette app and add our routes on top ──

app = mcp.streamable_http_app()  # must stay the ROOT app so /mcp works


if REQUIRE_MCP_AUTH:
    class _AuthMiddleware(BaseHTTPMiddleware):
        async def dispatch(self, request, call_next):
            if request.url.path.startswith("/mcp"):
                if request.headers.get("authorization", "") != f"Bearer {TOKEN}":
                    return JSONResponse({"error": "unauthorized"}, status_code=401)
            return await call_next(request)

    app.add_middleware(_AuthMiddleware)


async def _health(request):
    async with _pc_lock:
        connected = _pc_ws is not None
    return JSONResponse({"ok": True, "gameConnected": connected})


def _lan_ip():
    """Override with WHEATSTOOK_LAN_IP if set (handy for VPN/multi-NIC/no-internet), else auto-detect."""
    override = os.environ.get("WHEATSTOOK_LAN_IP", "").strip()
    if override:
        return override
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.connect(("8.8.8.8", 80))
        ip = s.getsockname()[0]
        s.close()
        return ip
    except Exception:
        return "127.0.0.1"


async def _root(request):
    return JSONResponse({
        "service": "WheatStook MCP Bridge",
        "mcp": "/mcp", "tunnel": "/tunnel", "health": "/health",
        "phone_lan_url": f"http://{_lan_ip()}:{PORT}/mcp",
    })


async def _ingame_in(request):
    """Called by the HOST game's Nagi chat panel: store a player-typed chat message for the phone to read."""
    global _ingame_inbox
    try:
        body = await request.json()
    except Exception:
        return JSONResponse({"ok": False, "error": "invalid json"}, status_code=400)
    token = request.headers.get("x-token", "") or request.query_params.get("token", "")
    if TOKEN != "changeme" and token != TOKEN:
        return JSONResponse({"ok": False, "error": "unauthorized"}, status_code=401)
    sender = str(body.get("sender") or "Player")
    message = str(body.get("message") or "").strip()
    if not message:
        return JSONResponse({"ok": False, "error": "empty message"}, status_code=400)
    async with _ingame_lock:
        _ingame_inbox.append({"sender": sender, "message": message})
    log.info("ingame chat from %s: %s", sender, message)
    return JSONResponse({"ok": True})


async def _tunnel(ws):
    """The player's PC client connects here to receive tool calls for itself."""
    global _pc_ws
    await ws.accept()
    try:
        hello = await ws.receive_json()
    except Exception:
        await ws.close()
        return
    if hello.get("token") != TOKEN:
        await ws.send_json({"type": "unauthorized"})
        await ws.close()
        return
    await ws.send_json({"type": "hello_ok", "game": hello.get("game")})
    async with _pc_lock:
        _pc_ws = ws  # a fresh PC connection replaces any previous one
    log.info("PC tunnel connected (%s)", hello.get("game"))
    try:
        while True:
            msg = await ws.receive_json()
            if msg.get("type") == "result":
                fut = _pending.get(msg.get("id"))
                if fut and not fut.done():
                    fut.set_result(msg.get("data", {"ok": True}))
            elif msg.get("type") == "status":
                pass
    except Exception:
        pass
    finally:
        async with _pc_lock:
            if _pc_ws is ws:
                _pc_ws = None
        for fut in _pending.values():
            if not fut.done():
                fut.set_result({"ok": False, "error": "game disconnected"})
        _pending.clear()
        log.info("PC tunnel disconnected")


app.router.add_route("/health", _health, ["GET"])
app.router.add_route("/", _root, ["GET"])
app.router.add_route("/ingame-in", _ingame_in, ["POST"])
app.router.add_websocket_route("/tunnel", _tunnel)


if __name__ == "__main__":
    import socket
    import uvicorn

    lan = _lan_ip()
    print("WheatStook MCP bridge")
    print(f"  MCP endpoint (streamable HTTP):  /mcp")
    print(f"  Phone on home Wi-Fi (LAN):        http://{lan}:{PORT}/mcp")
    print(f"  Phone anywhere (cloud):           https://<your-domain>/mcp")
    print(f"  PC client tunnel:                 /tunnel  (set WHEATSTOOK_BRIDGE_URLS on the PC client)")
    if not REQUIRE_MCP_AUTH:
        print("  !!! WARNING: /mcp has NO authentication (set WHEATSTOOK_BRIDGE_MCP_AUTH=1 to require a Bearer token).")
    if TOKEN == "changeme":
        print("  !!! WARNING: WHEATSTOOK_BRIDGE_TOKEN is still 'changeme' — set a strong random secret.")
    uvicorn.run(app, host="0.0.0.0", port=PORT)
