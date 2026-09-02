"""MFA helpers: enroll+confirm an account, compute TOTP. Usage: python mfa.py enroll <who>"""
import base64, hashlib, hmac, json, os, ssl, struct, sys, time, urllib.request, urllib.error

SP = os.path.dirname(os.path.abspath(__file__))
API = "https://localhost:7160"
ctx = ssl.create_default_context(); ctx.check_hostname = False; ctx.verify_mode = ssl.CERT_NONE


def token(who):
    with open(f"{SP}/tok-{who}.json") as f:
        return json.load(f)["access_token"]


def req(url, data=None, hdr=None, method=None):
    h = {"Content-Type": "application/json", **(hdr or {})}
    r = urllib.request.Request(url, json.dumps(data).encode() if data is not None else None, h, method=method)
    try:
        with urllib.request.urlopen(r, context=ctx) as x:
            return x.status, x.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def totp(secret, t=None):
    key = base64.b32decode(secret + "=" * (-len(secret) % 8), casefold=True)
    counter = int((t or time.time()) // 30)
    mac = hmac.new(key, struct.pack(">Q", counter), hashlib.sha1).digest()
    off = mac[-1] & 0x0F
    code = (struct.unpack(">I", mac[off:off + 4])[0] & 0x7FFFFFFF) % 1_000_000
    return f"{code:06d}"


def enroll(who):
    auth = {"Authorization": f"Bearer {token(who)}"}
    s, b = req(f"{API}/api/auth/mfa/enroll", {}, auth)
    secret = json.loads(b)["secret"]
    s, b = req(f"{API}/api/auth/mfa/confirm", {"code": totp(secret)}, auth)
    if s >= 400:
        print(f"confirm failed {s}: {b}", file=sys.stderr); sys.exit(1)
    print(json.dumps({"secret": secret, "recovery_codes": json.loads(b).get("recovery_codes", [])}))


if __name__ == "__main__":
    if sys.argv[1] == "enroll":
        enroll(sys.argv[2])
    elif sys.argv[1] == "totp":
        print(totp(sys.argv[2]))
