"""Shows a protobuf message of the server protos with its nested types resolved.

    python Griffin.ProtocolSmoke/protoshow.py CS_StageStart SC_StageStart [--depth 2]

The generated file (Griffin.Common.Proto/protos/GF2_deobfuscated.proto) uses obfuscated names for most nested
messages; this tool only helps reading them. Field numbers and wire types are what matters on the wire.
"""
import re
import sys

PROTO = 'Griffin.Common.Proto/protos/GF2_deobfuscated.proto'
text = open(PROTO, encoding='utf-8').read()

messages = {}
for m in re.finditer(r'^message (\w+) \{\n(.*?)^\}', text, re.S | re.M):
    messages[m.group(1)] = m.group(2)
enums = {}
for m in re.finditer(r'^enum (\w+) \{\n(.*?)^\}', text, re.S | re.M):
    enums[m.group(1)] = re.findall(r'^\s*(\w+)\s*=\s*(-?\d+);', m.group(2), re.M)

FIELD = re.compile(r'^\s*(?:(repeated|optional)\s+)?(map<[^>]+>|[\w.]+)\s+(\w+)\s*=\s*(\d+)', re.M)


def show(name, depth, indent=0, seen=()):
    pad = '  ' * indent
    body = messages.get(name)
    if body is None:
        print(f'{pad}<{name}: not found>')
        return
    # nested enums inside the message body
    nested = {}
    for em in re.finditer(r'enum (\w+) \{(.*?)\}', body, re.S):
        nested[em.group(1)] = re.findall(r'(\w+)\s*=\s*(-?\d+);', em.group(2))
    print(f'{pad}message {name}')
    for rep, typ, fname, num in FIELD.findall(re.sub(r'enum \w+ \{.*?\}', '', body, flags=re.S)):
        label = f'{rep + " " if rep else ""}{typ}'
        print(f'{pad}  {num:>3} {fname}: {label}')
        base = re.findall(r'[\w.]+', typ)[-1] if typ.startswith('map<') else typ
        if depth > 0 and base in messages and base not in seen:
            show(base, depth - 1, indent + 2, seen + (name,))
    for en, members in nested.items():
        print(f'{pad}  enum {en}: ' + ', '.join(f'{a}={b}' for a, b in members[:16]))


if __name__ == '__main__':
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    depth = 1
    if '--depth' in sys.argv:
        depth = int(sys.argv[sys.argv.index('--depth') + 1])
        args = [a for a in args if a != str(depth)]
    for n in args:
        show(n, depth)
        print()
