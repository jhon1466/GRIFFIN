namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>One thing in the room that is not the player: an enemy, an interactive object (container, door, exit...) or something dropped on the floor.</summary>
    public sealed class DUnit
    {
        public uint Id { get; set; }
        public bool Enemy { get; set; }
        public uint Cfg { get; set; }
        public uint Yaw { get; set; }
        public long X { get; set; }
        public long Y { get; set; }
        public long Z { get; set; }
        public uint State { get; set; }
        /// <summary>EsIobjDepotV1 type (column 4).</summary>
        public uint Kind { get; set; }
        public ulong Depot { get; set; }
        public ulong V1 { get; set; }
        public uint Group { get; set; }
        /// <summary>Enemy level: the spawn group's level, or the next one (the official server rolled between them).</summary>
        public uint Level { get; set; } = 1;
        public ulong Field6 { get; set; }
        public uint Count { get; set; }
        public bool Dynamic { get; set; }
        public uint Frame { get; set; }
        public uint DropPlan { get; set; }
        public uint Rolls { get; set; }
        /// <summary>What it holds once it is opened or killed (null while it has not been rolled).</summary>
        public List<DItem?>? Container { get; set; }

        public PbW WritePos()
        {
            var w = new PbW();
            if (Yaw != 0)
                w.V(3, Yaw);
            return w.S(4, X).S(5, Y).S(6, Z);
        }

        public PbW WriteContainer()
        {
            var w = new PbW().V(1, (ulong)Container!.Count);
            foreach (DItem? item in Container)
                w.M(2, item is null ? new PbW() : new PbW().M(1, item.Write()));
            return w;
        }

        /// <summary>An interactive object: <c>{1: id, 2: config, 3: position, 4: state, 7: depot row, 10: frame}</c>.</summary>
        public PbW WriteIobj()
        {
            var w = new PbW().V(1, Id).V(2, Cfg).M(3, WritePos()).V(4, State).V(7, V1);
            if (Dynamic)
                w.V(8, 1);
            if (Count != 0)
                w.V(9, Count);
            w.V(10, Frame);
            if (Container is not null)
                w.M(5, WriteContainer());
            return w;
        }

        /// <summary>An enemy: <c>{1: id, 2: {1: id, 2: config, 3: position, 4: depot, 5: spawn group, 6: carried loot, 8: depot row, 9: dynamic}}</c>.</summary>
        public PbW WriteEnemy()
        {
            var body = new PbW().V(1, Id).V(2, Cfg).M(3, WritePos()).V(4, Depot).V(5, Group);
            if (Field6 != 0)
                body.V(6, Field6);
            body.V(8, V1);
            if (Dynamic)
                body.V(9, 1);
            return new PbW().V(1, Id).M(2, body);
        }
    }

    /// <summary>
    /// The world of a room generated from the client tables of the mode (A-145): the stage level picks the depot rows (<c>EsIobjDepotV1</c> interactive objects with their
    /// drop plan and chance, <c>EsEnemyDepotV1</c> enemies grouped by <c>EsEnemySpawnGroupV1</c>) and the points of interest (<c>DarkovPoiDepotV1</c>).
    /// </summary>
    public sealed class DarkovWorld
    {
        public uint Stage { get; init; }
        public uint Variant { get; init; }
        public List<DUnit> Units { get; } = [];
        public List<(ulong V1, uint Poi)> Pois { get; } = [];
        public (long X, long Y, long Z) Spawn { get; set; }
        public uint NextId { get; set; } = 1002;
        public uint SpawnPoi { get; set; }

        public static DarkovWorld Build(uint stage, uint variant, uint poi, Random rng)
        {
            var world = new DarkovWorld { Stage = stage, Variant = variant };
            string prefix = $"{stage}{variant}";

            var enemyGroupCfg = RawTables.ById("EsEnemyGroupConfigData");
            var depotGroup = new Dictionary<ulong, (uint Group, uint Cfg, uint Level)>();
            foreach (PbMsg group in RawTables.Rows("EsEnemySpawnGroupV1Data").Where(r => r.U(1).ToString().StartsWith(prefix)))
                foreach (ulong depot in group.Nums(4))
                    depotGroup[depot] = ((uint)group.U(2), (uint)group.U(8), (uint)group.U(10));

            // enemies: half of the rows of the level
            foreach (PbMsg row in RawTables.Rows("EsEnemyDepotV1Data").Where(r => r.U(1).ToString().StartsWith(prefix)))
            {
                ulong depot = row.U(2);
                bool grouped = depotGroup.TryGetValue(depot, out var group);
                if (grouped && group.Cfg != 0 && rng.NextDouble() > 0.5)
                    continue;
                // small stages list the enemy configuration on the row itself
                uint cfg = grouped ? PickEnemy(enemyGroupCfg, group.Cfg, rng) : 0;
                if (cfg == 0)
                    cfg = (uint)row.U(6);
                if (cfg == 0)
                    continue;
                long[] pos = RawTables.Numbers(row.Str(4));
                if (pos.Length < 3)
                    continue;
                world.Units.Add(new DUnit
                {
                    Id = world.NextId++, Enemy = true, Yaw = (uint)row.U(5), Cfg = cfg, Level = grouped ? group.Level + (uint)rng.Next(0, 2) : 1, X = pos[0], Y = pos[1], Z = pos[2], Depot = depot, V1 = row.U(1), Group = group.Group,
                    Field6 = DarkovLearned.EnemyField6.GetValueOrDefault((uint)row.U(1)),
                });
            }

            // interactive objects: the kinds the official server was seen to place, with the chance the row declares
            foreach (PbMsg row in RawTables.Rows("EsIobjDepotV1Data").Where(r => r.U(1).ToString().StartsWith(prefix)))
            {
                // the object type (EsInterMain): the row's own type, or the type of its configuration when the row uses a stage-specific one; the table holds the initial state (14) and amount (7)
                uint kind = (uint)row.U(4);
                var interMain = RawTables.ById("EsInterMainData");
                if (!interMain.ContainsKey(kind) && RawTables.ById("EsIobjDepotData").TryGetValue(row.U(2), out PbMsg? config))
                    kind = (uint)config.U(2);
                if (!interMain.TryGetValue(kind, out PbMsg? inter) || !inter.Has(14))
                    continue;
                (uint State, uint Count) learned = ((uint)inter.U(14), inter.U(7) == ulong.MaxValue ? 0 : (uint)inter.U(7));
                string[] plan = row.Str(8).Split(';');
                double chance = 1, percent0 = 0;
                if (plan.Length >= 3 && uint.TryParse(plan[0], out _) && double.TryParse(plan[2], out double percent) && percent > 0)
                {
                    chance = percent / 100.0;
                    percent0 = percent;
                }
                if (rng.NextDouble() > chance)
                    continue;
                long[] pos = RawTables.Numbers(row.Str(5));
                if (pos.Length < 3)
                    continue;
                var unit = new DUnit
                {
                    Id = world.NextId++, Cfg = (uint)row.U(2), X = pos[0], Y = pos[1], Z = pos[2], State = learned.State, Count = learned.Count, V1 = row.U(1), Kind = kind,
                };
                if (plan.Length >= 3 && uint.TryParse(plan[0], out uint planId) && uint.TryParse(plan[1], out uint rolls) && percent0 > 0)
                {
                    unit.DropPlan = planId;
                    unit.Rolls = rolls;
                }

                world.Units.Add(unit);
            }

            // the official rooms number the objects first (in depot order) and the enemies after them
            uint next = 1002;
            foreach (DUnit unit in world.Units.Where(u => !u.Enemy).OrderBy(u => u.V1))
                unit.Id = next++;
            foreach (DUnit unit in world.Units.Where(u => u.Enemy).OrderBy(u => u.V1))
                unit.Id = next++;
            world.NextId = next;

            // points of interest of the level
            foreach (PbMsg row in RawTables.Rows("DarkovPoiDepotV1Data").Where(r => r.U(1).ToString().StartsWith(prefix)))
                world.Pois.Add((row.U(1), (uint)row.U(2)));

            // where the player appears: the spawn row of the point of interest asked for (any variant), or the first one of the level
            var spawns = RawTables.Rows("EsPlayerSpawnV1Data").Where(r => r.U(1).ToString().StartsWith(stage.ToString())).ToList();
            PbMsg? spawn = spawns.FirstOrDefault(r => poi != 0 && r.U(2) == poi && r.U(3) == variant) ?? spawns.FirstOrDefault(r => poi != 0 && r.U(2) == poi)
                ?? spawns.FirstOrDefault(r => r.U(1).ToString().StartsWith(prefix)) ?? spawns.FirstOrDefault();
            world.SpawnPoi = spawn is null ? poi : (uint)spawn.U(2);
            long[] at = spawn is null ? [] : RawTables.Numbers(spawn.Str(4));
            world.Spawn = at.Length >= 3 ? (at[0], at[1], at[2]) : (19056, 500, 3029);
            return world;
        }

        private static uint PickEnemy(IReadOnlyDictionary<ulong, PbMsg> groups, uint groupConfig, Random rng)
        {
            if (!groups.TryGetValue(groupConfig, out PbMsg? config))
                return 0;
            var cfgs = config.Str(2).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(uint.Parse).ToList();
            return cfgs.Count == 0 ? 0 : cfgs[rng.Next(cfgs.Count)];
        }
    }
}
