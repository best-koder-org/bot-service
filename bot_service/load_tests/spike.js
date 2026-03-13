// k6 spike test — ramp up to high load, then back down
import http from "k6/http";
import { check, sleep } from "k6";

const BASE_URL = __ENV.GATEWAY_URL || "http://localhost:8080";
const KC_URL = __ENV.KEYCLOAK_URL || "http://localhost:8090";

export const options = {
  stages: [
    { duration: "10s", target: 10 },   // warm up
    { duration: "20s", target: 100 },   // ramp to 100 VUs
    { duration: "30s", target: 100 },   // hold at 100
    { duration: "10s", target: 200 },   // spike to 200
    { duration: "20s", target: 200 },   // hold spike
    { duration: "10s", target: 0 },     // ramp down
  ],
  thresholds: {
    http_req_duration: ["p(95)<5000"],
    http_req_failed: ["rate<0.3"],
  },
};

const BOT_USERS = [];
for (let i = 0; i < 50; i++) {
  BOT_USERS.push(`bot_${i}`);
}

function getToken(username) {
  const res = http.post(
    `${KC_URL}/realms/DatingApp/protocol/openid-connect/token`,
    {
      grant_type: "password",
      client_id: "datingapp-backend",
      username: username,
      password: "BotPass123!",
    }
  );
  if (res.status === 200) {
    return JSON.parse(res.body).access_token;
  }
  return null;
}

export default function () {
  const username = BOT_USERS[Math.floor(Math.random() * BOT_USERS.length)];
  const token = getToken(username);

  if (token) {
    const headers = { Authorization: `Bearer ${token}` };

    // Browse candidates
    const candidates = http.get(`${BASE_URL}/api/candidates?limit=10`, { headers });
    check(candidates, { "candidates OK": (r) => r.status < 500 });

    // Swipe on a random candidate
    if (candidates.status === 200) {
      try {
        const list = JSON.parse(candidates.body);
        const items = Array.isArray(list) ? list : list.candidates || [];
        if (items.length > 0) {
          const target = items[Math.floor(Math.random() * items.length)];
          const targetId = target.userId || target.id || target.user_id;
          const direction = Math.random() < 0.3 ? "right" : "left";

          const swipeRes = http.post(
            `${BASE_URL}/api/swipes`,
            JSON.stringify({ targetUserId: targetId, direction: direction }),
            { headers: { ...headers, "Content-Type": "application/json" } }
          );
          check(swipeRes, { "swipe OK": (r) => r.status < 500 });
        }
      } catch (e) {
        // parsing error, skip
      }
    }

    // Get messages
    const msgs = http.get(`${BASE_URL}/api/messages`, { headers });
    check(msgs, { "messages OK": (r) => r.status < 500 });
  }

  sleep(Math.random() * 2);
}
