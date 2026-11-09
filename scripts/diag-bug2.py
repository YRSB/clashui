"""bug2 feedback loop: core alive but all nodes timeout (post sleep/wake degradation).

Usage: python scripts/diag-bug2.py
Prints checks + VERDICT=GREEN|RED <reason>. Exit 0 green, 1 red.
Secret is read from settings.json and never printed.
"""
import concurrent.futures as cf
import json
import sys
import time
import urllib.parse
import urllib.request

SETTINGS = r"C:\Users\nobody\AppData\Local\ClashUI\settings.json"
PROBE_URL = "https://www.gstatic.com/generate_204"


def load():
    with open(SETTINGS, encoding="utf-8") as f:
        s = json.load(f)
    return s["ControllerAddr"], s["Secret"]


def get(addr, secret, path, timeout):
    req = urllib.request.Request(
        f"http://{addr}{path}", headers={"Authorization": "Bearer " + secret})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.status, r.read()


def main():
    addr, secret = load()
    t0 = time.time()
    try:
        st, body = get(addr, secret, "/version", 5)
        print(f"core-version: {st} {body[:60]!r}")
        core_ok = st == 200
    except Exception as e:
        print(f"core-version: FAIL {type(e).__name__} {e}")
        core_ok = False
    if not core_ok:
        print(f"VERDICT=RED core-down elapsed={time.time() - t0:.1f}s")
        return 1
    try:
        _, body = get(addr, secret, "/proxies", 8)
        names = [n for n in json.loads(body).get("proxies", {})
                 if n not in ("DIRECT", "REJECT")]
    except Exception as e:
        print(f"proxies: FAIL {type(e).__name__} {e}")
        print(f"VERDICT=RED core-api-broken elapsed={time.time() - t0:.1f}s")
        return 1
    targets = []
    for cand in names:
        u = cand.upper()
        if "PROXY" in u or "AUTO" in u or "SELECT" in u:
            targets.append(cand)
            break
    for n in names:
        if n not in targets:
            targets.append(n)
        if len(targets) >= 3:
            break
    print(f"delay-targets: {targets}")
    q = urllib.parse.urlencode({"timeout": 5000, "url": PROBE_URL})

    def probe(name):
        s0 = time.time()
        try:
            st, b = get(addr, secret,
                        "/proxies/" + urllib.parse.quote(name) + "/delay?" + q, 9)
            return name, True, repr(b[:40]), time.time() - s0
        except Exception as e:
            return name, False, f"{type(e).__name__} {e}"[:80], time.time() - s0

    results = []
    with cf.ThreadPoolExecutor(max_workers=3) as ex:
        for r in ex.map(probe, targets):
            results.append(r)
            detail = ("OK " + r[2]) if r[1] else ("FAIL " + r[2])
            print(f"delay {r[0]}: {detail} ({r[3]:.1f}s)")
    ok = sum(1 for r in results if r[1])
    try:
        req = urllib.request.Request(PROBE_URL)
        with urllib.request.urlopen(req, timeout=10) as r:
            tun_ok, tun_st = True, str(r.status)
    except Exception as e:
        tun_ok, tun_st = False, type(e).__name__
    print(f"tun-direct: {('OK ' + tun_st) if tun_ok else ('FAIL ' + tun_st)}")
    el = time.time() - t0
    if ok == 0:
        print(f"VERDICT=RED nodes-timeout 0/{len(results)} elapsed={el:.1f}s")
        return 1
    if not tun_ok:
        print(f"VERDICT=RED tun-path-broken delays-ok={ok}/{len(results)} elapsed={el:.1f}s")
        return 1
    print(f"VERDICT=GREEN delays-ok={ok}/{len(results)} elapsed={el:.1f}s")
    return 0


sys.exit(main())
