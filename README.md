# 🤖 DatingApp Bot Service

Web-based dashboard for seeding test profiles, simulating user behavior, and load testing the DatingApp platform.

## Quick Start

```bash
cd bot-service
python -m bot_service
```

Then open **http://localhost:9091** in your browser.

## Features

### 🌱 Seed Profiles
- Fetches realistic Swedish profiles from [randomuser.me](https://randomuser.me)
- Enriches with Swedish bios, interests, and dating prompts via Faker
- **Local mode**: Generate profiles as JSON (no services needed)
- **Keycloak mode**: Create real users in Keycloak + UserService

### 🎮 Simulate
- Bots swipe, match, and send messages autonomously
- Adjustable speed (0.1x–5.0x)
- Live stats: swipes, matches, messages, errors
- **Dry-run mode**: Simulate without API calls
- **Live mode**: Real API calls to running services

### 📊 Load Testing
- **Locust** (built-in, port 8089): Python-based with web UI
- **k6** (external): JavaScript-based, smoke + spike test scenarios

## Architecture

```
bot_service/
├── __init__.py          # Package version
├── __main__.py          # NiceGUI dashboard (entry point)
├── config.py            # All configuration + curated data pools
├── seeder.py            # Profile generation + Keycloak/UserService APIs
├── simulator.py         # Behavior simulation (swipe/match/message loop)
├── data/
│   └── conversations.json   # 30 Swedish conversation templates
├── load_tests/
│   ├── smoke.js         # k6 smoke test (5 VUs, 30s)
│   └── spike.js         # k6 spike test (10→200 VUs)
└── locust_tests/
    └── locustfile.py    # Locust behavior simulation
```

## Dependencies

- **nicegui** — Web dashboard framework
- **faker** — Swedish profile data generation
- **httpx** — Async HTTP client
- **websockets** — SignalR connection support
- **locust** — Load testing framework

## Configuration

All config is in `bot_service/config.py`. Key environment defaults:

| Setting | Default | Description |
|---------|---------|-------------|
| KEYCLOAK_URL | http://localhost:8090 | Keycloak server |
| GATEWAY_URL | http://localhost:8080 | YARP gateway |
| DASHBOARD_PORT | 9091 | Bot dashboard port |
| DEFAULT_BOT_COUNT | 50 | Bots to seed |
| SWIPE_RIGHT_PROBABILITY | 0.30 | Like rate |

## Swedish Naturalness Benchmark

The benchmark suite (`T366`) scores 100 bot-generated Swedish messages on naturalness, grammar, and persona consistency using an LLM judge. Each axis must average ≥ 3.5 / 5.

**Run the benchmark:**

```bash
# Requires GROQ_API_KEY or GEMINI_API_KEY to be set
export GROQ_API_KEY=your_key_here
dotnet test BotService.Tests --filter Category=Benchmark
```

**Normal CI (no key required):** The benchmark test is automatically skipped when no API key is present, so `dotnet test BotService.Tests` always passes in CI without keys.

**CI with benchmark enabled:**

```yaml
- name: Run benchmark
  env:
    GROQ_API_KEY: ${{ secrets.GROQ_API_KEY }}
  run: dotnet test BotService.Tests --filter Category=Benchmark
```
