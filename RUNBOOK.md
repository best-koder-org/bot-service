# Bot Swarm Operations Runbook

> Auto-generated from live service survey on 2026-06-24.

## Quick Start

```bash
# Start bot-service standalone
cd bot-service && dotnet run

# The service auto-loads personas from BotService/Personas/*.json
# It starts in Idle mode — no bots active until you POST /api/swarm/start
```

## API Reference

### Swarm Control — `/api/swarm`

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/swarm/status` | Active mode, bot count, running since |
| `GET` | `/api/swarm/modes` | List available modes |
| `POST` | `/api/swarm/start` | Start swarm: `{ "mode": "onboarding-assist", "botCount": 5, "durationSeconds": 3600 }` |
| `POST` | `/api/swarm/stop` | Stop all active bots, return to Idle |

**Modes:**

| Value | Mode | Use Case |
|-------|------|----------|
| `onboarding-assist` | OnboardingAssistMode | New users get quick matches from bots |
| `retention-boost` | RetentionBoostMode | Re-engage inactive users with bot messages |
| `load-test` | LoadTestMode | Generate configurable RPS against all services |
| `experiment` | ExperimentMode | A/B test conversation strategies |

### Bot Management — `/api/bot`

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/bot/status` | All bots: active/paused count, per-persona state |
| `GET` | `/api/bot/status/{personaId}` | Single bot status |
| `GET` | `/api/bot/personas` | List all loaded personas |
| `POST` | `/api/bot/pause/{personaId}` | Pause one bot |
| `POST` | `/api/bot/resume/{personaId}` | Resume one bot |
| `POST` | `/api/bot/pause-all` | Pause all bots |
| `POST` | `/api/bot/resume-all` | Resume all bots |
| `POST` | `/api/bot/reset-counters` | Reset message/swipe counters |

### Findings & Observability — `/api/findings`

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/findings/summary` | Aggregated dashboard: counts by type, top 5 critical, avg latency, trend |
| `GET` | `/api/findings` | All findings, paginated. Query: `?severity=critical&type=safety` |
| `GET` | `/api/findings/{id}` | Single finding detail |
| `POST` | `/api/findings/{id}/resolve` | Mark finding resolved |
| `GET` | `/api/findings/recent` | Last 50 findings, newest first |
| `GET` | `/api/findings/llm-stats` | Token usage, cost, latency per provider |
| `GET` | `/api/findings/export` | Export all findings as JSON (for CI/integration) |

### Experiments — `/api/bot/experiments`

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/bot/experiments` | Create experiment: `{ "name", "durationMinutes", "groupAConfig", "groupBConfig" }` |
| `GET` | `/api/bot/experiments` | List all experiments |
| `GET` | `/api/bot/experiments/{id}` | Experiment detail |
| `POST` | `/api/bot/experiments/{id}/start` | Start experiment |
| `POST` | `/api/bot/experiments/{id}/complete` | Complete experiment |
| `POST` | `/api/bot/experiments/{id}/cancel` | Cancel experiment |
| `GET` | `/api/bot/experiments/{id}/results` | Experiment results + metrics |

### User Feedback — `/api/userfeedback`

| Method | Path | Description |
|--------|------|-------------|
| `POST` | `/api/userfeedback` | Submit feedback: `{ "botPersonaId", "rating", "freeform" }` |
| `GET` | `/api/userfeedback` | All feedback entries |
| `GET` | `/api/userfeedback/{id}` | Single feedback |
| `GET` | `/api/userfeedback/{id}/audio` | Feedback audio if voice |

## LLM Providers

Configured via `appsettings.json` → `BotServiceOptions.LlmOptions`:

| Provider | Env Var | Model Default | Notes |
|----------|---------|---------------|-------|
| Gemini | `GEMINI_API_KEY` | `gemini-2.5-flash-lite` | Free tier, primary |
| Groq | `GROQ_API_KEY` | `llama-3.3-70b-versatile` | Fallback on 429 |
| Ollama | (localhost) | `qwen3:32b` | Offline/local |

**Circuit breaker:** 3 consecutive failures → fallback to next provider. Budget exceeded → canned Swedish fallback replies.

## Configuration

```json
// BotServiceOptions
{
  "LlmOptions": {
    "PrimaryProvider": "gemini",
    "FallbackProvider": "groq",
    "DailyTokenBudget": 100000,
    "MaxTokensPerMessage": 150,
    "Temperature": 0.8
  },
  "DefaultSwarmMode": "retention-boost",
  "MaxConcurrentBots": 50,
  "MessageIntervalMs": 3000,
  "SafetyEnabled": true
}
```

## Personas

- **Location:** `BotService/Personas/*.json`
- **Count:** 55 personas (expanded from 12)
- **Demographics:** Ages 22–57, cities across Sweden, diverse occupations
- **Format:**
```json
{
  "id": "bot_erik-b",
  "name": "Erik B",
  "age": 34,
  "gender": "male",
  "city": "Göteborg",
  "occupation": "Software Developer",
  "personality": "reserved, thoughtful",
  "chattiness": "medium",
  "relationshipGoals": "serious",
  "modes": ["onboarding-assist", "retention-boost", "load-test", "experiment"],
  "enabled": true
}
```

## Scripts

| Script | Purpose |
|--------|---------|
| `scripts/bot-daily-digest.py` | Daily Markdown report → email/Slack |
| `scripts/bot-dashboard.sh` | Terminal-friendly health overview |

## Troubleshooting

### Bots not responding in Swedish
1. Check LLM provider is reachable: `curl http://localhost:8089/api/findings/llm-stats`
2. If `tokensUsed: 0` → API key missing or invalid. Check `GEMINI_API_KEY` / `GROQ_API_KEY`.
3. If tokens used but non-Swedish output → prompt issue in `LlmConversationEngine`.

### Swarm won't start
1. Check persona loading: `curl http://localhost:8089/api/bot/personas`
2. Must have at least 1 persona in `BotService/Personas/` with matching mode.
3. Check logs for `Swarm start requested` and `No active bots available`.

### High LLM costs
1. Check daily budget: `curl http://localhost:8089/api/findings/llm-stats`
2. Reduce `DailyTokenBudget` in `appsettings.json`.
3. Increase `MessageIntervalMs` to reduce message frequency.
4. Consider running Ollama locally: `ollama pull qwen3:32b && curl http://localhost:11434/v1/chat/completions`

### demo-user message loop
- The `bot_demo-user@bot.local` persona shares identity with the Flutter dev user (ProfileId 1).
- If Active, it creates a rapid self-message loop.
- **Fix:** `curl -X POST http://localhost:8089/api/bot/pause/demo-user`

## Admin Reset

For cleaning up bot-generated test data across all services:

```bash
curl -X POST http://localhost:8080/api/admin/reset-interactions \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json"
```

## Monitoring

- **Health:** `GET /api/bot/status` — active/paused bots, running mode
- **Findings summary:** `GET /api/findings/summary` — errors, safety flags, API latency
- **LLM usage:** `GET /api/findings/llm-stats` — tokens, cost, provider health
- **Swarm mode:** `GET /api/swarm/status`

### Critical thresholds
- `safety_findings/week > 10` → investigate safety-service
- `api_latency_p99 > 2000ms` → check service health
- `llm_failure_rate > 20%` → check API keys/provider status
- `bot_match_rate < 20%` → persona quality may need tuning (T316)

## Tester Demo Mode — bots as realistic fake users

Makes the app feel populated for human testers **without spamming or filling the DBs**.

- **Reactive-only**: bots never proactively swipe random users. They only *like back*
  when a human swipes right on them, so matches are always human-initiated.
- **Onboarding assist**: fresh human signups get pre-likes from `MaxOnboardingBots`
  compatible bots, so their first right-swipes match instantly.
- **Opener**: one opener per match, then strict turn-taking (bots only reply when the
  human sent the last message).
- **Targeted purge**: bot-generated rows carry `IsBotGenerated` and are deleted by the
  bot-purge endpoints. Real user data is NEVER touched.

### API — `/api/demo` (bot-service :8089)

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/demo/status` | Demo mode state + active bots / matches / messages / onboarded testers |
| `POST` | `/api/demo/enable` | Turn on: `{ "enabled": true, "reactiveOnly": true }` |
| `POST` | `/api/demo/disable` | Turn off + purge all bot interactions (PurgeOnStop) |
| `POST` | `/api/demo/purge?olderThanHours=0` | Purge bot data now (optional TTL filter) |

### Targeted purge endpoints (per service + gateway composite)

| Service | Endpoint |
|---------|----------|
| swipe-service | `DELETE /api/admin/bot-swipe-data?olderThanHours=24` |
| messaging-service | `DELETE /api/admin/bot-messages?olderThanHours=24` |
| MatchmakingService | `DELETE /api/admin/bot-match-data?olderThanHours=24` |
| YARP composite | `POST /api/admin/reset-bot-interactions?olderThanHours=24` |

`olderThanHours=0` (default) deletes ALL bot rows; a positive value deletes only rows
older than N hours (keeps a tester's active conversation). All guarded to
Dev/Staging/Demo environments.

### Config — `BotService:Demo` (appsettings.json)

| Key | Default | Meaning |
|-----|---------|---------|
| `Enabled` | true | Master switch for demo mode |
| `ReactiveOnly` | true | No proactive swiping; like-back + onboarding only |
| `MaxOnboardingBots` | 5 | Bots that pre-like a fresh tester |
| `OpenerOnMatch` | true | Send one opener per match, then turn-taking |
| `PurgeTtlHours` | 24 | Auto-purge bot rows older than this (0 = disabled) |
| `PurgeOnStop` | true | Purge all bot data when demo mode is disabled |
| `OnboardingCheckIntervalSec` | 60 | How often to poll for new testers |
| `PreSeedBotCount` / `PreSeedBotIds` | 4 / astrid,linnea,maja,elsa | Bots that pre-like the demo user |
| `PreSeedAutoReciprocate` | true | demo-user auto-swipes back → instant matches |
| `MaxLikeBackPerCycle` | 5 | Like-backs per bot per cycle (bounds DB volume) |

### Dashboard
The dev dashboard (`dev_dashboard.py`, Testers tab → **Tester Demo Mode**) has
Enable/Disable/Purge controls and live bot-data metrics.

