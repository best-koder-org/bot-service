"""
Load Test Runner — generates realistic traffic then checks for bottlenecks.

Workflow:
  1. Runs Locust headless for N seconds with M users
  2. Queries Prometheus for the metrics generated during the test
  3. Runs perf_checker to find bottlenecks
  4. Prints combined report

Usage:
    python -m bot_service.load_test                     # 30s, 10 users
    python -m bot_service.load_test --users 50 --time 60  # 60s, 50 users
    python -m bot_service.load_test --users 200 --time 120 --spawn-rate 20
"""
import argparse, subprocess, sys, os, time, json

VENV_BIN = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), ".venv", "bin")
LOCUST_BIN = os.path.join(VENV_BIN, "locust")
LOCUSTFILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "locust_tests", "locustfile.py")

def run_load_test(users: int, duration: int, spawn_rate: int):
    print(f"\n{'═'*60}")
    print(f"  LOAD TEST — {users} users, {duration}s, spawn-rate {spawn_rate}/s")
    print(f"{'═'*60}\n")

    # Pre-check: are services alive?
    import urllib.request
    for port, name in [(8080, "Gateway"), (8082, "UserService"), (8087, "SwipeService")]:
        try:
            urllib.request.urlopen(f"http://localhost:{port}/health", timeout=3)
            print(f"  ✅ {name}:{port} healthy")
        except Exception:
            print(f"  ❌ {name}:{port} NOT responding — abort!")
            return

    # Run Locust in headless mode
    cmd = [
        LOCUST_BIN,
        "-f", LOCUSTFILE,
        "--headless",
        "--host", "http://localhost:8080",
        "-u", str(users),
        "-r", str(spawn_rate),
        "-t", f"{duration}s",
        "--csv", "/tmp/locust_results",
        "--csv-full-history",
        "--only-summary",
    ]

    print(f"\n  Starting Locust...\n")
    start = time.time()
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=duration + 60)
    elapsed = time.time() - start

    # Parse output
    print(f"  Locust finished in {elapsed:.0f}s\n")

    # Print the summary stats from Locust
    for line in proc.stdout.split("\n"):
        if line.strip() and not line.startswith("["):
            print(f"  {line}")

    if proc.stderr:
        errors = [l for l in proc.stderr.split("\n") if "error" in l.lower() or "fail" in l.lower()]
        if errors:
            print(f"\n  ⚠ Errors during test:")
            for e in errors[:5]:
                print(f"    {e}")

    # Parse CSV results if available
    try:
        with open("/tmp/locust_results_stats.csv") as f:
            import csv
            reader = csv.DictReader(f)
            rows = list(reader)
            print(f"\n  {'─'*55}")
            print(f"  {'Endpoint':40s} {'Avg(ms)':>8s} {'P95(ms)':>8s} {'Fail%':>7s}")
            print(f"  {'─'*55}")
            for row in rows:
                name = row.get("Name", "?")
                avg = row.get("Average Response Time", "?")
                p95 = row.get("95%", "?")
                fails = row.get("Failure Count", "0")
                reqs = row.get("Request Count", "1")
                fail_pct = float(fails) / max(float(reqs), 1) * 100
                print(f"  {name:40s} {avg:>8s} {p95:>8s} {fail_pct:>6.1f}%")
            print(f"  {'─'*55}")
    except FileNotFoundError:
        print("  (no CSV results file)")

    # Wait for Prometheus to scrape
    print(f"\n  Waiting 20s for Prometheus to scrape post-load metrics...")
    time.sleep(20)

    # Run bottleneck checker
    print()
    from bot_service.perf_checker import run_report
    print(run_report())

    # Hardware context reminder
    print(f"\n  ℹ HARDWARE CONTEXT (results are specific to this machine):")
    try:
        import multiprocessing
        cpus = multiprocessing.cpu_count()
        with open("/proc/meminfo") as f:
            for line in f:
                if "MemTotal" in line:
                    mem_gb = int(line.split()[1]) / 1024 / 1024
                    break
        print(f"    CPUs: {cpus}  RAM: {mem_gb:.0f} GB")
        print(f"    On a smaller VPS (2 CPU / 4 GB), expect 2-5x worse numbers")
        print(f"    On production (8+ CPU / 32 GB), expect 2-3x better")
    except Exception:
        pass
    print()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Load test the dating app")
    parser.add_argument("--users", "-u", type=int, default=10, help="Number of simulated users")
    parser.add_argument("--time", "-t", type=int, default=30, help="Test duration in seconds")
    parser.add_argument("--spawn-rate", "-r", type=int, default=5, help="Users spawned per second")
    args = parser.parse_args()
    run_load_test(args.users, args.time, args.spawn_rate)
