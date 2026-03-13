"""Behavior simulator — bots swipe, match, and message each other."""
import asyncio
import json
import os
import random
import subprocess
from pathlib import Path
from typing import Callable

import httpx

from . import config
from .seeder import state as seeder_state

DATA_DIR = Path(__file__).parent / "data"


class SimulatorState:
    """Shared state for the simulator, observable by the dashboard."""

    def __init__(self):
        self.reset()

    def reset(self):
        self.swipes = 0
        self.matches = 0
        self.messages_sent = 0
        self.active_bots = 0
        self.running = False
        self.cancelled = False
        self.speed = 1.0  # multiplier (0.1 = fast, 5.0 = slow)
        self.errors = 0
        self.startup_phase = ""  # shown in UI during service bring-up
        self.services_up = 0
        self.services_total = len(config.SERVICES)
        self.swiped_pairs: set[tuple[int, int]] = set()  # (user_id, target_id) already swiped this session
        self.dupes_skipped = 0  # count of duplicates skipped from local memory


state = SimulatorState()

_conversations: list[list[str]] = []


def _load_conversations():
    """Load pre-generated conversation templates."""
    global _conversations
    if _conversations:
        return
    conv_file = DATA_DIR / "conversations.json"
    if conv_file.exists():
        with open(conv_file) as f:
            _conversations = json.load(f)
    else:
        _conversations = [
            ["Hej! Hur mår du? 😊", "Hej! Jag mår bra, tack! Själv? 🙂", "Bra tack! Vad gör du idag?"],
            ["Tjena! Gillade din profil 😄", "Tack, samma här! Vad har du för intressen?", "Jag älskar att vara ute i naturen!"],
            ["Hej hej! Fin bild 📸", "Tack så mycket! Den är från min senaste resa", "Var var du? 🌍"],
            ["Hallå! Såg att du också gillar matlagning 🍳", "Ja! Jag lagar mat nästan varje dag", "Vad är din specialrätt?"],
            ["Hej! Kul att vi matchade! 🎉", "Verkligen! Berätta lite om dig själv", "Jag är en spontan person som gillar äventyr"],
            ["Tja! Vad jobbar du med? 💼", "Jag är utvecklare, du då?", "Jag jobbar inom sjukvården"],
            ["Hej! Vilken fin hund du har! 🐕", "Tack! Han heter Max", "Åh så gulligt! Jag älskar hundar"],
            ["God kväll! Hur var din dag? 🌙", "Den var bra tack! Lite trött men nöjd", "Förstår! Ska du göra nåt kul i helgen?"],
            ["Hej! Jag ser att du gillar vandring 🥾", "Ja! Jag vandrade Kungsleden förra sommaren", "Imponerande! Det ska jag också göra"],
            ["Hey! Har du sett nån bra film på sistone? 🎬", "Jag såg precis den nya Marvel-filmen", "Åh, den vill jag också se!"],
        ]


# ─── Health Check & Auto-Start ──────────────────────────────────────────────

_SERVICE_LABELS = {
    "Keycloak":           "Keycloak (auth, :8090)",
    "YARP Gateway":       "YARP Gateway (:8080)",
    "UserService":        "UserService (:8082)",
    "MatchmakingService": "MatchmakingService (:8083)",
    "PhotoService":       "PhotoService (:8085)",
    "MessagingService":   "MessagingService (:8086)",
    "SwipeService":       "SwipeService (:8087)",
}

_INFRA_SERVICES = {"Keycloak"}
_DOTNET_SERVICES = {"YARP Gateway", "UserService", "MatchmakingService",
                    "PhotoService", "MessagingService", "SwipeService"}


async def _check_service(client: httpx.AsyncClient, name: str, url: str) -> bool:
    try:
        resp = await client.get(url, timeout=3)
        return resp.status_code < 500
    except Exception:
        return False


async def _check_all_services(client: httpx.AsyncClient) -> dict[str, bool]:
    async def _check(name: str, url: str) -> tuple[str, bool]:
        ok = await _check_service(client, name, url)
        return name, ok

    tasks = [_check(n, info["health"]) for n, info in config.SERVICES.items()]
    pairs = await asyncio.gather(*tasks)
    return dict(pairs)


def _log_status_table(
    status: dict[str, bool],
    log: Callable[[str], None],
) -> tuple[list[str], list[str]]:
    up, down = [], []
    for name in config.SERVICES:
        label = _SERVICE_LABELS.get(name, name)
        if status.get(name):
            log(f"   ✅ {label}")
            up.append(name)
        else:
            log(f"   ❌ {label}")
            down.append(name)
    state.services_up = len(up)
    return up, down


def _run_script(
    script: str,
    label: str,
    log: Callable[[str], None],
    timeout: int = 180,
) -> bool:
    if not os.path.isfile(script):
        log(f"❌ Script not found: {script}")
        return False
    log(f"⚙️  Running {label}...")
    try:
        result = subprocess.run(
            ["bash", script],
            cwd=config.DATINGAPP_ROOT,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
        if result.returncode == 0:
            log(f"✅ {label} completed")
            return True
        else:
            for line in (result.stderr or result.stdout or "").strip().splitlines()[-8:]:
                log(f"   {line}")
            log(f"❌ {label} failed (exit code {result.returncode})")
            return False
    except subprocess.TimeoutExpired:
        log(f"❌ {label} timed out ({timeout}s)")
        return False
    except Exception as e:
        log(f"❌ {label} error: {e}")
        return False


async def _wait_for_services(
    names: set[str],
    log: Callable[[str], None],
    max_seconds: int = 60,
    poll_interval: int = 3,
) -> bool:
    total = len(names)
    attempts = max_seconds // poll_interval
    not_ready = list(names)

    async with httpx.AsyncClient() as client:
        for attempt in range(attempts):
            if state.cancelled:
                return False

            status = await _check_all_services(client)
            ready = [n for n in names if status.get(n)]
            not_ready = [n for n in names if not status.get(n)]
            state.services_up = sum(1 for v in status.values() if v)

            if not not_ready:
                log(f"   ✅ {total}/{total} services ready!")
                return True

            waiting_names = ", ".join(_SERVICE_LABELS.get(n, n) for n in not_ready)
            elapsed = attempt * poll_interval
            log(f"   ⏳ {len(ready)}/{total} ready ({elapsed}s) — waiting for: {waiting_names}")
            state.startup_phase = f"{len(ready)}/{total} services ready..."

            await asyncio.sleep(poll_interval)

    log(f"❌ Timeout after {max_seconds}s — these services never became healthy:")
    for n in not_ready:
        log(f"   ❌ {_SERVICE_LABELS.get(n, n)}")
    return False


async def _ensure_services_running(
    log: Callable[[str], None],
) -> bool:
    total = len(config.SERVICES)
    state.startup_phase = f"Checking {total} services..."
    log(f"🔍 Checking {total} services...")

    async with httpx.AsyncClient() as client:
        status = await _check_all_services(client)

    up, down = _log_status_table(status, log)

    if not down:
        log(f"✅ All {total} services healthy — ready to simulate!")
        state.startup_phase = ""
        return True

    log(f"")
    log(f"📊 {len(up)}/{total} services up, {len(down)} need starting")

    infra_down = [n for n in down if n in _INFRA_SERVICES]
    dotnet_down = [n for n in down if n in _DOTNET_SERVICES]

    if infra_down:
        state.startup_phase = "Starting Keycloak + databases..."
        log("")
        log("🐳 Step 1/2: Starting infrastructure (Keycloak + databases)...")
        ok = await asyncio.get_event_loop().run_in_executor(
            None, _run_script, config.INFRASTRUCTURE_SCRIPT, "infrastructure/start.sh", log, 180,
        )
        if not ok:
            state.startup_phase = "❌ Infrastructure failed"
            return False

        log("⏳ Waiting for Keycloak to become ready (can take 30-60s)...")
        state.startup_phase = "Waiting for Keycloak..."
        keycloak_ok = await _wait_for_services({"Keycloak"}, log, max_seconds=90, poll_interval=3)
        if not keycloak_ok:
            state.startup_phase = "❌ Keycloak didn't start"
            log("❌ Keycloak failed to become ready")
            return False
        log("✅ Keycloak is ready")
    else:
        log("")
        log("✅ Step 1/2: Infrastructure already running (Keycloak ✅)")

    if dotnet_down:
        state.startup_phase = f"Starting {len(dotnet_down)} .NET services..."
        log("")
        svc_names = ", ".join(_SERVICE_LABELS.get(n, n) for n in dotnet_down)
        log(f"🚀 Step 2/2: Starting .NET services ({svc_names})...")
        ok = await asyncio.get_event_loop().run_in_executor(
            None, _run_script, config.DEV_START_SCRIPT, "dev-start.sh", log, 180,
        )
        if not ok:
            state.startup_phase = "❌ dev-start.sh failed"
            return False

        log(f"⏳ Waiting for {len(dotnet_down)} .NET services to become healthy...")
        state.startup_phase = "Waiting for .NET services..."
        all_ok = await _wait_for_services(set(dotnet_down), log, max_seconds=60, poll_interval=3)
        if not all_ok:
            state.startup_phase = "❌ Some services didn't start"
            return False
    else:
        log("")
        log("✅ Step 2/2: All .NET services already running")

    log("")
    async with httpx.AsyncClient() as client:
        final_status = await _check_all_services(client)
    final_up = sum(1 for v in final_status.values() if v)
    state.services_up = final_up

    if final_up == total:
        state.startup_phase = ""
        log(f"🎉 All {total}/{total} services healthy — starting simulation!")
        return True
    else:
        still_down = [n for n, ok in final_status.items() if not ok]
        labels = ", ".join(_SERVICE_LABELS.get(n, n) for n in still_down)
        state.startup_phase = f"❌ {len(still_down)} services still down"
        log(f"❌ {final_up}/{total} up — still down: {labels}")
        return False


# ─── Keycloak Registration for Local Bots ───────────────────────────────────


async def _register_bots_in_keycloak(
    bots: list[dict],
    log: Callable[[str], None],
) -> list[dict]:
    """
    Register local-only bots in Keycloak + create UserService profiles.
    Returns the list of bots that were successfully registered.
    """
    need_registration = [b for b in bots if not b.get("keycloak_id")]
    if not need_registration:
        log("✅ All bots already registered in Keycloak")
        return bots

    log(f"🔑 Registering {len(need_registration)} local bots in Keycloak...")
    state.startup_phase = f"Registering bots in Keycloak (0/{len(need_registration)})..."

    async with httpx.AsyncClient(timeout=30) as client:
        admin_token = await _get_admin_token(client)
        if not admin_token:
            log("❌ Could not get Keycloak admin token — cannot register bots")
            return []

        registered = 0
        failed = 0
        for i, bot in enumerate(need_registration):
            if state.cancelled:
                log(f"⛔ Cancelled after registering {registered} bots")
                break

            username = bot["username"]
            try:
                resp = await client.post(
                    f"{config.KEYCLOAK_URL}/admin/realms/{config.KEYCLOAK_REALM}/users",
                    headers={"Authorization": f"Bearer {admin_token}"},
                    json={
                        "username": username,
                        "email": bot.get("email", f"{username}@bot.local"),
                        "firstName": bot.get("first_name", username),
                        "lastName": bot.get("last_name", "Bot"),
                        "enabled": True,
                        "emailVerified": True,
                        "credentials": [
                            {"type": "password", "value": config.DEFAULT_BOT_PASSWORD, "temporary": False}
                        ],
                        "attributes": {"bot": ["true"]},
                    },
                )
                if resp.status_code == 201:
                    kc_id = resp.headers.get("location", "").split("/")[-1]
                    bot["keycloak_id"] = kc_id
                    registered += 1
                elif resp.status_code == 409:
                    bot["keycloak_id"] = "existing"
                    registered += 1
                else:
                    failed += 1
                    if i < 3:
                        log(f"   ❌ {username}: HTTP {resp.status_code} — {resp.text[:100]}")
            except Exception as e:
                failed += 1
                if i < 3:
                    log(f"   ❌ {username}: {e}")

            if (i + 1) % 10 == 0 or i == len(need_registration) - 1:
                state.startup_phase = f"Registering bots ({i+1}/{len(need_registration)})..."
                log(f"   👤 {i+1}/{len(need_registration)} processed ({registered} ok, {failed} failed)")

            await asyncio.sleep(0.02)

        # Create UserService profiles for each registered bot
        log(f"📝 Creating {registered} user profiles in UserService...")
        state.startup_phase = f"Creating profiles (0/{registered})..."
        profile_ok = 0
        profile_fail = 0
        registered_bots = [b for b in bots if b.get("keycloak_id")]

        for i, bot in enumerate(registered_bots):
            if state.cancelled:
                break
            username = bot["username"]
            try:
                token_resp = await client.post(
                    f"{config.KEYCLOAK_URL}/realms/{config.KEYCLOAK_REALM}/protocol/openid-connect/token",
                    data={
                        "grant_type": "password",
                        "client_id": config.KEYCLOAK_CLIENT_ID,
                        "username": username,
                        "password": config.DEFAULT_BOT_PASSWORD,
                    },
                )
                if token_resp.status_code != 200:
                    profile_fail += 1
                    if i < 3:
                        log(f"   ❌ Token for {username}: HTTP {token_resp.status_code}")
                    continue

                user_token = token_resp.json()["access_token"]

                # Check if profile already exists
                check = await client.get(
                    f"{config.USER_SERVICE_URL}/api/profiles/me",
                    headers={"Authorization": f"Bearer {user_token}"},
                )
                if check.status_code == 200:
                    data = check.json()
                    if isinstance(data, dict) and "data" in data:
                        bot["user_service_id"] = data["data"].get("id")
                    profile_ok += 1
                    continue  # already exists

                # Create profile via POST /api/UserProfiles
                payload = {
                    "name": f"{bot.get('first_name', username)} {bot.get('last_name', 'Bot')}",
                    "email": bot.get("email", f"{username}@bot.local"),
                    "preferences": bot.get("preferences", "both"),
                    "bio": bot.get("bio", ""),
                    "gender": bot.get("gender", "other").capitalize(),
                    "dateOfBirth": bot.get("date_of_birth", "2000-01-01"),
                    "city": bot.get("city", "Stockholm"),
                    "interests": bot.get("interests", []),
                    "occupation": bot.get("occupation", ""),
                    "height": bot.get("height_cm", 175),
                    "relationshipType": bot.get("relationship_goal", "open_to_anything"),
                }

                profile_resp = await client.post(
                    f"{config.USER_SERVICE_URL}/api/UserProfiles",
                    headers={"Authorization": f"Bearer {user_token}", "Content-Type": "application/json"},
                    json=payload,
                )
                if profile_resp.status_code in (200, 201):
                    data = profile_resp.json()
                    if isinstance(data, dict) and "data" in data:
                        bot["user_service_id"] = data["data"].get("id")
                    profile_ok += 1
                else:
                    profile_fail += 1
                    if i < 3:
                        log(f"   ❌ Profile for {username}: HTTP {profile_resp.status_code} — {profile_resp.text[:100]}")

            except Exception as e:
                profile_fail += 1
                if i < 3:
                    log(f"   ❌ Profile for {username}: {e}")

            if (i + 1) % 10 == 0 or i == len(registered_bots) - 1:
                state.startup_phase = f"Creating profiles ({i+1}/{len(registered_bots)})..."

            await asyncio.sleep(0.02)

        log(f"✅ Registration done: {registered} Keycloak users, {profile_ok} profiles created")
        if failed > 0 or profile_fail > 0:
            log(f"   ⚠️  {failed} registration failures, {profile_fail} profile failures")

    state.startup_phase = ""
    return [b for b in bots if b.get("keycloak_id")]


async def _get_admin_token(client: httpx.AsyncClient) -> str | None:
    """Get Keycloak admin token."""
    try:
        resp = await client.post(
            f"{config.KEYCLOAK_URL}/realms/master/protocol/openid-connect/token",
            data={
                "grant_type": "client_credentials",
                "client_id": "admin-cli",
                "username": config.KEYCLOAK_ADMIN_USER,
                "password": config.KEYCLOAK_ADMIN_PASS,
            },
        )
        if resp.status_code == 200:
            return resp.json()["access_token"]
        resp = await client.post(
            f"{config.KEYCLOAK_URL}/realms/master/protocol/openid-connect/token",
            data={
                "grant_type": "password",
                "client_id": "admin-cli",
                "username": config.KEYCLOAK_ADMIN_USER,
                "password": config.KEYCLOAK_ADMIN_PASS,
            },
        )
        if resp.status_code == 200:
            return resp.json()["access_token"]
        return None
    except Exception:
        return None


# ─── API Helpers (using correct endpoints from Swagger specs) ────────────────


async def _get_user_token(client: httpx.AsyncClient, username: str, bot: dict | None = None) -> str | None:
    """Get access token for a bot user. Also stores keycloak_sub on the bot dict."""
    try:
        resp = await client.post(
            f"{config.KEYCLOAK_URL}/realms/{config.KEYCLOAK_REALM}/protocol/openid-connect/token",
            data={
                "grant_type": "password",
                "client_id": config.KEYCLOAK_CLIENT_ID,
                "username": username,
                "password": config.DEFAULT_BOT_PASSWORD,
            },
        )
        if resp.status_code == 200:
            token = resp.json()["access_token"]
            # Extract and cache Keycloak sub claim for messaging
            if bot and not bot.get("keycloak_sub"):
                try:
                    import base64
                    parts = token.split(".")
                    payload_b64 = parts[1] + "=" * (4 - len(parts[1]) % 4)
                    payload = json.loads(base64.b64decode(payload_b64))
                    bot["keycloak_sub"] = payload.get("sub")
                except Exception:
                    pass
            return token
        return None
    except Exception:
        return None


async def _get_my_profile(client: httpx.AsyncClient, token: str) -> dict | None:
    """Get the bot's own profile from UserService (GET /api/profiles/me)."""
    try:
        resp = await client.get(
            f"{config.USER_SERVICE_URL}/api/profiles/me",
            headers={"Authorization": f"Bearer {token}"},
        )
        if resp.status_code == 200:
            data = resp.json()
            if isinstance(data, dict) and "data" in data:
                return data["data"]
            return data
        return None
    except Exception:
        return None


async def _get_candidates(client: httpx.AsyncClient, token: str, user_id: int) -> list[dict]:
    """Fetch swipe candidates via POST /api/Matchmaking/find-matches.

    Request body: { userId: int, limit: int }
    Response: { matches: [...], count: int, ... }
    """
    try:
        resp = await client.post(
            f"{config.MATCHMAKING_URL}/api/Matchmaking/find-matches",
            headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
            json={"userId": user_id, "limit": 200, "minScore": 0},
        )
        if resp.status_code == 200:
            data = resp.json()
            # Response has "matches" array
            return data.get("matches", [])
        return []
    except Exception:
        return []


async def _swipe(client: httpx.AsyncClient, token: str, user_id: int, target_user_id: int, is_like: bool) -> dict | None:
    """Perform a swipe via POST /api/Swipes.

    Request body: { userId: int, targetUserId: int, isLike: bool, idempotencyKey: str }
    """
    import uuid
    try:
        resp = await client.post(
            f"{config.SWIPE_SERVICE_URL}/api/Swipes",
            headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
            json={
                "userId": user_id,
                "targetUserId": target_user_id,
                "isLike": is_like,
                "idempotencyKey": str(uuid.uuid4()),
            },
        )
        if resp.status_code in (200, 201):
            result = resp.json()
            # Unwrap envelope: {success, data: {isMutualMatch, matchId}}
            return result.get("data", result)
        if resp.status_code == 400:
            # 400 = already swiped or self-swipe — not a real swipe
            return None
        return None
    except Exception:
        return None


async def _send_message(client: httpx.AsyncClient, token: str, recipient_user_id: str, text: str) -> bool:
    """Send a message via POST /api/Messages.

    Request body: { recipientUserId: str, text: str }
    """
    try:
        resp = await client.post(
            f"{config.MESSAGING_URL}/api/Messages",
            headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
            json={"recipientUserId": str(recipient_user_id), "text": text},
        )
        return resp.status_code in (200, 201)
    except Exception:
        return False


# ─── Bot Simulation Loop ────────────────────────────────────────────────────


async def _simulate_bot(client: httpx.AsyncClient, bot: dict, log: Callable[[str], None]):
    """Simulate one bot's behavior for one cycle."""
    username = bot.get("username")
    display = bot.get("display_name", username)

    token = await _get_user_token(client, username, bot)
    if not token:
        state.errors += 1
        if state.errors <= 5:
            log(f"   ⚠️  Token failed for {display} ({username}) — not in Keycloak?")
        elif state.errors == 6:
            log(f"   ⚠️  (suppressing further token errors...)")
        return

    # Get our UserService profile ID (needed for matchmaking + swiping)
    user_id = bot.get("user_service_id")
    if not user_id:
        profile = await _get_my_profile(client, token)
        if profile:
            user_id = profile.get("id")
            bot["user_service_id"] = user_id
        else:
            state.errors += 1
            if state.errors <= 5:
                log(f"   ⚠️  No profile for {display} — run seeder in keycloak mode first")
            return

    candidates = await _get_candidates(client, token, user_id)
    if not candidates:
        return

    # Filter out already-swiped candidates FIRST, then pick from the rest
    fresh_candidates = []
    for c in candidates:
        tid = c.get("targetUserId") or c.get("userId") or c.get("id")
        if tid and int(tid) != user_id and (user_id, int(tid)) not in state.swiped_pairs:
            fresh_candidates.append(c)

    if not fresh_candidates:
        return  # nothing new to swipe — skip silently

    random.shuffle(fresh_candidates)
    swipe_count = min(len(fresh_candidates), random.randint(3, 8))
    for candidate in fresh_candidates[:swipe_count]:
        if state.cancelled:
            return

        target_id = candidate.get("targetUserId") or candidate.get("userId") or candidate.get("id")
        pair = (user_id, int(target_id))

        is_like = random.random() < config.SWIPE_RIGHT_PROBABILITY

        result = await _swipe(client, token, user_id, int(target_id), is_like)
        if result is not None:
            state.swipes += 1
            state.swiped_pairs.add(pair)
            emoji = "❤️" if is_like else "👎"
            target_name = (candidate.get("userProfile") or {}).get("city", "")
            log(f"   {emoji} {display} → user {target_id} {f'({target_name})' if target_name else ''}")
        else:
            # 400 = already swiped (from prior session) — track it to skip next time
            state.swiped_pairs.add(pair)

        if result and result.get("isMutualMatch"):
            state.matches += 1
            log(f"💕 MATCH! {display} ↔ user {target_id}!")

            # MessagingService requires Keycloak UUIDs, not integer IDs
            # Look up target's keycloak_sub from the bot list
            target_sub = None
            target_bot = None
            for b in seeder_state.bot_users:
                if b.get("user_service_id") == int(target_id):
                    target_sub = b.get("keycloak_sub")
                    target_bot = b
                    break

            # If sub wasn't cached yet, fetch a token for the target to populate it
            if not target_sub and target_bot:
                await _get_user_token(client, target_bot["username"], target_bot)
                target_sub = target_bot.get("keycloak_sub")

            if not target_sub:
                log(f"   ℹ️  Cannot message user {target_id} — no Keycloak UUID cached")
            else:
                _load_conversations()
                if _conversations:
                    convo = random.choice(_conversations)
                    for msg in convo[:random.randint(1, len(convo))]:
                        if state.cancelled:
                            return
                        if await _send_message(client, token, target_sub, msg):
                            state.messages_sent += 1
                        delay = config.MESSAGE_DELAY_SEC * state.speed
                        await asyncio.sleep(delay * random.uniform(0.5, 1.5))

        delay = config.SWIPE_DELAY_SEC * state.speed
        await asyncio.sleep(delay * random.uniform(0.5, 1.5))


async def run_simulation(
    log_callback: Callable[[str], None] | None = None,
    mode: str = "live",
    cycles: int = 0,
):
    """
    Run the behavior simulation.

    Modes:
      - "live": Real API calls — auto-starts services if needed
      - "dry-run": Simulated activity without calling any APIs

    Cycles:
      - 0: Run forever until stopped
      - N: Run N cycles then stop
    """

    def log(msg: str):
        if log_callback:
            log_callback(msg)

    if state.running:
        log("⚠️  Simulation already running!")
        return

    bots = seeder_state.bot_users
    if not bots:
        log("⚠️  No bot users found — go to 🌱 Seed tab and seed some bots first!")
        return

    state.reset()
    state.running = True
    state.active_bots = len(bots)

    # ── Live mode: check + auto-start services ──
    if mode == "live":
        total = len(config.SERVICES)
        log(f"🔎 Live mode — checking {total} services before starting...")
        log("")
        ok = await _ensure_services_running(log)
        if not ok:
            log("")
            log("❌ Cannot start simulation — fix the services above and try again")
            state.running = False
            return
        if state.cancelled:
            log("⛔ Cancelled during startup")
            state.running = False
            return
        log("")

        # Auto-register local bots in Keycloak if needed
        has_keycloak_ids = any(b.get("keycloak_id") for b in bots)
        if not has_keycloak_ids:
            log("📋 Bots were seeded locally — registering them in Keycloak for live mode...")
            log("")
            bots = await _register_bots_in_keycloak(bots, log)
            if not bots:
                log("")
                log("❌ No bots could be registered — cannot simulate")
                state.running = False
                return
            seeder_state.bot_users = bots
            state.active_bots = len(bots)
            log("")
            log(f"✅ {len(bots)} bots ready for live simulation")
            log("")

    log(f"🚀 Simulation started — {len(bots)} bots, mode: {mode}")

    cycle = 0
    async with httpx.AsyncClient(timeout=30) as client:
        while not state.cancelled:
            cycle += 1
            if cycles > 0 and cycle > cycles:
                break

            log(f"🔄 Cycle {cycle}" + (f"/{cycles}" if cycles > 0 else "") +
                f" — Swipes: {state.swipes} | Matches: {state.matches} | Msgs: {state.messages_sent}" +
                (f" | 🔁 Skipped: {state.dupes_skipped}" if state.dupes_skipped > 0 else "") +
                (f" | ⚠️ Errors: {state.errors}" if state.errors > 0 else ""))

            if mode == "live":
                active = random.sample(bots, k=len(bots))  # all bots participate every cycle
                tasks = [_simulate_bot(client, bot, log) for bot in active]
                await asyncio.gather(*tasks, return_exceptions=True)
            else:
                for bot in random.sample(bots, k=min(10, len(bots))):
                    display = bot.get("display_name", bot.get("username", "?"))
                    state.swipes += random.randint(1, 5)
                    if random.random() < 0.2:
                        state.matches += 1
                        state.messages_sent += random.randint(1, 3)
                        log(f"💕 [dry-run] {display} matched!")
                    await asyncio.sleep(0.1)

            delay = config.SWIPE_DELAY_SEC * state.speed
            await asyncio.sleep(delay)

    state.running = False
    state.startup_phase = ""
    log(f"🏁 Simulation done — {state.swipes} swipes, {state.matches} matches, {state.messages_sent} messages")
    if state.errors > 0:
        log(f"   ⚠️  {state.errors} errors during simulation (check logs above)")


def stop_simulation():
    """Signal the simulation to stop."""
    state.cancelled = True
