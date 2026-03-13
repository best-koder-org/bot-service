"""Profile seeder — loads pre-bundled Swedish users and builds enriched profiles instantly."""
import asyncio
import os
import json
import random
import uuid
from datetime import datetime, timedelta
from pathlib import Path
from typing import Callable

import httpx

try:
    import pymysql
    HAS_PYMYSQL = True
except ImportError:
    HAS_PYMYSQL = False
from faker import Faker

from . import config

fake = Faker("sv_SE")
DATA_DIR = Path(__file__).parent / "data"
SEED_USERS_FILE = DATA_DIR / "seed_users.json"


class SeederState:
    """Shared state for the seeder, observable by the dashboard."""

    def __init__(self):
        self.reset()

    def reset(self):
        self.total = 0
        self.created = 0
        self.skipped = 0
        self.failed = 0
        self.running = False
        self.cancelled = False
        self.bot_users: list[dict] = []

    @property
    def progress(self) -> float:
        return (self.created + self.skipped) / max(self.total, 1)


state = SeederState()


def _load_seed_users() -> list[dict]:
    """Load pre-bundled user templates from seed_users.json."""
    if SEED_USERS_FILE.exists():
        with open(SEED_USERS_FILE) as f:
            return json.load(f)
    return []


def _build_profile(raw_user: dict) -> dict:
    """Build a full DatingApp profile from seed data + faker enrichment."""
    gender = raw_user.get("gender", random.choice(["male", "female"]))
    dob = raw_user.get("dob", {})
    age = dob.get("age", random.randint(22, 45))
    location = raw_user.get("location", {})
    city = location.get("city", random.choice(config.SWEDISH_CITIES))
    name_data = raw_user.get("name", {})
    first_name = name_data.get("first", fake.first_name())
    last_name = name_data.get("last", fake.last_name())
    pic = raw_user.get("picture", {})
    photo_url = pic.get("large", f"https://i.pravatar.cc/400?u={uuid.uuid4()}")
    login_data = raw_user.get("login", {})
    username = login_data.get("username", fake.user_name())
    email = raw_user.get("email", fake.email())

    interests = random.sample(config.INTERESTS_POOL, k=random.randint(3, 7))
    occupation = random.choice(config.OCCUPATIONS_POOL)

    bio_template = random.choice(config.BIO_TEMPLATES)
    bio = bio_template.format(
        interest1=interests[0],
        interest2=interests[1],
        interest3=interests[2] if len(interests) > 2 else interests[0],
        occupation=occupation,
        city=city,
    )

    prompts = []
    selected_questions = random.sample(config.PROMPT_QUESTIONS, k=random.randint(2, 4))
    for q in selected_questions:
        prompts.append(
            {"question": q, "answer": random.choice(config.PROMPT_ANSWERS_POOL)}
        )

    height_cm = random.randint(155, 195) if gender == "male" else random.randint(150, 180)

    # Map gender to a preferences value for the UserService
    if gender == "male":
        pref = random.choice(["women", "both"])
    elif gender == "female":
        pref = random.choice(["men", "both"])
    else:
        pref = "both"

    return {
        "username": username,
        "email": email,
        "first_name": first_name,
        "last_name": last_name,
        "display_name": first_name,
        "gender": gender,
        "age": age,
        "date_of_birth": (datetime.now() - timedelta(days=age * 365)).isoformat()[:10],
        "city": city,
        "latitude": location.get("coordinates", {}).get("latitude", str(fake.latitude())),
        "longitude": location.get("coordinates", {}).get("longitude", str(fake.longitude())),
        "bio": bio,
        "occupation": occupation,
        "interests": interests,
        "height_cm": height_cm,
        "photo_url": photo_url,
        "photo_thumbnail": pic.get("thumbnail", photo_url),
        "photos": [
            {"url": photo_url, "is_primary": True, "order_index": 0},
        ],
        "prompts": prompts,
        "relationship_goal": random.choice(config.RELATIONSHIP_GOALS),
        "preferences": pref,
        "is_bot": True,
        "is_verified": random.random() > 0.3,
        "is_online": random.random() > 0.5,
        "preference": {
            "distance_km": random.choice([10, 25, 50, 100]),
            "age_range": {
                "min": max(18, age - random.randint(3, 8)),
                "max": age + random.randint(3, 8),
            },
            "relationship_goals": random.choice(config.RELATIONSHIP_GOALS),
        },
    }


# ── Keycloak helpers (only used in keycloak mode) ───────────────────────────

async def _get_keycloak_admin_token(client: httpx.AsyncClient) -> str | None:
    try:
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


async def _lookup_keycloak_user(client: httpx.AsyncClient, token: str, username: str) -> str | None:
    """Look up existing Keycloak user by username, return their ID or None."""
    try:
        resp = await client.get(
            f"{config.KEYCLOAK_URL}/admin/realms/{config.KEYCLOAK_REALM}/users",
            headers={"Authorization": f"Bearer {token}"},
            params={"username": username, "exact": "true"},
        )
        if resp.status_code == 200:
            users = resp.json()
            if users:
                return users[0]["id"]
        return None
    except Exception:
        return None


async def _update_existing_user(client: httpx.AsyncClient, token: str, kc_user_id: str, user_data: dict) -> bool:
    """Update an existing Keycloak user with full profile data + reset password."""
    try:
        resp = await client.put(
            f"{config.KEYCLOAK_URL}/admin/realms/{config.KEYCLOAK_REALM}/users/{kc_user_id}",
            headers={"Authorization": f"Bearer {token}"},
            json={
                "firstName": user_data["first_name"],
                "lastName": user_data["last_name"],
                "email": user_data["email"],
                "emailVerified": True,
                "enabled": True,
                "requiredActions": [],
                "attributes": {"bot": ["true"]},
            },
        )
        if resp.status_code not in (204, 200):
            return False

        resp2 = await client.put(
            f"{config.KEYCLOAK_URL}/admin/realms/{config.KEYCLOAK_REALM}/users/{kc_user_id}/reset-password",
            headers={"Authorization": f"Bearer {token}"},
            json={"type": "password", "value": config.DEFAULT_BOT_PASSWORD, "temporary": False},
        )
        return resp2.status_code == 204
    except Exception:
        return False


async def _create_keycloak_user(client: httpx.AsyncClient, token: str, user_data: dict) -> tuple[str | None, str]:
    """
    Create a Keycloak user. Returns (user_id, status) where status is one of:
      - "created", "exists", or "failed"
    """
    try:
        resp = await client.post(
            f"{config.KEYCLOAK_URL}/admin/realms/{config.KEYCLOAK_REALM}/users",
            headers={"Authorization": f"Bearer {token}"},
            json={
                "username": user_data["username"],
                "email": user_data["email"],
                "firstName": user_data["first_name"],
                "lastName": user_data["last_name"],
                "enabled": True,
                "emailVerified": True,
                "credentials": [{"type": "password", "value": config.DEFAULT_BOT_PASSWORD, "temporary": False}],
                "attributes": {"bot": ["true"]},
            },
        )
        if resp.status_code == 201:
            kc_id = resp.headers.get("location", "").split("/")[-1]
            return kc_id, "created"

        if resp.status_code == 409:
            kc_id = await _lookup_keycloak_user(client, token, user_data["username"])
            if kc_id:
                await _update_existing_user(client, token, kc_id, user_data)
                return kc_id, "exists"
            return None, "failed"

        return None, f"failed(HTTP {resp.status_code}: {resp.text[:200]})"
    except Exception as e:
        return None, f"failed({e})"


async def _get_user_token(client: httpx.AsyncClient, username: str) -> str | None:
    try:
        resp = await client.post(
            f"{config.KEYCLOAK_URL}/realms/{config.KEYCLOAK_REALM}/protocol/openid-connect/token",
            data={"grant_type": "password", "client_id": config.KEYCLOAK_CLIENT_ID,
                  "username": username, "password": config.DEFAULT_BOT_PASSWORD},
        )
        if resp.status_code == 200:
            return resp.json()["access_token"]
        return None
    except Exception:
        return None


async def _create_user_profile(client: httpx.AsyncClient, token: str, profile: dict, log: Callable[[str], None] | None = None) -> bool:
    """Create a user profile in UserService via POST /api/UserProfiles.

    Required fields: name, email, preferences
    Optional: bio, gender, dateOfBirth, city, interests, occupation, height, etc.
    """
    try:
        # First check if profile already exists
        check = await client.get(
            f"{config.USER_SERVICE_URL}/api/profiles/me",
            headers={"Authorization": f"Bearer {token}"},
        )
        if check.status_code == 200:
            # Profile exists — extract the ID for matchmaking
            data = check.json()
            if isinstance(data, dict) and "data" in data:
                profile["user_service_id"] = data["data"].get("id")
            elif isinstance(data, dict):
                profile["user_service_id"] = data.get("id")
            return True  # already exists, skip creation

        # Build the payload matching CreateUserProfileDto schema
        payload = {
            "name": f"{profile['first_name']} {profile['last_name']}",
            "email": profile["email"],
            "preferences": profile.get("preferences", "both"),
            "bio": profile.get("bio", ""),
            "gender": profile.get("gender", "other").capitalize(),
            "dateOfBirth": profile.get("date_of_birth", "2000-01-01"),
            "city": profile.get("city", "Stockholm"),
            "interests": profile.get("interests", []),
            "occupation": profile.get("occupation", ""),
            "height": profile.get("height_cm", 175),
            "relationshipType": profile.get("relationship_goal", "open_to_anything"),
        }

        # Add coordinates if available
        lat = profile.get("latitude")
        lon = profile.get("longitude")
        if lat is not None and lon is not None:
            try:
                payload["latitude"] = float(lat)
                payload["longitude"] = float(lon)
            except (ValueError, TypeError):
                pass

        resp = await client.post(
            f"{config.USER_SERVICE_URL}/api/UserProfiles",
            headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json"},
            json=payload,
        )
        if resp.status_code in (200, 201):
            data = resp.json()
            # Extract userService ID for matchmaking
            if isinstance(data, dict) and "data" in data:
                profile["user_service_id"] = data["data"].get("id")
            elif isinstance(data, dict):
                profile["user_service_id"] = data.get("id")
            return True
        else:
            if log:
                log(f"   ⚠️  Profile creation failed for {profile.get('username')}: HTTP {resp.status_code} — {resp.text[:150]}")
            return False
    except Exception as e:
        if log:
            log(f"   ⚠️  Profile creation error for {profile.get('username')}: {e}")
        return False





def _sync_swipe_profile_mappings(profiles: list[dict], log: Callable[[str], None] | None = None) -> int:
    """Sync Keycloak UUID → UserService ID mappings to SwipeService's UserProfileMappings table.
    
    The SwipeService match-check endpoint needs this mapping to translate
    Keycloak UUIDs (used by MessagingService) into integer profile IDs.
    """
    if not HAS_PYMYSQL:
        if log:
            log("⚠️  pymysql not installed — skipping SwipeService mapping sync")
        return 0
    
    db_host = os.getenv("SWIPE_DB_HOST", "localhost")
    db_port = int(os.getenv("SWIPE_DB_PORT", "3310"))
    db_user = os.getenv("SWIPE_DB_USER", "root")
    db_pass = os.getenv("SWIPE_DB_PASS", "root_password")
    db_name = os.getenv("SWIPE_DB_NAME", "SwipeServiceDb")
    
    try:
        conn = pymysql.connect(
            host=db_host, port=db_port, user=db_user,
            password=db_pass, database=db_name, charset="utf8mb4",
        )
    except Exception as e:
        if log:
            log(f"⚠️  Could not connect to SwipeService DB ({db_host}:{db_port}): {e}")
        return 0
    
    synced = 0
    skipped = 0
    try:
        cursor = conn.cursor()
        for p in profiles:
            kc_sub = p.get("keycloak_sub")
            us_id = p.get("user_service_id")
            if not kc_sub or not us_id:
                continue
            
            cursor.execute("SELECT ProfileId FROM UserProfileMappings WHERE UserId = %s", (kc_sub,))
            if cursor.fetchone():
                skipped += 1
                continue
            
            try:
                cursor.execute(
                    "INSERT INTO UserProfileMappings (UserId, ProfileId, CreatedAt) VALUES (%s, %s, NOW())",
                    (kc_sub, us_id)
                )
                synced += 1
            except Exception:
                skipped += 1  # duplicate PK etc
        
        conn.commit()
    except Exception as e:
        if log:
            log(f"⚠️  Error syncing SwipeService mappings: {e}")
    finally:
        conn.close()
    
    if log:
        log(f"🔗 SwipeService mappings: {synced} synced, {skipped} already existed")
    return synced

def _sync_to_matchmaking_db(profiles: list[dict], log: Callable[[str], None] | None = None) -> int:
    """Sync user profiles to MatchmakingService's MySQL database directly.
    
    The MatchmakingService has its own isolated database and does not read from
    UserService. To make find-matches return candidates, profiles must be INSERTed
    into matchmaking_service_db.UserProfiles.
    """
    if not HAS_PYMYSQL:
        if log:
            log("⚠️  pymysql not installed — skipping MatchmakingService DB sync")
            log("   Install with: pip install pymysql")
        return 0
    
    db_host = os.getenv("MATCHMAKING_DB_HOST", "localhost")
    db_port = int(os.getenv("MATCHMAKING_DB_PORT", "3309"))
    db_user = os.getenv("MATCHMAKING_DB_USER", "root")
    db_pass = os.getenv("MATCHMAKING_DB_PASS", "root_password")
    db_name = os.getenv("MATCHMAKING_DB_NAME", "matchmaking_service_db")
    
    try:
        conn = pymysql.connect(
            host=db_host, port=db_port, user=db_user,
            password=db_pass, database=db_name, charset="utf8mb4",
        )
    except Exception as e:
        if log:
            log(f"⚠️  Could not connect to MatchmakingService DB ({db_host}:{db_port}): {e}")
        return 0
    
    synced = 0
    skipped = 0
    try:
        cursor = conn.cursor()
        for p in profiles:
            us_id = p.get("user_service_id")
            if not us_id:
                continue
            
            # Check if already exists
            cursor.execute("SELECT Id FROM UserProfiles WHERE UserId = %s", (us_id,))
            if cursor.fetchone():
                skipped += 1
                continue
            
            gender = (p.get("gender", "other") or "other").lower()
            age = p.get("age", 30)
            
            # Map preferences to PreferredGender  
            pref = p.get("preferences", "both")
            if pref == "men":
                pref_gender = "male"
            elif pref == "women":
                pref_gender = "female"
            else:
                pref_gender = "both"
            
            lat = float(p.get("latitude", 59.33))
            lon = float(p.get("longitude", 18.07))
            interests = json.dumps(p.get("interests", []))
            
            cursor.execute("""
                INSERT INTO UserProfiles (
                    UserId, Gender, Age, Latitude, Longitude, City, State, Country,
                    PreferredGender, MinAge, MaxAge, MaxDistance,
                    Interests, Education, Occupation, Height,
                    Religion, Ethnicity, WantsChildren, HasChildren,
                    SmokingStatus, DrinkingStatus,
                    LocationWeight, AgeWeight, InterestsWeight, EducationWeight, LifestyleWeight,
                    CreatedAt, UpdatedAt, IsActive
                ) VALUES (
                    %s, %s, %s, %s, %s, %s, %s, %s,
                    %s, %s, %s, %s,
                    %s, %s, %s, %s,
                    %s, %s, %s, %s,
                    %s, %s,
                    %s, %s, %s, %s, %s,
                    NOW(), NOW(), 1
                )
            """, (
                us_id, gender, age, lat, lon,
                p.get("city", "Stockholm"), "", "Sweden",
                pref_gender,
                max(18, age - 8), age + 8, 100,
                interests, "", p.get("occupation", ""), p.get("height_cm", 175),
                "", "", 0, 0,
                0, 0,
                1.0, 1.0, 1.0, 1.0, 1.0,
            ))
            synced += 1
        
        conn.commit()
    except Exception as e:
        if log:
            log(f"⚠️  Error syncing to MatchmakingService DB: {e}")
    finally:
        conn.close()
    
    if log:
        log(f"🔗 MatchmakingService DB: {synced} synced, {skipped} already existed")
    return synced


# ── Main seeder ──────────────────────────────────────────────────────────────

async def seed_bots(
    count: int = 50,
    log_callback: Callable[[str], None] | None = None,
    mode: str = "local",
) -> list[dict]:
    """
    Seed bot profiles.

    Modes:
      - "local": Load bundled seed_users.json, build profiles instantly (no network)
      - "keycloak": Also push to Keycloak + UserService (requires services running)
    """

    def log(msg: str):
        if log_callback:
            log_callback(msg)

    state.reset()
    state.total = count
    state.running = True

    log(f"🚀 Starting seeder — creating {count} bot profiles (mode: {mode})")

    # Step 1: Load pre-bundled user templates
    raw_users = _load_seed_users()
    if not raw_users:
        log("⚠️  seed_users.json not found — generating with Faker only")
        raw_users = [{} for _ in range(count)]
    else:
        log(f"📦 Loaded {len(raw_users)} pre-bundled user templates")

    while len(raw_users) < count:
        raw_users.extend(_load_seed_users())
    raw_users = raw_users[:count]

    # Step 2: Build enriched profiles (instant, no network)
    profiles = [_build_profile(u) for u in raw_users]

    # Deduplicate emails — Keycloak rejects duplicate emails
    seen_emails: set[str] = set()
    for p in profiles:
        if p["email"] in seen_emails:
            p["email"] = f"{p['username']}@bot.local"
        seen_emails.add(p["email"])

    state.created = len(profiles)
    state.bot_users = profiles
    log(f"✅ Built {len(profiles)} enriched profiles instantly")

    if mode == "local":
        state.running = False
        log(f"🏁 Done! {len(profiles)} bot profiles ready (local mode)")
        return profiles

    # Step 3: Keycloak mode — push to services
    log("🔑 Connecting to Keycloak...")
    async with httpx.AsyncClient(timeout=30) as client:
        admin_token = await _get_keycloak_admin_token(client)
        if not admin_token:
            log("⚠️  Could not get Keycloak admin token — profiles built locally only")
            state.running = False
            return profiles

        log("🔑 Got Keycloak admin token — pushing users...")
        state.created = 0

        for i, profile in enumerate(profiles):
            if state.cancelled:
                log(f"⛔ Cancelled after {state.created} bots")
                break

            username = profile["username"]
            try:
                kc_id, status = await _create_keycloak_user(client, admin_token, profile)

                if kc_id:
                    profile["keycloak_id"] = kc_id

                    if status == "exists":
                        state.skipped += 1
                    else:
                        state.created += 1

                    # Create UserService profile so the bot is discoverable
                    user_token = await _get_user_token(client, username)
                    if user_token:
                        # Cache keycloak sub for messaging
                        try:
                            import base64
                            parts = user_token.split(".")
                            payload_b64 = parts[1] + "=" * (4 - len(parts[1]) % 4)
                            payload = json.loads(base64.b64decode(payload_b64))
                            profile["keycloak_sub"] = payload.get("sub")
                        except Exception:
                            pass
                        ok = await _create_user_profile(client, user_token, profile, log)
                        us_id = profile.get("user_service_id", "?")
                        if ok and ((i + 1) % 10 == 0 or i == 0):
                            tag = "♻️ " if status == "exists" else "👤"
                            log(f"{tag} [{state.created + state.skipped}/{count}] {status}: {profile['display_name']}, {profile['age']}, {profile['city']} (profile_id={us_id})")
                    else:
                        log(f"   ⚠️  Could not get user token for {username} — profile not created in UserService")

                else:
                    state.failed += 1
                    log(f"❌ Failed to create Keycloak user: {username} ({status})")
            except Exception as e:
                state.failed += 1
                log(f"❌ Error creating {username}: {e}")

            await asyncio.sleep(0.05)

    state.running = False
    # Sync profiles to MatchmakingService database (separate from UserService)
    profiles_with_ids = [p for p in profiles if p.get("user_service_id")]
    if profiles_with_ids:
        log(f"🔗 Syncing {len(profiles_with_ids)} profiles to backend databases...")
        loop = asyncio.get_event_loop()
        await asyncio.gather(
            loop.run_in_executor(None, _sync_to_matchmaking_db, profiles_with_ids, log),
            loop.run_in_executor(None, _sync_swipe_profile_mappings, profiles_with_ids, log),
        )


    log(f"🏁 Seeding complete: {state.created} created, {state.skipped} already existed, {state.failed} failed")
    return state.bot_users


async def reset_bots(log_callback: Callable[[str], None] | None = None):
    """Delete all bot users from Keycloak and clear local state."""

    def log(msg: str):
        if log_callback:
            log_callback(msg)

    log("🗑️  Resetting all bot users...")
    async with httpx.AsyncClient(timeout=30) as client:
        admin_token = await _get_keycloak_admin_token(client)
        if not admin_token:
            log("⚠️  Could not get Keycloak admin token — clearing local state only")
            state.reset()
            return

        try:
            resp = await client.get(
                f"{config.KEYCLOAK_URL}/admin/realms/{config.KEYCLOAK_REALM}/users",
                headers={"Authorization": f"Bearer {admin_token}"},
                params={"max": 5000, "search": "bot_0"},
            )
            if resp.status_code != 200:
                log("⚠️  Could not fetch users — clearing local state only")
                state.reset()
                return

            users = resp.json()
            bot_users = [u for u in users if u.get("username", "").startswith("bot_")]
            log(f"Found {len(bot_users)} bot users to delete")

            deleted = 0
            for user in bot_users:
                try:
                    await client.delete(
                        f"{config.KEYCLOAK_URL}/admin/realms/{config.KEYCLOAK_REALM}/users/{user['id']}",
                        headers={"Authorization": f"Bearer {admin_token}"},
                    )
                    deleted += 1
                except Exception:
                    pass

            log(f"✅ Deleted {deleted} bot users from Keycloak")
        except Exception as e:
            log(f"❌ Error during reset: {e}")

    state.reset()
    log("🧹 Local state cleared")
