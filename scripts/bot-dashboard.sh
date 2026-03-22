#!/usr/bin/env bash
# Bot Swarm Health Dashboard
# Queries bot-service API endpoints and formats a terminal-friendly overview.
# Usage: bash scripts/bot-dashboard.sh [BASE_URL]
set -euo pipefail

BASE_URL="${1:-http://localhost:8089}"
BOLD='\033[1m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
RED='\033[0;31m'
CYAN='\033[0;36m'
NC='\033[0m'

header() { echo -e "\n${BOLD}${CYAN}=== $1 ===${NC}"; }
ok()     { echo -e "  ${GREEN}OK${NC} $1"; }
warn()   { echo -e "  ${YELLOW}WARN${NC} $1"; }
err()    { echo -e "  ${RED}ERR${NC} $1"; }

# Check service is reachable
if ! curl -sf "${BASE_URL}/api/bot/status" > /dev/null 2>&1; then
    err "Bot service not reachable at ${BASE_URL}"
    exit 1
fi

header "BOT SWARM STATUS"
STATUS=$(curl -sf "${BASE_URL}/api/bot/status" 2>/dev/null || echo '{"error":"unavailable"}')
if echo "$STATUS" | jq -e '.error' > /dev/null 2>&1; then
    err "Status endpoint unavailable"
else
    TOTAL=$(echo "$STATUS" | jq -r '.totalBots // 0')
    ACTIVE=$(echo "$STATUS" | jq -r '.activeBots // 0')
    echo -e "  Total bots:  ${BOLD}${TOTAL}${NC}"
    echo -e "  Active bots: ${BOLD}${GREEN}${ACTIVE}${NC}"
    if [ "$ACTIVE" -eq 0 ] 2>/dev/null; then
        warn "No active bots"
    fi
fi

header "RECENT FINDINGS"
FINDINGS=$(curl -sf "${BASE_URL}/api/bot/findings/summary" 2>/dev/null || echo '{"error":"unavailable"}')
if echo "$FINDINGS" | jq -e '.error' > /dev/null 2>&1; then
    warn "Findings endpoint unavailable"
else
    TOTAL_F=$(echo "$FINDINGS" | jq -r '.totalFindings // 0')
    HIGH=$(echo "$FINDINGS" | jq -r '.highSeverity // 0')
    MEDIUM=$(echo "$FINDINGS" | jq -r '.mediumSeverity // 0')
    LOW=$(echo "$FINDINGS" | jq -r '.lowSeverity // 0')
    echo -e "  Total:  ${BOLD}${TOTAL_F}${NC}"
    if [ "$HIGH" -gt 0 ] 2>/dev/null; then
        err "High severity: ${HIGH}"
    else
        ok "High severity: 0"
    fi
    if [ "$MEDIUM" -gt 0 ] 2>/dev/null; then
        warn "Medium severity: ${MEDIUM}"
    else
        ok "Medium severity: 0"
    fi
    ok "Low severity: ${LOW}"
fi

header "EXPERIMENTS"
EXPERIMENTS=$(curl -sf "${BASE_URL}/api/bot/experiments?pageSize=5" 2>/dev/null || echo '{"error":"unavailable"}')
if echo "$EXPERIMENTS" | jq -e '.error' > /dev/null 2>&1; then
    warn "Experiments endpoint unavailable"
else
    EXP_TOTAL=$(echo "$EXPERIMENTS" | jq -r '.total // 0')
    echo -e "  Total experiments: ${BOLD}${EXP_TOTAL}${NC}"
    echo "$EXPERIMENTS" | jq -r '.experiments[]? | "  [\(.status)] \(.name)"' 2>/dev/null || true
fi

header "SWARM MODES"
echo "  Available: synthetic, onboarding, retention, loadtest, experiment"

echo -e "\nDashboard generated at $(date -u '+%Y-%m-%d %H:%M:%S UTC')"
