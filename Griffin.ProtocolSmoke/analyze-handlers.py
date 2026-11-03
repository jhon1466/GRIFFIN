"""Lists direct handler overrides for client requests (MsgCs*).

Run from the repository root after a server start that wrote a log containing the "Mapped MsgCs... ->" lines:
    python Griffin.ProtocolSmoke/analyze-handlers.py [server.log]
Writes docs/unhandled-requests.md. This is a static inventory, not a feature-completion audit:
fallback responders, dispatcher-generated replies, obsolete requests, and out-of-scope requests
are not classified by this script. Use docs/feature-status.md for behavioral status.
"""
import glob
import re
import sys

allcs = set(re.findall(r'\b(MsgCs\w+)\b', open('Griffin.Common/Protocol/MsgId.cs', encoding='utf-8-sig').read()))
mapped = set()
if len(sys.argv) > 1:
    mapped = set(re.findall(r'Mapped "?(MsgCs\w+)"? ->', open(sys.argv[1], encoding='utf-8', errors='ignore').read()))

replying, silent = [], []
for path in glob.glob('Griffin.GameServer/Handlers/*.cs'):
    text = open(path, encoding='utf-8-sig').read()
    for m in re.finditer(r'public override void (Handle\w+)\(CS_(\w+) \w+, Connection \w+\)\s*\{', text):
        i, depth = m.end(), 1
        while depth and i < len(text):
            depth += (text[i] == '{') - (text[i] == '}')
            i += 1
        body = text[m.end():i]
        entry = (path.replace('\\', '/').split('/')[-1][:-3], m.group(2))
        (replying if re.search(r'\.Send\w*\(', body) else silent).append(entry)

implemented = {'MsgCs' + name for _, name in replying + silent}
lines = [
    '# Inventario de handlers directos del cliente',
    '',
    'Generado con `Griffin.ProtocolSmoke/analyze-handlers.py` analizando `Griffin.GameServer/Handlers/*.cs`.',
    f'El dispatcher registra {len(mapped)} mensajes. El registro incluye stubs generados que lanzan `NotImplementedException`',
    '(se registra una advertencia y no responden). Este informe solo detecta métodos `Handle*` sobrescritos en archivos de handler.',
    '`PacketDispatcher` y los responders pueden dar respuesta a solicitudes sin override directo. Los mensajes obsoletos o fuera del alcance anunciado tampoco se separan aquí; consulta `docs/feature-status.md` para el estado funcional.',
    '',
    f'- Mensajes `MsgCs*` definidos en `MsgId`: {len(allcs)}.',
    f'- Con override directo: {len(implemented)}; envían respuesta: {len(replying)}; no envían nada: {len(silent)}.',
    f'- Sin override directo detectado: {len(allcs - implemented)} (no equivale a “sin implementar”).',
    '',
    '## Implementados que no envían respuesta',
    '',
    *[f'- `{a}.Handle{b}`' for a, b in sorted(silent)],
    '',
    '## Sin override directo detectado',
    '',
    *[f'- `{x}`' for x in sorted(allcs - implemented)],
    '',
]
open('docs/unhandled-requests.md', 'w', encoding='utf-8').write('\n'.join(lines))
print(f'cs={len(allcs)} dispatcher-registered={len(mapped)} implemented={len(implemented)} replying={len(replying)} silent={len(silent)}')
