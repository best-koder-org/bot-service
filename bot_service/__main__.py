"""🤖 DatingApp Bot Dashboard — NiceGUI web UI on port 9091."""
import asyncio
import os

import httpx
from nicegui import ui, app

from . import config
from .seeder import seed_bots, reset_bots, state as seeder_state
from .simulator import run_simulation, stop_simulation, state as sim_state

try:
    import pymysql
    HAS_PYMYSQL = True
except ImportError:
    HAS_PYMYSQL = False

# ─── Shared State ────────────────────────────────────────────────────────────

_seed_task: asyncio.Task | None = None
_sim_task: asyncio.Task | None = None


# ─── Status Checker ──────────────────────────────────────────────────────────

async def check_seed_status() -> dict:
    """Check how many bot users exist in Keycloak, UserService, and DBs."""
    result = {
        "keycloak_bots": "?",
        "matchmaking_profiles": "?",
        "swipe_swipes": "?",
        "swipe_matches": "?",
        "seeder_memory": len(seeder_state.bot_users),
        "ready": False,
    }

    # Check Keycloak — search by bot_0 username prefix (bots may lack the bot:true attribute)
    try:
        async with httpx.AsyncClient(timeout=5) as client:
            # Get admin token
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
                admin_token = resp.json()["access_token"]
                # Search by username prefix "bot_0" which matches bot_000..bot_049 etc.
                resp2 = await client.get(
                    f"{config.KEYCLOAK_URL}/admin/realms/{config.KEYCLOAK_REALM}/users",
                    headers={"Authorization": f"Bearer {admin_token}"},
                    params={"max": 5000, "search": "bot_0"},
                )
                if resp2.status_code == 200:
                    users = resp2.json()
                    bots = [u for u in users if u.get("username", "").startswith("bot_")]
                    result["keycloak_bots"] = len(bots)
    except Exception:
        pass

    # Check databases
    if HAS_PYMYSQL:
        # Matchmaking DB
        try:
            conn = pymysql.connect(
                host=os.getenv("MATCHMAKING_DB_HOST", "localhost"),
                port=int(os.getenv("MATCHMAKING_DB_PORT", "3309")),
                user=os.getenv("MATCHMAKING_DB_USER", "root"),
                password=os.getenv("MATCHMAKING_DB_PASS", "root_password"),
                database=os.getenv("MATCHMAKING_DB_NAME", "matchmaking_service_db"),
                connect_timeout=3,
            )
            cur = conn.cursor()
            cur.execute("SELECT COUNT(*) FROM UserProfiles")
            result["matchmaking_profiles"] = cur.fetchone()[0]
            conn.close()
        except Exception:
            pass

        # SwipeService DB
        try:
            conn = pymysql.connect(
                host=os.getenv("SWIPE_DB_HOST", "localhost"),
                port=int(os.getenv("SWIPE_DB_PORT", "3310")),
                user=os.getenv("SWIPE_DB_USER", "root"),
                password=os.getenv("SWIPE_DB_PASS", "root_password"),
                database=os.getenv("SWIPE_DB_NAME", "SwipeServiceDb"),
                connect_timeout=3,
            )
            cur = conn.cursor()
            cur.execute("SELECT COUNT(*) FROM Swipes")
            result["swipe_swipes"] = cur.fetchone()[0]
            cur.execute("SELECT COUNT(*) FROM Matches")
            result["swipe_matches"] = cur.fetchone()[0]
            conn.close()
        except Exception:
            pass

    result["ready"] = isinstance(result["keycloak_bots"], int) and result["keycloak_bots"] > 0
    return result


def clear_swipe_db() -> str:
    """Delete all swipes and matches from SwipeService DB."""
    if not HAS_PYMYSQL:
        return "❌ pymysql not installed"
    try:
        conn = pymysql.connect(
            host=os.getenv("SWIPE_DB_HOST", "localhost"),
            port=int(os.getenv("SWIPE_DB_PORT", "3310")),
            user=os.getenv("SWIPE_DB_USER", "root"),
            password=os.getenv("SWIPE_DB_PASS", "root_password"),
            database=os.getenv("SWIPE_DB_NAME", "SwipeServiceDb"),
            connect_timeout=3,
        )
        cur = conn.cursor()
        cur.execute("DELETE FROM Matches")
        matches_deleted = cur.rowcount
        cur.execute("DELETE FROM Swipes")
        swipes_deleted = cur.rowcount
        conn.commit()
        conn.close()
        return f"🗑️ Cleared {swipes_deleted} swipes + {matches_deleted} matches from SwipeService DB"
    except Exception as e:
        return f"❌ Error clearing SwipeService DB: {e}"


def clear_matchmaking_suggestions() -> str:
    """Clear daily suggestion tracking so bots get fresh candidates."""
    if not HAS_PYMYSQL:
        return "❌ pymysql not installed"
    try:
        conn = pymysql.connect(
            host=os.getenv("MATCHMAKING_DB_HOST", "localhost"),
            port=int(os.getenv("MATCHMAKING_DB_PORT", "3309")),
            user=os.getenv("MATCHMAKING_DB_USER", "root"),
            password=os.getenv("MATCHMAKING_DB_PASS", "root_password"),
            database=os.getenv("MATCHMAKING_DB_NAME", "matchmaking_service_db"),
            connect_timeout=3,
        )
        cur = conn.cursor()
        cur.execute("DELETE FROM DailySuggestions")
        deleted = cur.rowcount
        conn.commit()
        conn.close()
        return f"🗑️ Cleared {deleted} daily suggestion records"
    except Exception as e:
        return f"❌ Error: {e}"


# ─── Dashboard Page ──────────────────────────────────────────────────────────

@ui.page("/")
def dashboard():
    """Build the full dashboard UI."""

    # ── Header ──
    with ui.header().classes("bg-indigo-700 text-white items-center justify-between"):
        ui.label("🤖 DatingApp Bot Dashboard").classes("text-xl font-bold")
        with ui.row().classes("gap-4"):
            lbl_bots = ui.label("Bots: 0")
            lbl_swipes = ui.label("Swipes: 0")
            lbl_matches = ui.label("Matches: 0")
            lbl_msgs = ui.label("Msgs: 0")

    # ── Status timer — updates header counters every second ──
    def update_counters():
        lbl_bots.text = f"Bots: {seeder_state.created}"
        lbl_swipes.text = f"Swipes: {sim_state.swipes}"
        lbl_matches.text = f"Matches: {sim_state.matches}"
        lbl_msgs.text = f"Msgs: {sim_state.messages_sent}"

    ui.timer(1.0, update_counters)

    # ── Tabs ──
    with ui.tabs().classes("w-full") as tabs:
        tab_seed = ui.tab("🌱 Seed", label="🌱 Seed Profiles")
        tab_sim = ui.tab("🎮 Simulate", label="🎮 Simulate")
        tab_load = ui.tab("📊 Load Test", label="📊 Load Test")
        tab_logs = ui.tab("📋 Logs", label="📋 All Logs")

    with ui.tab_panels(tabs, value=tab_seed).classes("w-full flex-grow"):

        # ════════════════════════════════════════════════════════════════════
        # TAB 1: SEED PROFILES
        # ════════════════════════════════════════════════════════════════════
        with ui.tab_panel(tab_seed):

            # ── Status Panel ──
            with ui.card().classes("w-full p-4 mb-4"):
                ui.label("📊 Current State").classes("text-lg font-semibold mb-2")
                with ui.row().classes("w-full gap-6"):
                    with ui.column().classes("gap-1"):
                        ui.label("Keycloak Bots").classes("text-xs text-gray-500")
                        status_kc = ui.label("…").classes("text-2xl font-bold text-indigo-600")
                    with ui.column().classes("gap-1"):
                        ui.label("Matchmaking Profiles").classes("text-xs text-gray-500")
                        status_mm = ui.label("…").classes("text-2xl font-bold text-blue-600")
                    with ui.column().classes("gap-1"):
                        ui.label("DB Swipes").classes("text-xs text-gray-500")
                        status_sw = ui.label("…").classes("text-2xl font-bold text-orange-600")
                    with ui.column().classes("gap-1"):
                        ui.label("DB Matches").classes("text-xs text-gray-500")
                        status_ma = ui.label("…").classes("text-2xl font-bold text-pink-600")
                    with ui.column().classes("gap-1"):
                        ui.label("In Memory").classes("text-xs text-gray-500")
                        status_mem = ui.label("…").classes("text-2xl font-bold text-gray-600")

                status_verdict = ui.label("").classes("mt-2 text-sm font-medium")

                async def refresh_status():
                    status_verdict.text = "⏳ Checking..."
                    info = await check_seed_status()
                    status_kc.text = str(info["keycloak_bots"])
                    status_mm.text = str(info["matchmaking_profiles"])
                    status_sw.text = str(info["swipe_swipes"])
                    status_ma.text = str(info["swipe_matches"])
                    status_mem.text = str(info["seeder_memory"])

                    # Build verdict
                    kc = info["keycloak_bots"]
                    mm = info["matchmaking_profiles"]
                    mem = info["seeder_memory"]
                    sw = info["swipe_swipes"]

                    if isinstance(kc, int) and kc >= 50 and isinstance(mm, int) and mm >= 50:
                        if mem == 0:
                            status_verdict.text = "✅ 50+ bots exist in Keycloak + Matchmaking DB.  ⚠️ Not loaded in memory — click 🚀 Seed (keycloak mode) to reload them."
                        elif isinstance(sw, int) and sw > 0:
                            status_verdict.text = f"✅ Ready to simulate! Bots are seeded.  ℹ️ {sw} swipes already in DB — use 🧹 Reset Swipes on Simulate tab to start fresh."
                        else:
                            status_verdict.text = "✅ Ready to simulate! All bots seeded and no prior swipes."
                    elif isinstance(kc, int) and kc >= 50:
                        status_verdict.text = "⚠️ Bots in Keycloak but NOT in Matchmaking DB — re-seed in keycloak mode to sync."
                    elif kc == "?":
                        status_verdict.text = "❓ Could not reach Keycloak — services may be down."
                    else:
                        kc_count = kc if isinstance(kc, int) else 0
                        status_verdict.text = f"🌱 Only {kc_count} bots found — click 🚀 Seed Bots (keycloak mode) to create them."

                ui.button("🔄 Refresh Status", on_click=refresh_status, color="blue").classes("mt-2")

            # Auto-check on page load
            ui.timer(0.5, refresh_status, once=True)

            # ── Seed Controls ──
            seed_log = ui.log(max_lines=200).classes("w-full h-64")

            with ui.row().classes("w-full items-end gap-4 mt-4"):
                bot_count = ui.number("Bot count", value=config.DEFAULT_BOT_COUNT, min=1, max=500, step=10).classes("w-32")
                seed_mode = ui.select(
                    ["keycloak", "local"],
                    value="keycloak",
                    label="Mode",
                ).classes("w-40")

                async def on_seed():
                    global _seed_task
                    if seeder_state.running:
                        seed_log.push("⚠️  Seeder already running!")
                        return
                    seed_log.push("─" * 50)
                    _seed_task = asyncio.create_task(
                        seed_bots(
                            count=int(bot_count.value),
                            log_callback=seed_log.push,
                            mode=seed_mode.value,
                        )
                    )

                async def on_reset():
                    seed_log.push("─" * 50)
                    await reset_bots(log_callback=seed_log.push)
                    await refresh_status()

                def on_cancel_seed():
                    seeder_state.cancelled = True
                    seed_log.push("⛔ Cancelling...")

                ui.button("🚀 Seed Bots", on_click=on_seed, color="green")
                ui.button("⛔ Cancel", on_click=on_cancel_seed, color="orange")
                ui.button("🗑️ Reset All", on_click=on_reset, color="red")

            # Progress bar
            seed_progress = ui.linear_progress(value=0, show_value=False).classes("w-full mt-2")
            ui.timer(0.5, lambda: seed_progress.set_value(seeder_state.progress))

        # ════════════════════════════════════════════════════════════════════
        # TAB 2: SIMULATE
        # ════════════════════════════════════════════════════════════════════
        with ui.tab_panel(tab_sim):
            sim_log = ui.log(max_lines=300).classes("w-full h-64")

            # Startup phase banner (visible during service bring-up)
            startup_banner = ui.label("").classes(
                "w-full text-center text-lg font-semibold text-orange-600 py-2"
            )
            startup_banner.set_visibility(False)

            def update_startup_phase():
                phase = sim_state.startup_phase
                if phase:
                    startup_banner.text = f"⏳ {phase}"
                    startup_banner.set_visibility(True)
                else:
                    startup_banner.set_visibility(False)

            ui.timer(0.5, update_startup_phase)

            with ui.row().classes("w-full items-end gap-4 mt-4"):
                sim_mode = ui.select(
                    ["live", "dry-run"],
                    value="dry-run",
                    label="Mode",
                ).classes("w-40")
                sim_cycles = ui.number("Cycles (0=∞)", value=0, min=0, max=10000, step=1).classes("w-32")
                speed_slider = ui.slider(min=0.1, max=5.0, value=1.0, step=0.1).classes("w-48")
                ui.label().bind_text_from(speed_slider, "value", backward=lambda v: f"Speed: {v:.1f}x")

                def on_speed_change():
                    sim_state.speed = speed_slider.value

                speed_slider.on("update:model-value", on_speed_change)

                async def on_start_sim():
                    global _sim_task
                    if sim_state.running:
                        sim_log.push("⚠️  Simulation already running!")
                        return
                    if _sim_task is not None and not _sim_task.done():
                        sim_log.push("⚠️  Simulation task still active!")
                        return
                    sim_log.push("─" * 50)
                    _sim_task = asyncio.create_task(
                        run_simulation(
                            log_callback=sim_log.push,
                            mode=sim_mode.value,
                            cycles=int(sim_cycles.value),
                        )
                    )

                def on_stop_sim():
                    stop_simulation()
                    sim_log.push("⛔ Stopping simulation...")

                def on_reset_sim():
                    stop_simulation()
                    sim_state.reset()
                    sim_log.push("🧹 Simulator in-memory state reset (swiped_pairs cleared)")
                    sim_log.push("   ℹ️  Bots can re-swipe the same users now")

                ui.button("▶️ Start", on_click=on_start_sim, color="green")
                ui.button("⏹️ Stop", on_click=on_stop_sim, color="orange")
                ui.button("🔄 Reset Sim", on_click=on_reset_sim, color="red")

            # ── Reset controls ──
            with ui.row().classes("w-full gap-4 mt-2"):
                async def on_full_reset():
                    """Clear swipes+matches from DB, daily suggestions, AND in-memory state."""
                    sim_log.push("─" * 50)
                    sim_log.push("🧹 Full reset — clearing everything...")

                    # 1. Stop simulation
                    stop_simulation()

                    # 2. Clear DB (runs in thread to avoid blocking)
                    loop = asyncio.get_event_loop()
                    msg1 = await loop.run_in_executor(None, clear_swipe_db)
                    sim_log.push(msg1)

                    msg2 = await loop.run_in_executor(None, clear_matchmaking_suggestions)
                    sim_log.push(msg2)

                    # 3. Clear in-memory state
                    sim_state.reset()
                    sim_log.push("🧹 In-memory state reset")

                    sim_log.push("✅ Full reset complete — bots can swipe fresh!")

                ui.button("🧹 Reset Swipes + Matches (DB)", on_click=on_full_reset, color="deep-orange").props("outline")

            # Live stats
            with ui.row().classes("w-full gap-8 mt-4"):
                with ui.card().classes("p-4"):
                    ui.label("Active Bots").classes("text-sm text-gray-500")
                    stat_bots = ui.label("0").classes("text-3xl font-bold text-indigo-600")
                with ui.card().classes("p-4"):
                    ui.label("Swipes").classes("text-sm text-gray-500")
                    stat_swipes = ui.label("0").classes("text-3xl font-bold text-blue-600")
                with ui.card().classes("p-4"):
                    ui.label("Matches").classes("text-sm text-gray-500")
                    stat_matches = ui.label("0").classes("text-3xl font-bold text-pink-600")
                with ui.card().classes("p-4"):
                    ui.label("Messages").classes("text-sm text-gray-500")
                    stat_msgs = ui.label("0").classes("text-3xl font-bold text-green-600")
                with ui.card().classes("p-4"):
                    ui.label("Errors").classes("text-sm text-gray-500")
                    stat_errors = ui.label("0").classes("text-3xl font-bold text-red-600")

            def update_sim_stats():
                stat_bots.text = str(sim_state.active_bots)
                stat_swipes.text = str(sim_state.swipes)
                stat_matches.text = str(sim_state.matches)
                stat_msgs.text = str(sim_state.messages_sent)
                stat_errors.text = str(sim_state.errors)

            ui.timer(1.0, update_sim_stats)

        # ════════════════════════════════════════════════════════════════════
        # TAB 3: LOAD TESTING
        # ════════════════════════════════════════════════════════════════════
        with ui.tab_panel(tab_load):
            ui.label("📊 Load Testing").classes("text-xl font-bold mb-4")

            with ui.card().classes("w-full p-4"):
                ui.label("Locust (built-in)").classes("text-lg font-semibold")
                ui.markdown(
                    "Start Locust from the command line:\n\n"
                    "```bash\n"
                    "cd bot-service && python -m locust -f bot_service/locust_tests/locustfile.py\n"
                    "```\n\n"
                    "Then open **http://localhost:8089** for the Locust web UI."
                )

                async def launch_locust():
                    import subprocess
                    import sys
                    subprocess.Popen(
                        [sys.executable, "-m", "locust", "-f", "bot_service/locust_tests/locustfile.py"],
                        cwd="/home/m/development/DatingApp/bot-service",
                    )
                    ui.notify("🚀 Locust started on http://localhost:8089", type="positive")

                ui.button("🚀 Launch Locust Web UI", on_click=launch_locust, color="teal")

            with ui.card().classes("w-full p-4 mt-4"):
                ui.label("k6 (external)").classes("text-lg font-semibold")
                ui.markdown(
                    "Run k6 load tests:\n\n"
                    "```bash\n"
                    "k6 run bot-service/bot_service/load_tests/smoke.js\n"
                    "k6 run bot-service/bot_service/load_tests/spike.js\n"
                    "```"
                )

        # ════════════════════════════════════════════════════════════════════
        # TAB 4: ALL LOGS
        # ════════════════════════════════════════════════════════════════════
        with ui.tab_panel(tab_logs):
            ui.label("📋 Combined Activity Log").classes("text-xl font-bold mb-4")
            all_log = ui.log(max_lines=500).classes("w-full h-96")
            all_log.push("Dashboard started. Use the tabs above to seed bots and run simulations.")

            with ui.row().classes("mt-4"):
                ui.button("🧹 Clear Log", on_click=lambda: all_log.clear(), color="gray")

    # ── Footer ──
    with ui.footer().classes("bg-gray-100 text-gray-600 text-sm"):
        ui.label(f"Bot Dashboard v1.0 • Services: Gateway {config.GATEWAY_URL} • Keycloak {config.KEYCLOAK_URL}")


# ─── Entry Point ─────────────────────────────────────────────────────────────

ui.run(
    title="🤖 DatingApp Bot Dashboard",
    port=config.DASHBOARD_PORT,
    reload=False,
    show=False,
)
