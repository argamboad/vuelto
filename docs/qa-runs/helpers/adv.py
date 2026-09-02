"""QA-ADV probe CLI: python adv.py <who:a|b|staff|tok:<raw>|anon> <METHOD> <path> [json-body]"""
import json, os, ssl, sys, urllib.request, urllib.error

SP = os.path.dirname(os.path.abspath(__file__))
API = "https://localhost:7160"
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE


def token(who):
    if who == "anon":
        return None
    if who.startswith("tok:"):
        return who[4:]
    with open(f"{SP}/tok-{who}.json") as f:
        return json.load(f)["access_token"]


def main():
    who, method, path = sys.argv[1], sys.argv[2], sys.argv[3]
    body = sys.argv[4] if len(sys.argv) > 4 else None
    headers = {"Content-Type": "application/json"}
    t = token(who)
    if t:
        headers["Authorization"] = f"Bearer {t}"
    r = urllib.request.Request(API + path, data=body.encode() if body else None,
                               headers=headers, method=method)
    try:
        with urllib.request.urlopen(r, context=ctx) as resp:
            print(resp.status)
            print(resp.read().decode())
    except urllib.error.HTTPError as e:
        print(e.code)
        print(e.read().decode())


if __name__ == "__main__":
    main()
