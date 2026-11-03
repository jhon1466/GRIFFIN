"""Cross-reference the checked-in request inventory with IDs extracted from the installed client.

This is an inventory aid, not a functional-completeness claim. It deliberately does not
modify Claude's generated feature-status.md or live log.
Run from repository root: python Griffin.ProtocolSmoke/codex-request-audit.py
"""
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
msgid_path = ROOT / "Griffin.Common/Protocol/MsgId.cs"
client_ids_path = ROOT / "Griffin.ProtocolSmoke/capture/client-msgids.tsv"
proto_path = ROOT / "Griffin.Common.Proto/protos/GF2_deobfuscated.proto"
dispatcher_path = ROOT / "Griffin.Common/Packets/PacketDispatcher.cs"
handlers_dir = ROOT / "Griffin.GameServer/Handlers"
output_path = ROOT / "docs/evidence/codex-request-audit.md"

msgid_text = msgid_path.read_text(encoding="utf-8-sig")
client_text = client_ids_path.read_text(encoding="utf-8-sig")
proto_text = proto_path.read_text(encoding="utf-8-sig")
dispatcher_text = dispatcher_path.read_text(encoding="utf-8-sig")

def enum_values(source: str) -> dict[str, int]:
    body = source[source.index("public enum MsgId"):]
    values: dict[str, int] = {}
    current = -1
    for name, explicit in re.findall(r"\b(Msg\w+)\s*(?:=\s*(\d+))?\s*,", body):
        current = int(explicit) if explicit else current + 1
        values[name] = current
    return values

base_values = enum_values(msgid_text)
client_values = {}
for name, numeric in re.findall(r"^(Msg\w+)\s+(\d+)\s*$", client_text, re.M):
    client_values[name] = int(numeric)
base_requests = {name for name in base_values if name.startswith("MsgCs")}
client_requests = {name for name in client_values if name.startswith("MsgCs")}
proto_requests = set(re.findall(r"^message CS_(\w+)\s*\{", proto_text, re.M))
proto_requests = {"MsgCs" + name for name in proto_requests}
overrides = set()
for path in handlers_dir.glob("*.cs"):
    source = path.read_text(encoding="utf-8-sig")
    overrides.update("MsgCs" + name for name in re.findall(
        r"public override void Handle\w+\(CS_(\w+)\s+\w+,\s*Connection\s+\w+\)", source
    ))
pref_source = (ROOT / "Griffin.GameServer/Services/PreferenceResponder.cs").read_text(encoding="utf-8-sig")
pref_block = pref_source[pref_source.index("Names ="):pref_source.index("];", pref_source.index("Names ="))]
preferences = {"MsgCs" + name for name in re.findall(r'"(\w+)"', pref_block)}

ro_match = re.search(r'ReadOnlyName\s*=\s*new\(\s*@"([^"]*)"', dispatcher_text)
sc_match = re.search(r'StateChangingName\s*=\s*new\(\s*@"([^"]*)"', dispatcher_text)
readonly = re.compile(ro_match.group(1), re.I) if ro_match else re.compile(r"(?!)")
state_change = re.compile(sc_match.group(1), re.I) if sc_match else re.compile(r"(?!)")
empty_sc = set(re.findall(r"^message (SC_\w+)\s*\{\s*\}", proto_text, re.M))

def classify(name: str) -> str:
    suffix = name[5:]
    if name in overrides:
        return "handler override (review semantics)"
    if name in preferences:
        return "persisted preference responder"
    if "SC_" + suffix in empty_sc and not re.search(r"Reward|Claim|Buy|Purchase|Exchange|CheckIn|Gacha|Consume|Compose|Dismantle|Sell|Take|Receive|Pick|Settle|Raid|Pay|Finish|Start|Battle", name, re.I):
        return "empty ack; no state"
    if readonly.search(suffix) and not state_change.search(suffix) and "MsgSc" + suffix in re.findall(r"\b(MsgSc\w+)\b", msgid_text):
        return "automatic empty read response"
    return "no behavior"

current = sorted(base_requests)
new_client = sorted(client_requests - base_requests)
new_client_ids = sorted((name, client_values[name]) for name in new_client)
id_mismatches = sorted((name, base_values[name], client_values[name]) for name in base_requests & client_requests
                       if name in client_values and base_values.get(name) != client_values[name])
missing_proto = sorted(client_requests - proto_requests)
known_unhandled = sorted(name for name in current if classify(name) in {"no behavior", "automatic empty read response", "empty ack; no state"})
rows = [
    "# Cruce de solicitudes del cliente y el servidor (Codex)", "",
    "Generado por `Griffin.ProtocolSmoke/codex-request-audit.py`. El estado se clasifica por forma del código y no certifica fidelidad funcional. Los handlers propios requieren revisión de semántica, tablas, persistencia y prueba del flujo.", "",
    f"- IDs de solicitud base (`MsgId.cs`): **{len(base_requests)}**.",
    f"- Solicitudes del enum extraído del cliente: **{len(client_requests)}**.",
    f"- Solicitudes del cliente ausentes de `MsgId.cs` por nombre: **{len(new_client)}**.",
    f"- IDs de solicitudes con el mismo nombre pero distinto número: **{len(id_mismatches)}**.",
    f"- Solicitudes del cliente sin definición `CS_*` en el proto local: **{len(missing_proto)}**.",
    f"- Solicitudes base clasificadas sin override propio o solo con fallback: **{len(known_unhandled)}**.",
    "",
    "## Interpretación",
    "",
    "`handler override` solo significa que existe un override; puede ser un stub, eco, captura o implementación completa. `empty ack` y `automatic empty read` son respuestas de compatibilidad y no cuentan como función completada. Los mensajes nuevos sin proto requieren reverse engineering del protocolo antes de una implementación segura; los IDs `Darkov` no se consideran alcanzables mientras el login no anuncie ese modo. El corte `MsgId.cs` frente a la lista del cliente mide 76 nombres `CS` ausentes de la base, mientras la diferencia total de mensajes `CS`+`SC` documentada por Claude es 163.",
    "",
    "## Solicitudes del cliente ausentes de MsgId.cs",
    "",
    *(f"- `{name}` ({number}) — {classify(name)}" for name, number in new_client_ids),
    "",
    "## Solicitudes base con respuesta técnica pero sin comportamiento de dominio",
    "",
    *(f"- `{name}` — {classify(name)}" for name in known_unhandled),
    "",
    "## Límites de versión",
    "",
    "El proto base se generó para una revisión anterior a la lista de IDs del cliente local 4.0.5136. El cruce de nombres/IDs no incluye estructuras de paquetes nuevas, ni prueba si el cliente llega a emitirlas. Consultar `docs/evidence/client-msgid-delta.txt` para el delta extraído y `docs/claude-live-log.md` para el reparto y estado de los trabajos concurrentes.",
    "",
    "## IDs que cambiaron de número",
    "",
    *(f"- `{name}`: servidor {base_id}, cliente {client_id}" for name, base_id, client_id in id_mismatches),
]
output_path.write_text("\n".join(rows) + "\n", encoding="utf-8")
print(f"base={len(base_requests)} client={len(client_requests)} new={len(new_client)} id_mismatches={len(id_mismatches)} missing_proto={len(missing_proto)} behaviorless={len(known_unhandled)}")
