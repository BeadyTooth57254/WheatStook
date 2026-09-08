"""WheatStook PC tunnel client (run on the same machine as the game).

Connects OUT to the Zeabur MCP bridge over WebSocket, authenticates with the shared
token, and answers tool calls by mapping them to the game's WheatStook HTTP API
(http://127.0.0.1:<port>). Reconnects automatically if the link drops.

Env:
    WHEATSTOOK_BRIDGE_URL       e.g. wss://<your-zeabur-url>/tunnel  (default ws://localhost:8000/tunnel)
    WHEATSTOOK_BRIDGE_TOKEN     shared secret, must match the server
    WHEATSTOOK_GAME_URL         farmhand game HTTP API (default http://localhost:58332)
    WHEATSTOOK_HOST_URL         host game HTTP API for in-game chat pushes (default http://localhost:58331)
    WHEATSTOOK_MODS_JSON        path to mods_keybinds.json (default ../scripts/mods_keybinds.json)
"""
import asyncio
import json
import logging
import os
import sys
import urllib.error
import urllib.parse
import urllib.request

import websockets

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("WheatStook.tunnel")

_DIR = os.path.dirname(os.path.abspath(__file__))
# A list of bridge endpoints to stay attached to at once (comma-separated).
# LAN default: run server.py on this same PC, phone reaches it over Wi-Fi.
# Add a cloud URL for away-from-home control, e.g. "ws://127.0.0.1:8000/tunnel,wss://<your-domain>/tunnel".
BRIDGE_URLS = [u.strip() for u in os.environ.get(
    "WHEATSTOOK_BRIDGE_URLS",
    os.environ.get("WHEATSTOOK_BRIDGE_URL", "ws://127.0.0.1:8000/tunnel"),
).split(",") if u.strip()]
TOKEN = os.environ.get("WHEATSTOOK_BRIDGE_TOKEN", "changeme").strip()
GAME_URL = os.environ.get("WHEATSTOOK_GAME_URL", "http://localhost:58332")  # farmhand (AI-controlled) game
HOST_URL = os.environ.get("WHEATSTOOK_HOST_URL", "http://localhost:58331")  # host (player) game, for in-game chat pushes
MODS_JSON = os.environ.get(
    "WHEATSTOOK_MODS_JSON",
    os.path.normpath(os.path.join(_DIR, "..", "scripts", "mods_keybinds.json")),
)


# ── game HTTP (stdlib, blocking; call via asyncio.to_thread) ──
def http(method: str, path: str, args: dict = None, base: str = None):
    args = args or {}
    url = (base or GAME_URL) + path
    if method == "GET" and args:
        url += "?" + urllib.parse.urlencode(args)
    data = None
    headers = {}
    if method == "POST":
        data = json.dumps(args).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.URLError as e:
        reason = getattr(e, "reason", None)
        if isinstance(reason, ConnectionRefusedError):
            return {"ok": False, "error": "game HTTP refused: the farmhand/host server is NOT listening on that port (Stardew closed, or that server never started) — retry only after the game is up"}
        if isinstance(reason, TimeoutError):
            return {"ok": False, "error": "game HTTP timed out: server up but not answering — retry once, else the game is frozen"}
        return {"ok": False, "error": f"game HTTP unreachable: {e}"}
    except ConnectionRefusedError as e:
        return {"ok": False, "error": f"game HTTP refused (port not listening): {e}"}
    except ConnectionResetError as e:
        return {"ok": False, "error": f"game HTTP reset: server dropped the connection: {e}"}
    except Exception as e:
        return {"ok": False, "error": str(e)}


# ── keybind lookup (local file, mirrors tool_agent.py) ──
def _norm(s):
    return s.replace(" ", "").replace("-", "").replace("_", "").lower()


def query_keybinds(mod="", query=""):
    try:
        with open(MODS_JSON, encoding="utf-8") as fh:
            data = json.load(fh)
    except Exception as e:
        return {"ok": True, "note": "mods_keybinds.json not available", "error": str(e), "data": []}
    out = []
    for e in data:
        if mod and _norm(mod) not in _norm(e["mod"]):
            continue
        hay = _norm(f"{e['mod']} {e['feature']} {e['path']}")
        if query and _norm(query) not in hay:
            continue
        keys = e.get("keys", []) or []
        out.append({
            "mod": e["mod"], "feature": e["feature"], "path": e["path"],
            "keys": keys, "keychain": "+".join(keys).lower(),
        })
    return {"ok": True, "count": len(out), "data": out}


# ── method -> HTTP route table ──
ROUTES = {
    "get_state": ("GET", "/state", None),
    "get_state_full": ("GET", "/state", {"full": "1"}),
    "inventory": ("GET", "/inventory", {"row": None}),
    "selftest": ("GET", "/selftest", None),
    "status": ("GET", "/status", None),
    "ctx": ("GET", "/ctx", {"radius": None}),
    "surroundings": ("GET", "/surroundings", {"radius": None}),
    "machines": ("GET", "/machines", None),
    "menu": ("GET", "/menu", None),
    "bed": ("GET", "/bed", None),
    "sleep": ("POST", "/sleep", {"force": None}),
    "talk": ("POST", "/talk", {"target": None}),
    "move_to": ("POST", "/move", {"x": None, "y": None}),
    "stop": ("POST", "/stop", None),
    "face": ("POST", "/face", {"direction": None}),
    "interact": ("POST", "/interact", None),
    "use_tool": ("POST", "/tool", {"name": None}),
    "press_key": ("POST", "/key", {"key": None, "count": None}),
    "dialogue_next": ("POST", "/key", {"key": "confirm", "count": None}),
    "drop": ("POST", "/drop", None),
    "follow": ("POST", "/follow", {"target": None}),
    "area": ("POST", "/area", {"op": None, "x1": None, "y1": None, "x2": None, "y2": None}),
    "emote": ("POST", "/emote", {"id": None}),
    "warp": ("POST", "/warp", {"location": None, "x": None, "y": None}),
    "chat": ("POST", "/chat", {"message": None}),
    "select": ("POST", "/select", {"name": None}),
    "buy": ("POST", "/buy", {"index": None, "count": None}),
    "tractor": ("POST", "/tractor", {"op": None}),
}

# Methods that target the HOST game (the player) rather than the farmhand — used for in-game chat replies.
HOST_ROUTES = {
    "send_ingame": ("POST", "/chat/push", {"sender": None, "message": None}),
}


def dispatch(method: str, args: dict):
    if method in HOST_ROUTES:
        m, p, params = HOST_ROUTES[method]
        body = {}
        if params:
            for k, default in params.items():
                v = args.get(k, default)
                if v is not None:
                    body[k] = v
        return http(m, p, body, base=HOST_URL)
    if method == "keybind":
        return query_keybinds(mod=args.get("mod", ""), query=args.get("query", ""))
    if method not in ROUTES:
        return {"ok": False, "error": f"unknown method: {method}"}
    m, p, params = ROUTES[method]
    body = {}
    if params:
        for k, default in params.items():
            v = args.get(k, default)
            if v is not None:
                body[k] = v
        # dialogue_next always sends confirm as the key
    return http(m, p, body)


async def session(uri):
    async with websockets.connect(uri, ping_interval=20, ping_timeout=20, open_timeout=15) as ws:
        await ws.send(json.dumps({"type": "hello", "token": TOKEN, "game": GAME_URL}))
        hello = json.loads(await ws.recv())
        if hello.get("type") == "unauthorized":
            log.error("Bridge rejected token — check WHEATSTOOK_BRIDGE_TOKEN")
            raise RuntimeError("unauthorized")
        log.info("Connected to bridge (%s)", hello.get("game"))
        async for raw in ws:
            try:
                msg = json.loads(raw)
            except Exception:
                continue
            if msg.get("type") == "call":
                cid = msg.get("id")
                method = msg.get("method")
                args = msg.get("args") or {}
                result = await asyncio.to_thread(dispatch, method, args)
                await ws.send(json.dumps({"type": "result", "id": cid, "data": result}))
            elif msg.get("type") == "ping":
                await ws.send(json.dumps({"type": "pong"}))


async def _keepalive(uri):
    """Stay attached to one bridge, reconnecting forever."""
    while True:
        try:
            await session(uri)
        except Exception as e:
            log.warning("bridge %s lost (%s) — retrying in 3s", uri, e)
            await asyncio.sleep(3)


async def main():
    assert TOKEN != "changeme", "Set WHEATSTOOK_BRIDGE_TOKEN to match the bridge."
    await asyncio.gather(*(_keepalive(u) for u in BRIDGE_URLS))


if __name__ == "__main__":
    print(f"WheatStook tunnel client | bridges={BRIDGE_URLS} game={GAME_URL}")
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nstopped")
