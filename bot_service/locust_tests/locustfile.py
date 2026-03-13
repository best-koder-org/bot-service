"""Locust load test — simulates dating app user behavior at scale."""
import random
import json
import requests as ext_requests

from locust import HttpUser, task, between, events


KEYCLOAK_URL = "http://localhost:8090"
REALM = "DatingApp"
CLIENT_ID = "dejtingapp-flutter"
BOT_PASSWORD = "BotPass123!"

# Bot usernames (must match Keycloak)
BOT_USERNAMES = [f"bot_{i:03d}" for i in range(50)]


class DatingAppUser(HttpUser):
    """Simulates a real dating app user's behavior."""

    wait_time = between(1, 5)
    host = "http://localhost:8080"

    def on_start(self):
        """Login and get token."""
        self.username = random.choice(BOT_USERNAMES)
        self.token = self._get_token()
        self.headers = {}
        if self.token:
            self.headers = {"Authorization": f"Bearer {self.token}"}
        self.my_matches = []

    def _get_token(self) -> str | None:
        """Authenticate via Keycloak (direct HTTP, not through Locust client)."""
        try:
            resp = ext_requests.post(
                f"{KEYCLOAK_URL}/realms/{REALM}/protocol/openid-connect/token",
                data={
                    "grant_type": "password",
                    "client_id": CLIENT_ID,
                    "username": self.username,
                    "password": BOT_PASSWORD,
                },
                timeout=10,
            )
            if resp.status_code == 200:
                return resp.json()["access_token"]
            else:
                pass
        except Exception:
            pass
        return None

    @task(5)
    def browse_candidates(self):
        """Most common action — browse potential matches."""
        with self.client.get(
            "/api/matchmaking/candidates",
            headers=self.headers,
            params={"limit": 20},
            name="/api/matchmaking/candidates",
            catch_response=True,
        ) as resp:
            if resp.status_code == 401:
                # Re-auth
                self.token = self._get_token()
                if self.token:
                    self.headers = {"Authorization": f"Bearer {self.token}"}
                resp.failure("401 — re-authed")
            elif resp.status_code == 200:
                resp.success()

    @task(3)
    def swipe(self):
        """Swipe on a candidate."""
        # First get candidates
        with self.client.get(
            "/api/matchmaking/candidates",
            headers=self.headers,
            params={"limit": 5},
            name="/api/matchmaking/candidates [for swipe]",
            catch_response=True,
        ) as resp:
            if resp.status_code != 200:
                return
            try:
                data = resp.json()
                items = data if isinstance(data, list) else data.get("candidates", data.get("items", []))
                if items:
                    target = random.choice(items)
                    target_id = target.get("userId") or target.get("id", "")
                    is_like = random.random() < 0.3
                    self.client.post(
                        "/api/swipes",
                        headers={**self.headers, "Content-Type": "application/json"},
                        data=json.dumps({"targetUserId": str(target_id), "isLike": is_like}),
                        name="/api/swipes",
                    )
            except Exception:
                pass

    @task(2)
    def view_profile(self):
        """View own profile."""
        self.client.get(
            "/api/userprofiles/me",
            headers=self.headers,
            name="/api/userprofiles/me",
        )

    @task(2)
    def check_messages(self):
        """Check conversations."""
        self.client.get(
            "/api/messages/conversations",
            headers=self.headers,
            name="/api/messages/conversations",
        )

    @task(1)
    def view_matches(self):
        """View matches list."""
        self.client.get(
            "/api/swipes/matches",
            headers=self.headers,
            name="/api/swipes/matches",
        )

    @task(1)
    def send_message(self):
        """Send a message to a match if we have any."""
        if not self.my_matches:
            # Try to get matches first
            resp = self.client.get(
                "/api/swipes/matches",
                headers=self.headers,
                name="/api/swipes/matches [for msg]",
            )
            if resp.status_code == 200:
                try:
                    data = resp.json()
                    self.my_matches = data if isinstance(data, list) else data.get("matches", [])
                except Exception:
                    pass

        if self.my_matches:
            match = random.choice(self.my_matches)
            match_id = match.get("matchId") or match.get("id", "")
            if match_id:
                self.client.post(
                    f"/api/messages/conversations/{match_id}/messages",
                    headers={**self.headers, "Content-Type": "application/json"},
                    data=json.dumps({"content": f"Hey! Bot msg {random.randint(1,999)}"}),
                    name="/api/messaging/messages",
                )
