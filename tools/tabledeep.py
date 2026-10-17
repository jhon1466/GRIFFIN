"""Reads rows of a client table (bk_stc_pb) by FIELD NUMBER, decoding nested messages, repeated and packed fields.

    python tools/tabledeep.py <stc dir> <TableName> [id ...] [--limit N] [--col N]

The typed table protos of the checked-in build are stale (names of an older version), so semantics must be read by
field number and checked against real values; this tool prints exactly what is in the table:
  - an integer stays an integer,
  - a length-delimited value is shown as text when it is printable UTF-8, otherwise as a nested message
    ({field: value, ...}; repeated fields become lists) or, failing that, as packed varints or hex.
A map<uint32,uint32> column therefore shows as a list of {1: key, 2: value}.
"""
import struct
import sys


def varint(b, i):
    r = s = 0
    while True:
        c = b[i]
        i += 1
        r |= (c & 0x7F) << s
        if not c & 0x80:
            return r, i
        s += 7


def rows(path):
    b = open(path, 'rb').read()
    hs = struct.unpack_from('<I', b, 0)[0]
    i = 4 + hs
    while i < len(b):
        tag, i = varint(b, i)
        if tag == 10:
            ln, i = varint(b, i)
            yield b[i:i + ln]
            i += ln
        else:
            wt = tag & 7
            if wt == 0:
                _, i = varint(b, i)
            elif wt == 2:
                ln, i = varint(b, i)
                i += ln
            elif wt == 5:
                i += 4
            elif wt == 1:
                i += 8


def parse(b):
    """Dict field -> value or list of values; None when the bytes are not a well-formed message."""
    i, out = 0, {}
    try:
        while i < len(b):
            tag, i = varint(b, i)
            fn, wt = tag >> 3, tag & 7
            if fn == 0 or fn > 200:
                return None
            if wt == 0:
                v, i = varint(b, i)
            elif wt == 2:
                ln, i = varint(b, i)
                if i + ln > len(b):
                    return None
                v = b[i:i + ln]
                i += ln
            elif wt == 5:
                v = struct.unpack_from('<f', b, i)[0]
                i += 4
            elif wt == 1:
                v = struct.unpack_from('<d', b, i)[0]
                i += 8
            else:
                return None
            out.setdefault(fn, []).append(v)
    except (IndexError, struct.error):
        return None
    return out


def packed(b):
    i, out = 0, []
    try:
        while i < len(b):
            v, i = varint(b, i)
            out.append(v)
    except IndexError:
        return None
    return out


def show(v, depth=0):
    if isinstance(v, int):
        return v
    if isinstance(v, float):
        return round(v, 4)
    try:
        t = v.decode('utf-8')
        short_binary = len(v) <= 8 and any(ord(c) < 32 for c in t)  # e.g. packed varints [10, 50] look like "\n2"
        if t and not short_binary and all(c.isprintable() or c in '\n\t' for c in t) and (len(t) > 3 or not t.isdigit()) and not (len(v) <= 4 and parse(v)):
            return repr(t) if len(t) <= int(__import__('os').environ.get('TABLEDEEP_MAX', '120')) else repr(t[:int(__import__('os').environ.get('TABLEDEEP_MAX', '120'))] + '…')
    except UnicodeDecodeError:
        pass
    m = parse(v) if depth < 5 else None
    if m:
        return {k: (show(x[0], depth + 1) if len(x) == 1 else [show(y, depth + 1) for y in x]) for k, x in sorted(m.items())}
    p = packed(v)
    if p is not None and len(p) <= 64:
        return p
    return v.hex()[:80]


def row_dict(r):
    m = parse(r) or {}
    return {k: (show(x[0]) if len(x) == 1 else [show(y) for y in x]) for k, x in sorted(m.items())}


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    args = [a for a in sys.argv[1:]]
    limit, col = 5, None
    if '--limit' in args:
        j = args.index('--limit'); limit = int(args[j + 1]); del args[j:j + 2]
    if '--col' in args:
        j = args.index('--col'); col = int(args[j + 1]); del args[j:j + 2]
    stc, name, want = args[0], args[1], {int(a) for a in args[2:]}
    n = shown = 0
    for r in rows(f'{stc}/{name}.bytes'):
        d = row_dict(r)
        n += 1
        key = d.get(col if col is not None else 1)
        if want and key not in want:
            continue
        if shown < limit or want:
            print(d)
            shown += 1
    print(f'{name}: {n} rows')
