using Serilog;

namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>
    /// One match of the Darkov mode (危区探遗): the state the room server keeps and the answers it gives (A-145). The client plays the match and reports what it did (movement,
    /// shots, hits, item moves, interactions) in <c>CS_DarkovOp</c>; the server keeps the books - attributes, hit points, containers, bag - and answers with
    /// <c>SC_DarkovRoomUpdate</c> deltas (components, each one a single-field message: 3 bag, 6 attributes, 9 objects, 11 unit attributes, 22 object state...).
    /// Every method returns the packets to send, in order, as (message id, payload).
    /// </summary>
    public sealed class DarkovRoom
    {
        public const ushort MsgMarchReply = 26003, MsgStartReply = 26007, MsgLoadedReply = 26009, MsgHeartReply = 26011, MsgOpReply = 26013, MsgFinished = 26014, MsgUpdate = 26015;

        private const int TicksPerSecond = 20;

        private readonly Random rng;
        private readonly Dictionary<uint, DUnit> units = [];
        private readonly Dictionary<uint, Dictionary<uint, long>> unitAttrs = [];
        private readonly Dictionary<uint, long> attrs = [];
        private readonly HashSet<uint> deadEnemies = [];

        public ulong RoomId { get; }
        public uint Uid { get; set; }
        public uint Stage { get; }
        public DarkovProfile Profile { get; }
        /// <summary>The preset character the tutorial stages give instead of the account's loadout (<c>DarkovPresetCharacter</c>), or 0.</summary>
        public uint Preset { get; }
        public DarkovWorld World { get; private set; }
        public uint Frame { get; private set; }
        public bool Started { get; private set; }
        public bool Finished { get; private set; }
        public int Kills { get; private set; }

        private uint lastTickFrame, loadedFrame;
        private long clientBase, serverBase;
        private readonly long createdMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private uint heroHp = 45000;
        private readonly uint requestedPoi;
        private DItem? magazine;

        public DarkovRoom(ulong roomId, uint stage, DarkovProfile profile, uint poi, Random? random = null)
        {
            rng = random ?? new Random();
            RoomId = roomId;
            requestedPoi = poi;
            Stage = stage;
            Preset = stage switch { 1000 => 1009001u, 9963 => 1009002u, _ => 0u };
            Profile = Preset == 0 ? profile : DarkovPresets.Create(Preset);
            uint variant = DarkovStages.Variant(stage, rng);
            World = DarkovWorld.Build(stage, variant, poi, rng);
            foreach (DUnit unit in World.Units)
                units[unit.Id] = unit;
            foreach ((uint id, long value) in PlayerAttributes())
                attrs[id] = value;
            heroHp = (uint)Math.Max(1, profile.HeroHp);
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // answers of the lobby side of the room
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>The room information shared by March and Start replies.</summary>
        public PbW RoomInfo(bool withLoadout)
        {
            var info = new PbW().V(1, RoomId).V(2, Stage).V(3, 1617669).M(4, new PbW().V(1, Profile.Hero));
            if (withLoadout)
                info.M(5, Loadout());
            if (withLoadout && Preset == 0)
                info.V(6, 1);
            info.V(7, 1).V(8, 3230000);
            if (requestedPoi != 0)
                info.V(10, requestedPoi);
            return info.V(12, 1);
        }

        public List<(ushort, byte[])> March(string host)
        {
            var reply = new PbW().M(1, RoomInfo(false)).M(2, new PbW().M(1, new PbW().Str(1, host)))
                .V(3, 50).V(5, (ulong)Now()).V(7, (ulong)Now() + 1).V(8, (ulong)rng.NextInt64(1, uint.MaxValue));
            return [(MsgMarchReply, reply.ToArray())];
        }

        public List<(ushort, byte[])> Start()
        {
            var reply = new PbW().M(1, RoomInfo(true)).V(2, 1).V(3, TimeLimit);
            return [(MsgStartReply, reply.ToArray())];
        }

        private const uint TimeLimit = 28800;

        /// <summary>What the player carries into the room: the hero with its weapon, the backpack, the equipment, the quick slots and the ammunition.</summary>
        private PbW Loadout()
        {
            var hero = new PbW().V(1, Profile.Hero)
                .M(3, new PbW().V(1, HeroUnitUid).V(2, 1010090).V(3, 1).M(12, new PbW().V(1, heroHp).V(2, 45000).V(3, 1).M(6, new PbW()).M(6, new PbW())))
                ;
            if (Preset == 0)
                hero.V(7, 6);
            for (int i = 0; i < 4; i++)
                hero.M(8, new PbW());
            magazine ??= new DItem { Uid = Profile.NewUid(), Cfg = 3071001, Count = 6 };
            if (Preset != 1009001)
                hero.M(9, magazine.Write()).V(10, 3071001);
            hero.V(11, HeroUnitUid);
            if (Preset == 0)
                hero.V(12, 10);
            else
                hero.V(13, Preset);

            var bag = Backpack();
            var player = new PbW().V(1, 1001).M(2, hero).M(3, bag)
                .M(4, new PbW().S(4, World.Spawn.X).S(5, World.Spawn.Y).S(6, World.Spawn.Z)).V(5, Profile.Hero);
            if (Profile.Exp != 0)
                player.V(6, Profile.Exp);
            return new PbW().V(1, 1617669).M(2, player);
        }

        private ulong HeroUnitUid => Profile.NextUid - 5;

        public PbW Backpack() => DarkovWire.Backpack(Profile);

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // loaded: the world is sent
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        public List<(ushort, byte[])> Loaded(long clientTime)
        {
            Started = true;
            clientBase = clientTime;
            serverBase = clientTime + 111649659;
            // the frame clock is the server time in 50 ms steps (what the client derives its own frame from: 117059047 ms <-> frame 2341180 in the official rooms)
            Frame = (uint)(serverBase / 50);
            lastTickFrame = loadedFrame = Frame;
            uint created = Frame - 123;
            foreach (DUnit unit in units.Values)
                unit.Frame = created;

            var timing = new PbW().M(1, new PbW().V(1, (ulong)(createdMs / 1000)).V(2, (ulong)rng.Next(1, 1_000_000_000)))
                .M(2, new PbW().V(1, (ulong)(createdMs / 1000) + 6).V(2, (ulong)rng.Next(1, 1_000_000_000)))
                .M(3, new PbW().V(1, (ulong)(createdMs / 1000)).V(2, (ulong)rng.Next(1, 1_000_000_000)))
                .V(4, TimeLimit).V(6, 12594).V(7, created).V(8, created + 12594 * 20).V(9, Frame);
            var delta = new PbW().M(1, new PbW().M(1, new PbW().V(1, RoomId).V(2, Stage).V(5, 1).V(6, 1).M(10, timing)))
                .M(2, Comp(51, new PbW().V(1, 1).V(2, Seed))).V(3, Frame);
            var reply = new PbW().V(1, RoomId).M(2, delta).V(4, 50).V(5, (ulong)clientTime).V(6, (ulong)serverBase).V(7, (ulong)serverBase)
                .M(8, new PbW().V(1, 5000).V(2, 15000).V(3, 10).V(4, 7).V(5, 200).V(6, 100).V(7, 500).V(8, 10).V(9, 100).V(10, 1000))
                .M(9, new PbW().V(1, (ulong)rng.NextInt64(1, long.MaxValue)));
            var packets = new List<(ushort, byte[])> { (MsgLoadedReply, reply.ToArray()) };

            // the first burst: the seed, then the attributes, buffs, hero, quests
            packets.Add(Update(Comp(51, new PbW().V(2, Seed))));
            var attrList = new PbW();
            foreach ((uint id, long value) in attrs.OrderBy(a => a.Key).Select(a => (a.Key, a.Value)))
            {
                var entry = new PbW().V(1, id);
                if (value != 0)
                    entry.S(2, value);
                attrList.M(1, entry);
            }

            var buffs = new PbW();
            foreach (uint buff in StartingBuffs())
            {
                var info = new PbW().V(2, ulong.MaxValue).V(4, created);
                if (buff / 100 == 3100921 || buff / 100 == 3100922 || buff / 100 == 3100923)
                    info.V(7, 1);
                var entry = new PbW().V(1, buff).V(2, 1).M(3, info);
                if (Preset != 0 && buff is 100002 or 100023)
                    entry.B(4, [0xF1, 0x07, 0x00, 0x00]);
                buffs.M(13, entry.V(5, 1001));
            }

            var heroAttrs = new PbW().V(1, Profile.Hero);
            foreach ((uint id, ulong learnedValue) in DarkovLearned.HeroAttrs)
            {
                ulong value = Preset != 0 && id == 3008 ? 229 : id == 6004 ? 360000 : id == 6005 ? 65000 : learnedValue; // 6004/6005 settle at these values right after the start in every official room // the tutorials have no hero grade bonus (+500 attack)
                if (id == 3010 && (Preset == 1009001 || Profile.Equip.All(e => e is null)))
                    continue; // no weapon: no loaded-magazine marker
                var entry = new PbW().V(1, id);
                if (value != 0)
                    entry.V(2, value);
                heroAttrs.M(2, entry);
            }
            var firstBurst = new List<PbW> { Comp(6, attrList), Comp(17, buffs), Comp(12, heroAttrs) };
            if (Preset == 0 && DarkovQuests.Component(Profile) is { } quest)
                firstBurst.Add(Comp(14, quest));
            else if (Preset != 0)
                firstBurst.Add(Comp(14, new PbW()));
            if (Preset != 0)
                firstBurst.Add(Comp(33, new PbW()));
            else
                firstBurst.Add(Comp(33, new PbW().M(1, new PbW().M(1, new PbW().V(1, 2030101).V(2, 1)))));
            packets.Add(Update(firstBurst.ToArray()));

            // enemies, then objects, in packets that stay under the size limit
            foreach (var group in units.Values.Where(u => u.Enemy).Chunk(40))
            {
                var comps = new List<PbW> { Comp(8, Chunk(group.Select(u => u.WriteEnemy()), 40).First()) };
                comps.AddRange(group.Select(u => Comp(11, UnitAttrsMessage(u.Id))));
                packets.Add(Update(comps.ToArray()));
            }
            foreach (var chunk in Chunk(units.Values.Where(u => !u.Enemy).Select(u => u.WriteIobj()), 40))
                packets.Add(Update(Comp(9, chunk)));

            // points of interest and the ones that start active
            var pois = new PbW();
            foreach ((ulong v1, uint poi) in World.Pois)
                pois.M(1, new PbW().V(1, v1).V(2, poi).V(4, ulong.MaxValue));
            packets.Add(Update(Comp(42, pois)));
            packets.Add(Update(Comp(25, Preset != 0 ? new PbW() : new PbW().M(1, new PbW().V(1, 9).V(2, 1)))));
            packets.Add(Update(Comp(26, null, 1)));
            foreach ((ulong v1, uint _) in World.Pois.Take(5))
                packets.Add(Update(Comp(43, new PbW().V(1, v1).V(4, Frame).V(2, 0))));
            return packets;
        }

        private ulong Seed => 100 + (RoomId % 400);

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // heartbeat: time passes, the attributes tick
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        public List<(ushort, byte[])> Heartbeat(long clientTime)
        {
            long server = serverBase + (clientTime - clientBase);
            var reply = new PbW().V(1, RoomId).V(3, (ulong)clientTime).V(4, (ulong)server).V(5, (ulong)server);
            var packets = new List<(ushort, byte[])> { (MsgHeartReply, reply.ToArray()) };
            AdvanceTo((uint)Math.Max(loadedFrame, server / 50), packets);
            return packets;
        }

        /// <summary>Lets the natural attribute ticks (one per second) happen up to <paramref name="frame"/>.</summary>
        private void AdvanceTo(uint frame, List<(ushort, byte[])> packets)
        {
            if (!Started || Finished || frame <= lastTickFrame)
                return;
            while (lastTickFrame + TicksPerSecond <= frame)
            {
                lastTickFrame += TicksPerSecond;
                var changed = new List<uint>();
                Tick(1005, 2009, 5004, changed);
                Tick(1003, 2005, 5003, changed);
                if (changed.Count > 0)
                    packets.Add(Update(lastTickFrame, Comp(6, Attrs(changed))));
            }

            Frame = Math.Max(Frame, lastTickFrame);
        }

        private void Tick(uint attr, uint rate, uint max, List<uint> changed)
        {
            long delta = attrs.GetValueOrDefault(rate);
            long cap = attrs.GetValueOrDefault(max, 100000);
            long before = attrs.GetValueOrDefault(attr);
            long after = Math.Clamp(before + delta, 0, cap);
            if (after == before)
                return;
            attrs[attr] = after;
            changed.Add(attr);
        }

        private PbW Attrs(IEnumerable<uint> ids)
        {
            var w = new PbW();
            foreach (uint id in ids)
            {
                long value = attrs.GetValueOrDefault(id);
                var a = new PbW().V(1, id);
                if (value != 0)
                    a.S(2, value);
                w.M(1, a);
            }

            return w;
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // operations of the client
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        public List<(ushort, byte[])> Ops(PbMsg request)
        {
            uint frame = (uint)request.U(2);
            var packets = new List<(ushort, byte[])>();
            var ack = new PbW().V(1, frame);
            var operations = request.Msgs(1).ToList();
            foreach (PbMsg op in operations)
                ack.M(2, op.Has(1) ? new PbW().V(1, op.U(1)) : new PbW());
            if (operations.Count == 0)
                ack.M(2, new PbW());
            // the acknowledgement closes the frame: the official server sent every update of the operations BEFORE it (1952 of 1954 in the capture); the client discards its
            // predicted result when the acknowledgement comes first, and the box it just opened closes again
            if (!Started || Finished)
            {
                packets.Add((MsgOpReply, ack.ToArray()));
                return packets;
            }

            AdvanceTo(frame, packets);
            Frame = Math.Max(Frame, frame);
            foreach (PbMsg op in operations)
            {
                try
                {
                    Apply(op, frame, packets);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Darkov room {Room}: operation {Fields} could not be applied.", RoomId, string.Join(",", op.Numbers));
                }
            }

            FlushShots(frame, packets);

            packets.Add((MsgOpReply, ack.ToArray()));
            return packets;
        }

        private void Apply(PbMsg op, uint frame, List<(ushort, byte[])> packets)
        {
            foreach (int field in op.Numbers.Where(n => n != 1).ToList())
            {
                switch (field)
                {
                    case 10 when op.Msg(10)!.Has(1):
                        Event(op.Msg(10)!.Msg(1)!, frame, packets);
                        break;
                    case 4:
                        StateRequest(op.Msg(4)!, frame, packets);
                        break;
                    case 5:
                        TakeItem(op.Msg(5)!, frame, packets);
                        break;
                    case 15:
                        foreach (PbMsg step in op.Msg(15)!.Msgs(1))
                            TakeItem(step, frame, packets);
                        break;
                    case 13:
                        Reload(op.Msg(13)!, frame, packets);
                        break;
                    case 6:
                        DropItem(op.Msg(6)!, frame, packets);
                        break;
                    case 22 when Preset != 0 && !Finished:
                        // the second tutorial ends with this operation (the scripted exit)
                        Finish(true, frame, packets);
                        break;
                }
            }
        }

        /// <summary>Shots and hits reported inside the movement operation: <c>10.1.3</c> = {11: shot, 12: hit}.</summary>
        private void Event(PbMsg actor, uint frame, List<(ushort, byte[])> packets)
        {
            PbMsg? ev = actor.Msg(3);
            if (ev is null)
                return;
            if (ev.Msg(12) is { } hit)
                Hit(hit, frame, packets);
            else if (ev.Has(11))
                Shot(frame, packets);
            else if (ev.Msg(13) is { } use)
                UseItem(use.U(1), frame, packets);
        }

        /// <summary>An item of the backpack is used (a medkit, bandages...): one is consumed and, for healing items (<c>EsRecover</c> kind 1), the hit points go up.</summary>
        private void UseItem(ulong uid, uint frame, List<(ushort, byte[])> packets)
        {
            int slot = Profile.Bag.FindIndex(i => i is not null && i.Uid == uid);
            if (slot < 0)
                return;
            DItem item = Profile.Bag[slot]!;
            item.Count--;
            var slotUpdate = new PbW();
            if (slot != 0)
                slotUpdate.V(1, (ulong)slot);
            if (item.Count == 0)
            {
                Profile.Bag[slot] = null;
                slotUpdate.M(2, new PbW());
            }
            else
                slotUpdate.M(2, new PbW().M(1, item.Write()));
            var comps = new List<PbW> { Comp(3, new PbW().M(1, slotUpdate)) };
            if (RawTables.ById("EsRecoverData").TryGetValue(item.Cfg, out PbMsg? recover) && recover.U(2) == 1)
            {
                long max = attrs.GetValueOrDefault(3001u, 35000);
                attrs[1001] = Math.Min(max, attrs.GetValueOrDefault(1001u) + (long)recover.U(4) * 6);
                comps.Add(Comp(6, Attrs([1001])));
            }

            packets.Add(Update(frame, comps.ToArray()));
        }

        private const uint MagazineSize = 6, WearPerShot = 117;

        /// <summary>The hero fires: one round leaves the magazine and the weapon wears.</summary>
        private bool shotPending, shotEmptied;

        private void Shot(uint frame, List<(ushort, byte[])> packets)
        {
            magazine ??= new DItem { Uid = Profile.NewUid(), Cfg = 3071001, Count = MagazineSize };
            if (magazine.Count == 0)
                return;
            magazine.Count--;
            heroHp = heroHp > WearPerShot ? heroHp - WearPerShot : 0;
            shotPending = true;
            shotEmptied |= magazine.Count == 0;
        }

        /// <summary>The shots of one operation message are reported once, with the state they leave (magazine, weapon wear).</summary>
        private void FlushShots(uint frame, List<(ushort, byte[])> packets)
        {
            if (!shotPending)
                return;
            shotPending = false;
            var w16 = magazine!.Count == 0 ? new PbW().M(1, new PbW().V(1, Profile.Hero)) : new PbW().M(1, new PbW().V(1, Profile.Hero).M(2, magazine.Write()));
            var comps = new List<PbW> { Comp(16, w16), Comp(10, new PbW().M(1, new PbW().V(1, Profile.Hero).M(2, HeroUnit()))) };
            if (magazine.Count == 0)
                comps.Add(Comp(12, new PbW().V(1, Profile.Hero).M(2, new PbW().V(1, 3010).V(2, 1))));
            shotEmptied = false;
            packets.Add(Update(frame, comps.ToArray()));
        }

        private PbW HeroUnit() => new PbW().V(1, HeroUnitUid).V(2, 1010090).V(3, 1).M(12, new PbW().V(1, heroHp).V(2, 45000).V(3, 1).M(6, new PbW()).M(6, new PbW()));

        private void Hit(PbMsg hit, uint frame, List<(ushort, byte[])> packets)
        {
            uint target = (uint)hit.U(2);
            long damage = hit.I(3);
            var deltas = hit.Msgs(6).Select(m => ((uint)m.U(1), m.I(2))).ToList();
            if (target == 1001)
            {
                var changed = new List<uint>();
                attrs[1001] = Math.Max(0L, attrs.GetValueOrDefault(1001u) - damage);
                changed.Add(1001);
                foreach ((uint id, long delta) in deltas)
                {
                    attrs[id] = attrs.GetValueOrDefault(id) + delta;
                    changed.Add(id);
                }

                var comps = new List<PbW>();
                int wearSlot = Profile.Equip.Count > 1 && Profile.Equip[1] is { HasDur: true } ? 1 : Profile.Equip.FindIndex(i => i is { HasDur: true });
                if (wearSlot >= 0)
                {
                    // being hit wears the equipment (1.2459 times the base damage of the hit, as the official server did)
                    DItem worn = Profile.Equip[wearSlot]!;
                    uint wear = (uint)Math.Max(1, (hit.U(10) != 0 ? (long)hit.U(10) : damage) * 12459 / 10000);
                    worn.DurCur = worn.DurCur > wear ? worn.DurCur - wear : 0;
                    var entry = new PbW();
                    if (wearSlot != 0)
                        entry.V(1, (ulong)wearSlot);
                    comps.Add(Comp(4, new PbW().M(1, entry.M(2, worn.Write()))));
                }

                comps.Add(Comp(6, Attrs(changed.Distinct())));
                packets.Add(Update(frame, comps.ToArray()));
                if (attrs[1001] == 0 && !Finished)
                    Finish(false, frame, packets);
                return;
            }

            if (units.TryGetValue(target, out DUnit? destructible) && !destructible.Enemy && destructible.Count > 0)
            {
                // a destructible object (training target, cover, mine...) counts its hits down; at zero the client destroys it (state change)
                destructible.Count--;
                packets.Add(Update(frame, Comp(50, new PbW().V(1, target).V(2, destructible.Count))));
                return;
            }

            if (!units.TryGetValue(target, out DUnit? unit) || !unit.Enemy || deadEnemies.Contains(target))
                return;
            var ua = UnitAttrs(target);
            ua[1001] = Math.Max(0, ua[1001] - damage);
            foreach ((uint id, long delta) in deltas)
                ua[id] = ua.GetValueOrDefault(id) + delta;
            bool killed = ua[1001] == 0;
            if (killed)
            {
                // the killing blow: the corpse with its loot, then the announcement of the death, then the attributes
                deadEnemies.Add(target);
                Kills++;
                PbMsg? at = hit.Msg(5);
                if (Preset == 0) SpawnCorpse(unit, at is null ? (unit.X, unit.Y, unit.Z) : (at.I(4), at.I(5), at.I(6)), frame, packets);
                packets.Add(Update(frame, Comp(7, new PbW().Packed(1, [target]))));
            }

            packets.Add(Update(frame, Comp(11, UnitAttrsMessage(target, [1001, .. deltas.Select(d => d.Item1)]))));
        }

        /// <summary>
        /// The starting attributes of the player: the base of the hero plus the attribute group of every piece of equipment (<c>DarkovEquipAttributeGroup</c>: armour level,
        /// shield, weight...), with the preset character's own values in the tutorials. The base comes from the official account (the hero's talents are not in the tables).
        /// </summary>
        private Dictionary<uint, long> PlayerAttributes()
        {
            var set = DarkovLearned.PlayerAttrs.ToDictionary(a => a.Id, a => (long)a.Value);
            foreach (uint equipped in new uint[] { 3007, 3013, 3017, 3042 })
                set.Remove(equipped);
            set[6001] = 30000;
            if (Preset != 0)
            {
                set[3001] = 25000;
                set[2002] = 100;
                set[2005] = 0;
                set[5055] = 1;
                PbMsg? preset = RawTables.ById("DarkovPresetCharacterData").GetValueOrDefault(Preset);
                set[1003] = preset is not null && preset.U(14) != 0 ? (long)preset.U(14) : 50000;
                set[1001] = preset is not null && preset.U(13) != 0 ? (long)preset.U(13) : 25000;
            }

            var groups = RawTables.ById("DarkovEquipAttributeGroupData");
            foreach (DItem? item in Profile.Equip)
            {
                if (item is null || !groups.TryGetValue(item.Cfg, out PbMsg? group))
                    continue;
                foreach (PbMsg pair in group.Msgs(4))
                    set[(uint)pair.U(1)] = set.GetValueOrDefault((uint)pair.U(1)) + pair.I(2);
            }

            return set;
        }

        /// <summary>
        /// The states the player starts with: those the stage level lists (<c>EsStageLevel</c> column 15), the preset character's (<c>DarkovPresetCharacter</c> column 18) and the buff of the
        /// hero's grade (<c>DarkovBuffConfig</c> 3100920xx01 + grade).
        /// </summary>
        private IEnumerable<uint> StartingBuffs()
        {
            var buffs = new List<uint>();
            PbMsg? level = RawTables.Rows("EsStageLevelData").FirstOrDefault(r => r.U(2) == Stage && r.U(3) == World.Variant);
            if (level is not null)
                buffs.AddRange(level.Nums(15).Select(n => (uint)n));
            if (RawTables.ById("DarkovPresetCharacterData").GetValueOrDefault(Preset) is { } preset)
                buffs.AddRange(preset.Nums(18).Select(n => (uint)n));
            buffs.Add(310092001 + 100 * (Preset != 0 ? 1u : 3u) + 0);
            return buffs;
        }

        /// <summary>The attributes of an enemy, created from the typical set of its configuration the first time they are needed.</summary>
        private Dictionary<uint, long> UnitAttrs(uint id)
        {
            if (unitAttrs.TryGetValue(id, out var existing))
                return existing;
            var set = new Dictionary<uint, long> { [1001] = 1500, [1015] = 0, [1023] = 1000 };
            DUnit unit = units[id];
            if (DarkovLearned.EnemyAttrs.TryGetValue(unit.Cfg, out var template))
            {
                foreach ((uint attr, ulong value) in template)
                    set[attr] = (long)value;
            }
            else if (DarkovLearned.EnemyHp.TryGetValue(unit.Cfg, out uint hp))
                set[1001] = hp;

            // the values of the enemy's level come from the table (DarkovEnemyAttributeGroup: configuration * 1000 + level): hit points, crit, damage, armour and resistances
            var groups = RawTables.ById("DarkovEnemyAttributeGroupData");
            for (uint level = unit.Level; level >= 1; level--)
            {
                if (!groups.TryGetValue((ulong)unit.Cfg * 1000 + level, out PbMsg? group))
                    continue;
                foreach (PbMsg pair in group.Msgs(3))
                    if (pair.U(1) is not (3010 or 3012 or 3013)) // armour columns: the official rooms did not take them from this table
                        set[(uint)pair.U(1)] = pair.I(2);
                if (group.Msgs(3).Any(pair => pair.U(1) == 3001))
                    set[1001] = set[3001];
                break;
            }

            return unitAttrs[id] = set;
        }

        private PbW UnitAttrsMessage(uint id, IEnumerable<uint>? only = null)
        {
            var set = UnitAttrs(id);
            var w = new PbW().V(1, id);
            foreach (uint attr in (only ?? set.Keys.OrderBy(k => k)).Distinct())
            {
                long value = set.GetValueOrDefault(attr);
                var a = new PbW().V(1, attr);
                if (value != 0)
                    a.S(2, value);
                w.M(3, a);
            }

            return w;
        }

        /// <summary>A killed enemy leaves a container with what it carried.</summary>
        private void SpawnCorpse(DUnit enemy, (long X, long Y, long Z) at, uint frame, List<(ushort, byte[])> packets)
        {
            var corpse = new DUnit
            {
                Id = World.NextId++, Cfg = 1001, X = at.X, Y = at.Y, Z = at.Z, State = 103, V1 = 1006, Dynamic = true, Frame = frame,
                Container = DarkovLoot.Roll(rng, Profile, Stage, DarkovLoot.EnemyPlan(enemy.Cfg), 2 + rng.Next(3), corpse: true),
            };
            units[corpse.Id] = corpse;
            packets.Add(Update(frame, Comp(9, new PbW().M(1, corpse.WriteIobj()))));
        }

        /// <summary>The client changes the state of an object it interacts with (opens a container, starts a channel...) or leaves through an exit.</summary>
        private void StateRequest(PbMsg request, uint frame, List<(ushort, byte[])> packets)
        {
            uint id = (uint)request.U(1);
            if (!units.TryGetValue(id, out DUnit? unit) || unit.Enemy)
                return;
            if (!request.Has(3))
            {
                if (DarkovStages.IsExit(unit))
                    Finish(true, frame, packets, unit);
                else if (request.U(5) == 102 && unit.Container is not null)
                    // the player leaves the container: the official server reports it as it stands (items taken leave empty slots)
                    packets.Add(Update(frame, Comp(9, new PbW().M(1, unit.WriteIobj()))));
                return;
            }

            uint next = (uint)request.U(3);
            if (unit.Container is null && unit.DropPlan != 0 && next == 102)
            {
                unit.Container = DarkovLoot.Roll(rng, Profile, Stage, unit.DropPlan, (int)Math.Max(1, Math.Min(unit.Rolls, 5)), v1: unit.V1);
                packets.Add(Update(frame, Comp(9, new PbW().M(1, unit.WriteIobj()))));
            }

            unit.State = next;
            packets.Add(Update(frame, Comp(22, new PbW().M(1, new PbW().V(1, id).V(2, next)))));

        }

        /// <summary>An item moves from a container to the backpack (or back): the client sends the container slot and the final stack.</summary>
        private void TakeItem(PbMsg step, uint frame, List<(ushort, byte[])> packets)
        {
            PbMsg? from = step.Msg(1);
            PbMsg? to = step.Msg(2);
            if (to is null)
                return;
            DItem? stack = DItem.Read(to.Msg(6));
            uint destinationType = (uint)to.U(1);
            int destination = (int)to.U(4);
            uint sourceType = from is null ? 0 : (uint)from.U(1);
            int sourceSlot = from is null ? 0 : (int)from.U(4);

            // the source loses the stack: a container of the room, or the backpack / equipment
            if (sourceType == 4 && from is not null)
            {
                if (units.TryGetValue((uint)from.U(2), out DUnit? container) && container.Container is { } list && sourceSlot < list.Count)
                {
                    list[sourceSlot] = null;
                    packets.Add(Update(frame, Comp(9, new PbW().M(1, container.WriteIobj()))));
                }
            }
            else if (sourceType == 0 && from is not null && from.Has(4) && sourceSlot < Profile.Bag.Count && destinationType != 0)
                Profile.Bag[sourceSlot] = null;
            else if (sourceType == 5 && sourceSlot < Profile.Equip.Count)
                Profile.Equip[sourceSlot] = null;

            switch (destinationType)
            {
                case 0 when stack is not null && destination >= 0 && destination < Profile.Bag.Count:
                {
                    Profile.Bag[destination] = stack;
                    var slotUpdate = new PbW();
                    if (destination != 0)
                        slotUpdate.V(1, (ulong)destination);
                    slotUpdate.M(2, new PbW().M(1, stack.Write()));
                    packets.Add(Update(frame, Comp(3, new PbW().M(1, slotUpdate))));
                    break;
                }
                case 5 when stack is not null && destination >= 0 && destination < Profile.Equip.Count:
                {
                    // equipment goes to its own slot (comp 4)
                    Profile.Equip[destination] = stack;
                    var entry = new PbW();
                    if (destination != 0)
                        entry.V(1, (ulong)destination);
                    packets.Add(Update(frame, Comp(4, new PbW().M(1, entry.M(2, stack.Write())))));
                    break;
                }
                case 2:
                {
                    // the quick slots hold the kind of item (comp 2)
                    int index = Math.Clamp(destination - 1, 0, Profile.QuickSlots.Length - 1);
                    uint cfg = stack?.Cfg ?? (uint)(to.Msg(6)?.U(2) ?? 0);
                    Profile.QuickSlots[index] = cfg;
                    packets.Add(Update(frame, Comp(2, new PbW().M(3, new PbW().Packed(2, Profile.QuickSlots.Select(q => (ulong)q))))));
                    break;
                }
            }
        }

        /// <summary>The weapon is reloaded: the magazine is filled from the ammunition in the backpack.</summary>
        private void Reload(PbMsg op, uint frame, List<(ushort, byte[])> packets)
        {
            magazine ??= new DItem { Uid = Profile.NewUid(), Cfg = 3071001, Count = MagazineSize };
            uint need = MagazineSize - Math.Min(MagazineSize, magazine.Count);
            bool wasEmpty = magazine.Count == 0;
            if (need == 0)
                return;
            int slot = Profile.Bag.FindIndex(i => i is not null && i.Cfg == magazine.Cfg && i.Count > 0);
            if (slot < 0)
                return;
            DItem stack = Profile.Bag[slot]!;
            uint taken = Math.Min(need, stack.Count);
            stack.Count -= taken;
            magazine.Count += taken;
            var slotUpdate = new PbW();
            if (slot != 0)
                slotUpdate.V(1, (ulong)slot);
            if (stack.Count == 0)
            {
                Profile.Bag[slot] = null;
                slotUpdate.M(2, new PbW());
            }
            else
                slotUpdate.M(2, new PbW().M(1, stack.Write()));
            packets.Add(Update(frame, Comp(3, new PbW().M(1, slotUpdate))));
            packets.Add(Update(frame, Comp(16, new PbW().M(1, new PbW().V(1, Profile.Hero).M(2, magazine.Write())))));
            if (wasEmpty)
                packets.Add(Update(frame, Comp(12, new PbW().V(1, Profile.Hero).M(2, new PbW().V(1, 3010).V(2, 2000)))));
        }

        /// <summary>A backpack item is thrown on the floor: it becomes an object holding it.</summary>
        private void DropItem(PbMsg op, uint frame, List<(ushort, byte[])> packets)
        {
            ulong uid = op.U(1);
            int slot = Profile.Bag.FindIndex(i => i is not null && i.Uid == uid);
            if (slot < 0)
                return;
            DItem item = Profile.Bag[slot]!;
            Profile.Bag[slot] = null;
            var drop = new DUnit
            {
                Id = World.NextId++, Cfg = 1003, X = World.Spawn.X, Y = World.Spawn.Y, Z = World.Spawn.Z, State = 103, V1 = 1008, Dynamic = true, Frame = frame,
                Container = [item],
            };
            units[drop.Id] = drop;
            packets.Add(Update(frame, Comp(9, new PbW().M(1, drop.WriteIobj()))));
            var slotUpdate = new PbW();
            if (slot != 0)
                slotUpdate.V(1, (ulong)slot);
            slotUpdate.M(2, new PbW());
            packets.Add(Update(frame, Comp(3, new PbW().M(1, slotUpdate))));
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // the end of the match
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        public bool Win { get; private set; }
        public bool Committed { get; set; }

        /// <summary>The account level for an amount of experience: one level for each threshold of <c>EsLevel</c> (column 2, cumulative) reached.</summary>
        public static uint LevelFor(uint exp)
        {
            uint level = 1;
            foreach (PbMsg row in RawTables.Rows("EsLevelData").Where(r => r.U(1) >= 2).OrderBy(r => r.U(1)))
            {
                if (exp < row.U(2))
                    break;
                level = (uint)row.U(1);
            }

            return level;
        }

        /// <summary>The player leaves before reaching an exit.</summary>
        public List<(ushort, byte[])> Abandon()
        {
            var packets = new List<(ushort, byte[])>();
            if (Started && !Finished)
                Finish(false, Frame, packets);
            Finished = true;
            return packets;
        }

        /// <summary>
        /// Settles the match into the account: a win keeps what the player carries (backpack, equipment, ammunition) and gives experience; a loss
        /// takes the backpack and the equipment away.
        /// </summary>
        public void Settle(DarkovProfile account)
        {
            if (Win && Preset != 0)
            {
                account.Exp += (uint)(66 + Kills * 5);
                account.Level = Math.Max(account.Level, LevelFor(account.Exp));
                if (!account.Stages.TryGetValue(Stage, out DExplore? tutorial))
                    account.Stages[Stage] = tutorial = new DExplore { Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                tutorial.Extractions++;
                tutorial.Flags |= 1;
            }
            else if (Preset != 0)
            {
                // a tutorial room is lost without costing anything
            }
            else if (Win)
            {
                account.Bag = Profile.Bag;
                account.Equip = Profile.Equip;
                account.Ammo = Profile.Ammo;
                account.QuickSlots = Profile.QuickSlots;
                account.NextUid = Math.Max(account.NextUid, Profile.NextUid);
                account.Exp += (uint)(66 + Kills * 5);
                account.Level = Math.Max(account.Level, LevelFor(account.Exp));
                if (!account.Stages.TryGetValue(Stage, out DExplore? progress))
                    account.Stages[Stage] = progress = new DExplore { Since = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                progress.Extractions++;
                progress.Flags |= 1;
                account.Quests = Profile.Quests;
            }
            else
            {
                account.Bag = [];
                account.Equip = [null, null, null, null];
                account.NextUid = Math.Max(account.NextUid, Profile.NextUid);
            }

            account.Normalize();
        }

        private void Finish(bool win, uint frame, List<(ushort, byte[])> packets, DUnit? exit = null)
        {
            Finished = true;
            Win = win;
            if (win)
            {
                DarkovQuests.CompleteOnExtraction(Profile);
                if (DarkovQuests.Component(Profile) is { } questState)
                    packets.Add(Update(frame, Comp(14, questState)));
            }

            long seconds = Math.Max(1, (Frame - loadedFrame) / TicksPerSecond);
            uint exp = win ? (uint)(66 + Kills * 5) : 0;
            var finished = new PbW().V(1, win ? 1UL : 2UL);
            if (Profile.Exp != 0)
                finished.V(3, Profile.Exp);
            if (exp != 0)
                finished.V(4, exp);
            finished.V(5, 2).V(6, Profile.Hero);
            if (exp != 0)
                finished.M(7, new PbW().V(1, Profile.Hero).V(3, exp));
            finished.V(9, Stage)
                .M(10, new PbW().V(1, Profile.Hero).M(3, new PbW().V(1, HeroUnitUid).V(2, 1010090).V(3, 1).M(12, new PbW().V(1, heroHp).V(2, 45000).V(3, 1)))
                    .V(10, 3071001).V(11, HeroUnitUid))
                .V(11, (ulong)seconds);
            packets.Add((MsgFinished, finished.ToArray()));
        }

        // ------------------------------------------------------------------------------------------------------------------------------------------------
        // building blocks
        // ------------------------------------------------------------------------------------------------------------------------------------------------

        /// <summary>One component of a room update: a message holding the single field <paramref name="field"/>.</summary>
        private static PbW Comp(int field, PbW? payload, ulong value = 0) =>
            payload is null ? new PbW().V(field, value) : new PbW().M(field, payload);

        private (ushort, byte[]) Update(params PbW[] comps) => Update(Frame, comps);

        private (ushort, byte[]) Update(uint frame, params PbW[] comps)
        {
            var delta = new PbW();
            foreach (PbW comp in comps)
                delta.M(2, comp);
            delta.V(3, frame);
            return (MsgUpdate, new PbW().V(1, RoomId).M(2, delta).ToArray());
        }

        private static IEnumerable<PbW> Chunk(IEnumerable<PbW> items, int size)
        {
            PbW? current = null;
            int count = 0;
            foreach (PbW item in items)
            {
                current ??= new PbW();
                current.M(1, item);
                if (++count == size)
                {
                    yield return current;
                    current = null;
                    count = 0;
                }
            }

            if (current is not null)
                yield return current;
        }

        private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public IReadOnlyDictionary<uint, DUnit> Units => units;

        /// <summary>Adds (or replaces) a unit with an id chosen by the caller: used by the replay test that plays the recorded operations against the official world.</summary>
        public void AdoptAttrs(uint id, Dictionary<uint, long> set) => unitAttrs[id] = set;

        public void Adopt(DUnit unit)
        {
            units[unit.Id] = unit;
            World.NextId = Math.Max(World.NextId, unit.Id + 1);
        }
    }
}
