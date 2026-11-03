"""One-line view of request -> response for the pending requests of a domain.

    python Griffin.ProtocolSmoke/msgbrief.py "Dormitorio y lobby 3D"
"""
import re
import sys

text = open('Griffin.Common.Proto/protos/GF2_deobfuscated.proto', encoding='utf-8').read()
msgs = {m.group(1): m.group(2) for m in re.finditer(r'^message (\w+) \{\n(.*?)^\}', text, re.S | re.M)}
FIELD = re.compile(r'^\s*(?:(repeated)\s+)?(map<[^>]+>|[\w.]+)\s+(\w+)\s*=\s*(\d+)', re.M)


def brief(name):
    body = msgs.get(name)
    if body is None:
        return '<no proto>'
    body = re.sub(r'enum \w+ \{.*?\}', '', body, flags=re.S)
    fs = [f"{n}{'[]' if r else ''}:{t.replace('map<','m<')}" for r, t, n, _ in FIELD.findall(body)]
    return '(' + ', '.join(fs[:8]) + (', …' if len(fs) > 8 else '') + ')'


board = open('docs/feature-status.md', encoding='utf-8').read()
dom = sys.argv[1]
section = board[board.index(f'### {dom}'):]
section = section[:section.index('\n\n')] if '\n\n' in section else section
for n in re.findall(r'`(\w+)`', section.split('\n', 1)[1]):
    print(f'{n}: {brief("CS_" + n)} -> {brief("SC_" + n)}')
