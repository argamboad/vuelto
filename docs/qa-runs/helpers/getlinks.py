import sys,json,re,urllib.request
mid=sys.argv[1]
body=urllib.request.urlopen(f"http://localhost:8025/api/v1/message/{mid}").read().decode()
d=json.loads(body)
text=(d.get('HTML') or '')+' '+(d.get('Text') or '')
links=re.findall(r'https?://[^\s"\'<>]+',text)
for l in dict.fromkeys(links):
    print(l)
