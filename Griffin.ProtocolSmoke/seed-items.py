"""Test fixture: puts items in the inventory of a smoke account of the TEST database (never the user's server).

    PYTHONPATH=<dir with pg8000> python Griffin.ProtocolSmoke/seed-items.py smoke@local.test 601=7000 300040=50

The account must already exist (run any smoke scenario once). Needs `pip install --target <dir> pg8000`. Item type defaults to 2 (plain item);
write `45=100:1` for a currency (type 1). Used for scenarios that need materials a new account does not have
(battle pass experience, gifts...); the server code itself never grants them.
"""
import sys
import pg8000.native as pg

email, pairs = sys.argv[1], [p.split('=') for p in sys.argv[2:]]  # item=count or item=count:type (type 1 = currency)
db = pg.Connection('postgres', password='password', host='localhost', database='griffin_claude_test')
uid = db.run('select "Uid" from "Accounts" where "Email"=:e', e=email)[0][0]
for item, spec in pairs:
    count, _, kind = spec.partition(':')
    db.run('delete from "Items" where "AccountUid"=:u and "ItemId"=:i', u=uid, i=int(item))
    db.run('insert into "Items" ("ItemId","Count","AccountUid","Type") values (:i,:c,:u,:t)', i=int(item), c=int(count), u=uid, t=int(kind or 2))
    print(f'account {uid}: item {item} x{count}')
