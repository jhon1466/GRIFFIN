"""Generates docs/feature-status.md: every client request (MsgCs*) classified per game domain.

    python Griffin.ProtocolSmoke/gen-feature-status.py

Status of a request:
  implemented  a handler class overrides it (real behaviour or captured response, see the handler)
  preference   answered by PreferenceResponder (stored per account and echoed)
  read-empty   not implemented but a plain read-only query: the dispatcher answers with the empty counterpart message
  none         not implemented and not a plain read-only query: answered with SC_ReportError UserSystemLocked (-10018)
  out-of-scope reachable only through content the server never announces (expired events, mode "Darkov")
Edit OUT_OF_SCOPE / DOMAINS below when a decision changes; the document is regenerated, never edited by hand.
"""
import glob
import re

ROOT = '.'
msgid = open(f'{ROOT}/Griffin.Common/Protocol/MsgId.cs', encoding='utf-8-sig').read()
all_cs = sorted(set(re.findall(r'\b(MsgCs\w+)\b', msgid)))

implemented = set()
refused = set()  # handlers whose whole body is connection.Refuse(...): answered with a specific client error, no function behind
for path in glob.glob(f'{ROOT}/Griffin.GameServer/Handlers/*.cs'):
    t = open(path, encoding='utf-8-sig').read()
    for m in re.finditer(r'public override void Handle\w+\(CS_(\w+) \w+, Connection \w+\)(\s*=>\s*connection\.Refuse\()?', t):
        (refused if m.group(2) else implemented).add('MsgCs' + m.group(1))

pref_src = open(f'{ROOT}/Griffin.GameServer/Services/PreferenceResponder.cs', encoding='utf-8-sig').read()
block = pref_src[pref_src.index('Names ='):pref_src.index('];', pref_src.index('Names ='))]
preferences = {'MsgCs' + n for n in re.findall(r'"(\w+)"', block)}

query_src = open(f'{ROOT}/Griffin.GameServer/Services/QueryResponder.cs', encoding='utf-8-sig').read()
qblock = query_src[query_src.index('Names ='):query_src.index('];', query_src.index('Names ='))]
queries = {'MsgCs' + n for n in re.findall(r'"(\w+)"', qblock)}

disp = open(f'{ROOT}/Griffin.Common/Packets/PacketDispatcher.cs', encoding='utf-8-sig').read()
ro = re.search(r'ReadOnlyName = new\(\s*@"([^"]*)"', disp).group(1)
sc = re.search(r'StateChangingName = new\(\s*@"([^"]*)"', disp).group(1)
RO, SC = re.compile(ro, re.I), re.compile(sc)
paired = set(re.findall(r'\b(MsgSc\w+)\b', msgid))
proto = open(f'{ROOT}/Griffin.Common.Proto/protos/GF2_deobfuscated.proto', encoding='utf-8').read()
EMPTY_SC = set(re.findall(r'^message (SC_\w+) \{\s*\}', proto, re.M))
DEPRECATED = set(re.findall(r'^message ((?:CS|SC)_\w+) \{\s*option deprecated = true;', proto, re.M))
MSGS = {m.group(1): m.group(2) for m in re.finditer(r'^message (\w+) \{\n(.*?)^\}', proto, re.S | re.M)}
_FIELD = re.compile(r'^\s*(?:(repeated)\s+)?(map<[^>]+>|[\w.]+)\s+(\w+)\s*=\s*(\d+)', re.M)
ECHO_EXCLUDE = re.compile('Reward|Claim|Buy|Purchase|Exchange|CheckIn|Lottery|Gacha|Draw|Consume|Compose|Dismantle|Decompose|Sell|Recycle|Withdraw|Take|Gain|Receive|Pick|Settle|Raid|Pay|Finish|Start|Fight|Battle|Rank|Upgrade|LvUp|LevelUp|Cook|Water|Dig|Invite|Apply|Join|Leave|Kick|Disband|Create|Redeem|Dispatch')


def _fields(name):
    body = re.sub(r'enum \w+ \{.*?\}', '', MSGS.get(name, ''), flags=re.S)
    return {(bool(r), t, n) for r, t, n, _ in _FIELD.findall(body)}


def is_echo(suffix):
    if ECHO_EXCLUDE.search(suffix) or 'CS_' + suffix not in MSGS or 'SC_' + suffix not in MSGS:
        return False
    sc, cs = _fields('SC_' + suffix), _fields('CS_' + suffix)
    return bool(sc) and sc <= cs


ACK_EXCLUDE = re.compile('Reward|Claim|Buy|Purchase|Exchange|CheckIn|Lottery|Gacha|Draw|Consume|Unlock|Compose|Dismantle|Decompose|Sell|Recycle|Withdraw|Take|Gain|Receive|Pick')

# Full game modes with their own proprietary simulation, events that are not announced, real-money flows and shops whose goods
# are not in the tables (RubyShop = event shop, StoreBlack* = black market without goods, StoreCart = event exchange cart),
# practice modes that need the battle settle engine (SimCombat Mythic/Unite/Week/Tutorial): documented in docs/feature-status.md
OUT_OF_SCOPE = re.compile(r'^MsgCs(Darkov|GetDarkov|SetDarkov|DarkZone|GetDarkZone|Rouge|GetRouge|ThemeYunTu|ThemeMiniGame|ThemeAnswer|ThemeCheckIn|SimCafe|GuildWar|ActivityAmo|ActivityThreeDimensions|ActivityCake|ActivityWorldBoss|Kitty|Monopoly|Jigsaw|Bingo|Sand|Shootspace|Tps|Uav|Geopolitic|Operation|Rich|HighPvp|NrtPvp|Async|Pvp|Client(Create|Join|Leave|Member|Start|Choose)|Activity(Back|BpTeam|CharacterRerun|Conversion|DelaySignIn|GetMedium|NewSystem|PaidSignin|SkinBargain|Anniversary|CheckInAnniversary)|Acticity|EventEndlessTower|EventSigninBp|Collection(GetReward|Settle)|TakeActivityDailyFood|TakeBingoExtraReward|OpenBingoCard|PazzleStage|Ppt(Finish|GetReward)|MiniGame(Finish|StageFinish)|ExPickThemeReward|FreeSkinGetReward|GetCareRewards|GetDispatchReward|CampFileTakeRewards|GetBattlepassRewardTimeLimit|LobbyGame(Start|Finish|ClaimUltimate)|LobbyMiniGame|Dev(IsLogin|Login)|GmOperation|AddRobotChat|RubyShop|StoreGetRechargeReward|BuyGuildBattleTicket|StoreBlackBuy|StoreTagRefresh|StoreCart|SimCombat(Mythic|Unite|Week|Tutorial)|Gain(GuildScoreReward|IndividualScoreReward|Supply)|Friend(Apply|ApproveApplication)|Moment|JoinBattleGroup|UnlockAdvancedReward|AccountCustomize)')

DOMAINS = [
    ('Campaña y fases', r'^MsgCs(Stage|Chapter|Difficult|Battle(?!pass)|Afk|Treasure|Plan|CyclePlan|Gun(Add|Battle)|ControlFight|Control(?!Room))'),
    ('Personajes (nivel, clase, talentos)', r'^MsgCs(Gun(?!Weapon|Remoulding)|PublicTalent|LockGunPublic|GetDelTalent|Skill|Love|Care|Costume|ReplaceGun|Formation|CustomFormation|Support)'),
    ('Armas y módulos', r'^MsgCs(Gun(Weapon|Remoulding)|Weapon|Replace|BuyGunWeapon|UnlockGunWeapon)'),
    ('Tienda y economía', r'^MsgCs(Store|Buy|Ruby|Exchange|ConfirmExchange|UseItems|UseGift|UseCustomized|Items|Compose|ItemDismantle|Dismantle|DeleteTimeLimit|AutoDismantle|SetAutoDismantle)'),
    ('Gacha', r'^MsgCs(Gacha|GetGacha|UpdateSettingGache)'),
    ('Misiones, logros y pase', r'^MsgCs(Quest|MultiQuest|ReadNewDaily|GetNewbee|Newbee|Take|Achieve|Battlepass|GetBattlepass|Daily|CheckIn|GetDailyCheckIn|ActivityCheckIn|Sign|Event|Index|Collection|OutfitCollection)'),
    ('Correo, chat y social', r'^MsgCs(Mail|Chat|Friend|Social|Refresh|Group|Moment)'),
    ('Banda', r'^MsgCs(Guild|GuildWar|Change(Guild|Leader)|Disband|Kick|Leave|HandleGuild|Set(GuildAuto|GuildJob)|Gain)'),
    ('Dormitorio y lobby 3D', r'^MsgCs(Lobby|Dorm|Handmade|Set(Handmade|Lobby)|Background|Bgm|Adjutant|BackAdjutant|Assistant)'),
    ('Expediciones y sala de control', r'^MsgCs(Dispatch|ControlRoom|Sim(Cafe))'),
    ('Simulación y modos de práctica', r'^MsgCs(SimCombat|Rouge|Get(Rouge|Sim)|Theme|Dark(?!ov)|GetDarkZone)'),
    ('Perfil y ajustes', r'^MsgCs(Mod|Set(Name|Personal|Bgm)|Change(Language)|Naming|Client|Tutorial|System|Timers|Timer|Preface|Gm|Get(Gm|Preface)|Sync|Login|HeartBeat|InitUser|PlayerCounter|Record)'),
]
DOM = [(n, re.compile(r)) for n, r in DOMAINS]


def domain(name):
    for n, r in DOM:
        if r.match(name):
            return n
    return 'Otros'


def status(name):
    if name in implemented:
        return 'implemented'
    if name in refused:
        return 'refused'
    if name in preferences:
        return 'preference'
    if name in queries:
        return 'read-empty'
    if OUT_OF_SCOPE.match(name):
        return 'out-of-scope'
    suffix = name[5:]
    if ('SC_' + suffix) in EMPTY_SC and not ACK_EXCLUDE.search(name):
        return 'ack-only'
    if is_echo(suffix):
        return 'echo-only'
    if ('MsgSc' + suffix) in paired and RO.search(suffix) and not SC.search(suffix):
        return 'read-empty'
    if 'CS_' + suffix in DEPRECATED or 'SC_' + suffix in DEPRECATED or 'CS_' + suffix not in MSGS:
        return 'deprecated'
    return 'none'


table = {}
for n in all_cs:
    table.setdefault(domain(n), {}).setdefault(status(n), []).append(n)

order = ['implemented', 'preference', 'echo-only', 'ack-only', 'read-empty', 'refused', 'none', 'deprecated', 'out-of-scope']
tot = {k: 0 for k in order}
for d in table.values():
    for k, v in d.items():
        tot[k] += len(v)
total = len(all_cs)
real = tot['implemented'] + tot['preference']

lines = [
    '# Estado de funciones por dominio',
    '',
    'Generado con `Griffin.ProtocolSmoke/gen-feature-status.py` (no se edita a mano). Estados: **implemented** (handler propio),',
    '**preference** (guardada y devuelta por `PreferenceResponder`), **echo-only** (la respuesta repite campos de la petición: `EchoResponder` guarda y devuelve, sin más efecto), **ack-only** (su respuesta oficial es un mensaje vacío: `AckResponder` confirma sin guardar estado), **read-empty** (consulta sin handler que recibe la respuesta vacía),',
    '**refused** (el handler solo responde con el error concreto del cliente porque el estado que haría falta no existe en este servidor: p. ej. sin chips de soporte no hay chip que romper), **none** (sin implementar: el cliente recibe el error `UserSystemLocked`, «función bloqueada», en vez de quedarse esperando), **deprecated** (el protocolo del cliente marca la petición o su respuesta como `deprecated`, o ni siquiera define el mensaje: las versiones nuevas usan otro), **out-of-scope** (alcanzable solo con contenido que el servidor no anuncia).',
    '',
    f'Solicitudes `MsgCs*`: {total}. Con comportamiento propio: {real} ({100 * real // total} %). Solo eco (sin más efecto): {tot["echo-only"]}. Solo confirmación (sin estado): {tot["ack-only"]}. Consulta vacía: {tot["read-empty"]}. Solo rechazo con error concreto: {tot["refused"]}. Sin implementar (error «bloqueado»): {tot["none"]}. Obsoletas en el protocolo: {tot["deprecated"]}. Fuera de alcance: {tot["out-of-scope"]}.',
    '',
    '| Dominio | implemented | preference | echo-only | ack-only | read-empty | refused | none | deprecated | out-of-scope | total |',
    '|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|',
]
for d in sorted(table):
    row = [len(table[d].get(k, [])) for k in order]
    lines.append(f'| {d} | ' + ' | '.join(map(str, row)) + f' | {sum(row)} |')
lines += ['', '## Pendientes por dominio (`none`: sin implementar)', '']
for d in sorted(table):
    pend = table[d].get('none', [])
    if pend:
        lines.append(f'### {d} ({len(pend)})')
        lines.append(', '.join(f'`{n[5:]}`' for n in pend))
        lines.append('')
open(f'{ROOT}/docs/feature-status.md', 'w', encoding='utf-8', newline='\n').write('\n'.join(lines))
print(f'total {total} real {real} read-empty {tot["read-empty"]} none {tot["none"]} out-of-scope {tot["out-of-scope"]}')
for d in sorted(table):
    print(f'{d}: ' + ', '.join(f'{k}={len(table[d].get(k, []))}' for k in order))


if __name__ == '__main__' and 'DUMP' in __import__('os').environ:
    for k in ('echo-only', 'ack-only', 'refused', 'read-empty', 'out-of-scope', 'none'):
        print('##', k)
        print(' '.join(sorted(n[5:] for d in table.values() for n in d.get(k, []))))
