"""
Performance Bottleneck Finder — queries live Prometheus metrics
and system state to surface real issues.

Usage:
    python -m bot_service.perf_checker          # one-shot report
    python -m bot_service.perf_checker --watch   # continuous (every 30s)
"""
import json, time, sys, subprocess, os, urllib.request, urllib.error
from datetime import datetime

PROMETHEUS = os.environ.get("PROMETHEUS_URL", "http://localhost:9090")
SERVICES = {
    "yarp-gateway":       {"port": 8080, "name": "YARP Gateway"},
    "user-service":       {"port": 8082, "name": "UserService"},
    "matchmaking-service":{"port": 8083, "name": "MatchmakingService"},
    "photo-service":      {"port": 8085, "name": "PhotoService"},
    "messaging-service":  {"port": 8086, "name": "MessagingService"},
    "swipe-service":      {"port": 8087, "name": "SwipeService"},
}

# ── Thresholds ──────────────────────────────────────────────
P95_WARN_MS    = 200     # > 200ms P95 = warning
P95_CRIT_MS    = 1000    # > 1s P95 = critical
ERROR_RATE_WARN = 0.01   # > 1% 5xx
ERROR_RATE_CRIT = 0.05   # > 5% 5xx
MEMORY_WARN_MB  = 500    # per-service
MEMORY_CRIT_MB  = 1000
CONN_WARN       = 50     # active connections

def prom_query(query: str) -> list:
    """Query Prometheus instant API."""
    try:
        url = f"{PROMETHEUS}/api/v1/query"
        data = urllib.parse.urlencode({"query": query}).encode()
        req = urllib.request.Request(url, data=data)
        with urllib.request.urlopen(req, timeout=5) as resp:
            body = json.loads(resp.read())
            if body.get("status") == "success":
                return body["data"]["result"]
    except Exception as e:
        pass
    return []

def prom_query_range(query: str, duration: str = "5m") -> list:
    """Query with a time range."""
    return prom_query(query)


# ── Checks ──────────────────────────────────────────────────

def check_service_health() -> list[dict]:
    """Check which services Prometheus can reach."""
    issues = []
    results = prom_query("up")
    up_jobs = {r["metric"].get("job"): int(float(r["value"][1])) for r in results}
    for job, info in SERVICES.items():
        status = up_jobs.get(job)
        if status is None:
            issues.append({"sev": "CRIT", "service": info["name"],
                           "msg": f"Not scraped by Prometheus (job={job} missing)"})
        elif status == 0:
            issues.append({"sev": "CRIT", "service": info["name"],
                           "msg": f"DOWN — Prometheus scrape failing"})
    return issues

def check_p95_latency() -> list[dict]:
    """Find endpoints with high P95 latency."""
    issues = []
    results = prom_query(
        'histogram_quantile(0.95, sum by (job, http_route, le) '
        '(rate(http_server_request_duration_seconds_bucket[5m])))'
    )
    for r in results:
        val_s = float(r["value"][1])
        if val_s != val_s:  # NaN
            continue
        val_ms = val_s * 1000
        job = r["metric"].get("job", "?")
        route = r["metric"].get("http_route", "?")
        svc = SERVICES.get(job, {}).get("name", job)
        if val_ms > P95_CRIT_MS:
            issues.append({"sev": "CRIT", "service": svc,
                           "msg": f"P95 = {val_ms:.0f}ms on {route} (threshold: {P95_CRIT_MS}ms)"})
        elif val_ms > P95_WARN_MS:
            issues.append({"sev": "WARN", "service": svc,
                           "msg": f"P95 = {val_ms:.0f}ms on {route} (threshold: {P95_WARN_MS}ms)"})
    return issues

def check_error_rates() -> list[dict]:
    """Find services with high 5xx error rates."""
    issues = []
    for job, info in SERVICES.items():
        total = prom_query(f'sum(rate(http_server_request_duration_seconds_count{{job="{job}"}}[5m]))')
        errors = prom_query(f'sum(rate(http_server_request_duration_seconds_count{{job="{job}",http_response_status_code=~"5.."}}[5m]))')
        if total and errors:
            t = float(total[0]["value"][1])
            e = float(errors[0]["value"][1])
            if t > 0:
                rate = e / t
                if rate > ERROR_RATE_CRIT:
                    issues.append({"sev": "CRIT", "service": info["name"],
                                   "msg": f"5xx error rate = {rate*100:.1f}% ({e:.1f}/{t:.1f} req/s)"})
                elif rate > ERROR_RATE_WARN:
                    issues.append({"sev": "WARN", "service": info["name"],
                                   "msg": f"5xx error rate = {rate*100:.1f}%"})
    return issues

def check_request_rates() -> list[dict]:
    """Show current request rates and find hot endpoints."""
    issues = []
    results = prom_query(
        'sum by (job, http_route, http_request_method) '
        '(rate(http_server_request_duration_seconds_count[5m]))'
    )
    hot = []
    for r in results:
        rps = float(r["value"][1])
        if rps > 0.01:
            job = r["metric"].get("job", "?")
            route = r["metric"].get("http_route", "?")
            method = r["metric"].get("http_request_method", "?")
            svc = SERVICES.get(job, {}).get("name", job)
            hot.append((svc, method, route, rps))
    hot.sort(key=lambda x: -x[3])
    for svc, method, route, rps in hot[:5]:
        issues.append({"sev": "INFO", "service": svc,
                       "msg": f"HOT: {method} {route} → {rps:.1f} req/s"})
    return issues

def check_connections() -> list[dict]:
    """Check active connection counts (Kestrel)."""
    issues = []
    results = prom_query('sum by (job) (kestrel_active_connections)')
    for r in results:
        job = r["metric"].get("job", "?")
        conns = float(r["value"][1])
        svc = SERVICES.get(job, {}).get("name", job)
        if conns > CONN_WARN:
            issues.append({"sev": "WARN", "service": svc,
                           "msg": f"{conns:.0f} active connections (threshold: {CONN_WARN})"})
    return issues

def check_rate_limiting() -> list[dict]:
    """Check if rate limiting is rejecting requests."""
    issues = []
    results = prom_query(
        'sum by (job) (rate(aspnetcore_rate_limiting_requests_total{result="rejected"}[5m]))'
    )
    for r in results:
        rps = float(r["value"][1])
        job = r["metric"].get("job", "?")
        svc = SERVICES.get(job, {}).get("name", job)
        if rps > 0:
            issues.append({"sev": "WARN", "service": svc,
                           "msg": f"Rate limiter rejecting {rps:.1f} req/s"})
    return issues

def check_process_memory() -> list[dict]:
    """Check .NET process memory from /proc or ps."""
    issues = []
    try:
        out = subprocess.check_output(
            "ps aux --sort=-%mem | grep -E 'dotnet|Service|dejting-yarp' | grep -v grep",
            shell=True, text=True, timeout=5
        )
        for line in out.strip().split("\n"):
            parts = line.split()
            if len(parts) < 11:
                continue
            rss_kb = int(parts[5])
            rss_mb = rss_kb / 1024
            cmd = " ".join(parts[10:])
            # Extract service name
            name = cmd.split("/")[-1][:30]
            if rss_mb > MEMORY_CRIT_MB:
                issues.append({"sev": "CRIT", "service": name,
                               "msg": f"Memory = {rss_mb:.0f} MB (threshold: {MEMORY_CRIT_MB} MB)"})
            elif rss_mb > MEMORY_WARN_MB:
                issues.append({"sev": "WARN", "service": name,
                               "msg": f"Memory = {rss_mb:.0f} MB (threshold: {MEMORY_WARN_MB} MB)"})
    except Exception:
        pass
    return issues

def check_db_connections() -> list[dict]:
    """Check MySQL connection count."""
    issues = []
    try:
        out = subprocess.check_output(
            "mysql -h 127.0.0.1 -P 3310 -u root -proot_password -e 'SHOW STATUS LIKE \"Threads_connected\";' 2>/dev/null",
            shell=True, text=True, timeout=5
        )
        for line in out.strip().split("\n"):
            if "Threads_connected" in line:
                count = int(line.split()[-1])
                if count > 50:
                    issues.append({"sev": "WARN", "service": "MySQL:3310",
                                   "msg": f"{count} active DB connections"})
    except Exception:
        pass
    try:
        out = subprocess.check_output(
            "mysql -h 127.0.0.1 -P 3309 -u root -proot_password -e 'SHOW STATUS LIKE \"Threads_connected\";' 2>/dev/null",
            shell=True, text=True, timeout=5
        )
        for line in out.strip().split("\n"):
            if "Threads_connected" in line:
                count = int(line.split()[-1])
                if count > 50:
                    issues.append({"sev": "WARN", "service": "MySQL:3309",
                                   "msg": f"{count} active DB connections"})
    except Exception:
        pass
    return issues

def check_slow_queries() -> list[dict]:
    """Find endpoints where avg latency > 100ms."""
    issues = []
    results = prom_query(
        'sum by (job, http_route, http_request_method) '
        '(rate(http_server_request_duration_seconds_sum[5m])) / '
        'sum by (job, http_route, http_request_method) '
        '(rate(http_server_request_duration_seconds_count[5m]))'
    )
    for r in results:
        val = float(r["value"][1])
        if val != val:  # NaN
            continue
        if val > 0.1:  # > 100ms avg
            job = r["metric"].get("job", "?")
            route = r["metric"].get("http_route", "?")
            method = r["metric"].get("http_request_method", "?")
            svc = SERVICES.get(job, {}).get("name", job)
            issues.append({"sev": "WARN", "service": svc,
                           "msg": f"SLOW AVG: {method} {route} → {val*1000:.0f}ms"})
    return issues

def check_http_client_latency() -> list[dict]:
    """Check outbound HTTP calls (service-to-service)."""
    issues = []
    results = prom_query(
        'histogram_quantile(0.95, sum by (job, le) '
        '(rate(http_client_request_duration_seconds_bucket[5m])))'
    )
    for r in results:
        val = float(r["value"][1])
        if val != val:
            continue
        job = r["metric"].get("job", "?")
        svc = SERVICES.get(job, {}).get("name", job)
        if val > 0.5:  # > 500ms outbound P95
            issues.append({"sev": "WARN", "service": svc,
                           "msg": f"Outbound HTTP P95 = {val*1000:.0f}ms (service-to-service bottleneck?)"})
    return issues


# ── Report ──────────────────────────────────────────────────

ALL_CHECKS = [
    ("Service Health",        check_service_health),
    ("P95 Latency",           check_p95_latency),
    ("Slow Endpoints (avg)",  check_slow_queries),
    ("Error Rates (5xx)",     check_error_rates),
    ("Rate Limiting",         check_rate_limiting),
    ("Active Connections",    check_connections),
    ("Outbound HTTP (svc→svc)", check_http_client_latency),
    ("Process Memory",        check_process_memory),
    ("Database Connections",  check_db_connections),
    ("Hot Endpoints",         check_request_rates),
]

SEV_EMOJI = {"CRIT": "🔴", "WARN": "🟡", "INFO": "🔵"}

def run_report() -> str:
    """Run all checks, return formatted report."""
    lines = []
    lines.append(f"{'═'*60}")
    lines.append(f"  BOTTLENECK REPORT — {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}")
    lines.append(f"{'═'*60}")

    all_issues = []
    for section_name, check_fn in ALL_CHECKS:
        issues = check_fn()
        all_issues.extend(issues)
        if issues:
            lines.append(f"\n┌─ {section_name} ─")
            for iss in issues:
                emoji = SEV_EMOJI.get(iss["sev"], "⚪")
                lines.append(f"│ {emoji} [{iss['sev']:4s}] {iss['service']:25s} {iss['msg']}")
            lines.append(f"└─")

    crits = sum(1 for i in all_issues if i["sev"] == "CRIT")
    warns = sum(1 for i in all_issues if i["sev"] == "WARN")
    infos = sum(1 for i in all_issues if i["sev"] == "INFO")

    lines.append(f"\n{'─'*60}")
    if crits > 0:
        lines.append(f"  🔴 {crits} CRITICAL  🟡 {warns} WARNING  🔵 {infos} INFO")
        lines.append(f"  VERDICT: ISSUES FOUND — check CRITs above")
    elif warns > 0:
        lines.append(f"  🟡 {warns} WARNING  🔵 {infos} INFO")
        lines.append(f"  VERDICT: Mostly healthy — review warnings")
    else:
        lines.append(f"  ✅ ALL CLEAR — no bottlenecks detected")
        if infos:
            lines.append(f"  🔵 {infos} informational items above")
    lines.append(f"{'─'*60}")

    return "\n".join(lines)


if __name__ == "__main__":
    watch = "--watch" in sys.argv
    while True:
        report = run_report()
        if watch:
            os.system("clear")
        print(report)
        if not watch:
            break
        time.sleep(30)
