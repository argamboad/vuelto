"""Mint an access token for an email via the OTP API flow + Mailpit. Prints the token to stdout."""
import json, re, ssl, sys, time, urllib.request

API = "https://localhost:7160"
MAIL = "http://localhost:8025"
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE


def req(url, data=None, headers=None, method=None):
    h = {"Content-Type": "application/json", **(headers or {})}
    r = urllib.request.Request(url, data=json.dumps(data).encode() if data is not None else None,
                               headers=h, method=method)
    try:
        with urllib.request.urlopen(r, context=ctx) as resp:
            body = resp.read().decode()
            return resp.status, body
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def latest_id(email):
    _, listing = req(f"{MAIL}/api/v1/search?query=to:%22{email}%22&limit=1")
    msgs = json.loads(listing).get("messages", [])
    return msgs[0]["ID"] if msgs else None


def main(email):
    before = latest_id(email)
    status, body = req(f"{API}/api/auth/otp/send", {"email": email})
    if status >= 400:
        print(f"send failed {status}: {body}", file=sys.stderr)
        sys.exit(1)
    code = None
    for _ in range(20):
        time.sleep(1.5)
        mid = latest_id(email)
        if mid and mid != before:
            _, msg = req(f"{MAIL}/api/v1/message/{mid}")
            m = re.search(r"\b(\d{6})\b", json.loads(msg).get("Text", ""))
            if m:
                code = m.group(1)
                break
    if not code:
        print("no OTP found in Mailpit", file=sys.stderr)
        sys.exit(1)
    status, body = req(f"{API}/api/auth/otp/verify", {"email": email, "code": code})
    if status >= 400:
        print(f"verify failed {status}: {body}", file=sys.stderr)
        sys.exit(1)
    print(json.dumps(json.loads(body)))


if __name__ == "__main__":
    main(sys.argv[1])
