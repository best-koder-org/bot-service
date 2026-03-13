// k6 smoke test — light load to verify APIs respond
import http from "k6/http";
import { check, sleep } from "k6";

const BASE_URL = __ENV.GATEWAY_URL || "http://localhost:8080";
const KC_URL = __ENV.KEYCLOAK_URL || "http://localhost:8090";

export const options = {
  vus: 5,
  duration: "30s",
  thresholds: {
    http_req_duration: ["p(95)<2000"],
    http_req_failed: ["rate<0.1"],
  },
};

function getToken(username, password) {
  const res = http.post(
    `${KC_URL}/realms/DatingApp/protocol/openid-connect/token`,
    {
      grant_type: "password",
      client_id: "datingapp-backend",
      username: username,
      password: password || "BotPass123!",
    }
  );
  if (res.status === 200) {
    return JSON.parse(res.body).access_token;
  }
  return null;
}

export default function () {
  // Health checks through gateway
  const healthRes = http.get(`${BASE_URL}/api/health`);
  check(healthRes, {
    "gateway responds": (r) => r.status === 200 || r.status === 404,
  });

  // Try to get a token and fetch profile
  const token = getToken("alice", "Test123!");
  if (token) {
    const headers = { Authorization: `Bearer ${token}` };

    const profileRes = http.get(`${BASE_URL}/api/profile/me`, { headers });
    check(profileRes, {
      "profile responds": (r) => r.status < 500,
    });

    const candidatesRes = http.get(`${BASE_URL}/api/candidates?limit=10`, {
      headers,
    });
    check(candidatesRes, {
      "candidates responds": (r) => r.status < 500,
    });
  }

  sleep(1);
}
