namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>What containers and enemies hold: stacks drawn from the drop tables (<c>EsDropPlan</c> -> <c>EsDropItem</c> "item:amount:weight" lists).</summary>
    public static class DarkovLoot
    {
        private static List<(uint Cfg, uint Count, int Weight)>? pool;

        /// <summary>Every "item:amount:weight" entry of the drop tables: the pool a plan without data of its own falls back to.</summary>
        private static List<(uint Cfg, uint Count, int Weight)> Pool()
        {
            if (pool is not null)
                return pool;
            var all = new List<(uint, uint, int)>();
            foreach (PbMsg row in RawTables.Rows("EsDropItemData"))
                all.AddRange(Entries(row.Str(2)));
            return pool = all;
        }

        private static IEnumerable<(uint Cfg, uint Count, int Weight)> Entries(string text)
        {
            foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] f = part.Split(':');
                if (f.Length >= 3 && uint.TryParse(f[0], out uint cfg) && uint.TryParse(f[1], out uint count) && int.TryParse(f[2], out int weight))
                    yield return (cfg, count, weight);
            }
        }

        public static uint EnemyPlan(uint enemyCfg) => 10000 + enemyCfg % 100;

        /// <summary>The official server's real behaviour (the client tables do not hold its drop data): a container gets one of the sets of stacks the official containers of the same plan held.</summary>
        public static List<DItem?>? Observed(Random rng, DarkovProfile profile, uint stage, uint plan, bool corpse)
        {
            (uint Cfg, uint Count, uint Cur, uint Max)[][]? sets = corpse ? DarkovLearned.CorpseLoot : PlanSets(stage, plan);
            if (sets is null || sets.Length == 0)
                return null;
            var chosen = sets[rng.Next(sets.Length)];
            var result = new List<DItem?>();
            foreach (var stack in chosen)
                result.Add(new DItem { Uid = profile.NewUid(), Cfg = stack.Cfg, Count = stack.Count, DurCur = stack.Cur, DurMax = stack.Max });
            return result;
        }

        private static (uint Cfg, uint Count, uint Cur, uint Max)[][]? PlanSets(uint stage, uint plan)
        {
            if (DarkovLearned.PlanLoot.TryGetValue((ulong)stage * 1000000 + plan, out var exact))
                return exact;
            // a stage the official rooms never showed this plan on: the open map's version of it, unless the stage is a tutorial
            return stage is 1000 or 9963 ? null : DarkovLearned.PlanLoot.GetValueOrDefault(1001UL * 1000000 + plan);
        }

        public static List<DItem?> Roll(Random rng, DarkovProfile profile, uint stage, uint plan, int slots, bool corpse = false, ulong v1 = 0)
        {
            if (v1 != 0 && DarkovLearned.TutorialLoot.TryGetValue(v1, out var exact))
                return exact.Select(st => (DItem?)new DItem { Uid = profile.NewUid(), Cfg = st.Cfg, Count = st.Count, DurCur = st.Cur, DurMax = st.Max }).ToList();
            if (Observed(rng, profile, stage, plan, corpse) is { } seen)
                return seen;
            var result = new List<DItem?>();
            var items = RawTables.ById("EsItemData");
            var entries = new List<(uint Cfg, uint Count, int Weight)>();
            if (RawTables.ById("EsDropPlanData").TryGetValue(plan, out PbMsg? planRow) && planRow.Raw(2) is { } raw
                && RawTables.ById("EsDropItemData").TryGetValue(PbMsg.Parse(raw).U(1), out PbMsg? dropItem))
                entries.AddRange(Entries(dropItem.Str(2)));
            if (entries.Count == 0)
                entries.AddRange(Pool());
            if (entries.Count == 0)
                return result;
            int total = entries.Sum(e => Math.Max(1, e.Weight));
            for (int i = 0; i < slots; i++)
            {
                int pick = rng.Next(total);
                foreach (var e in entries)
                {
                    pick -= Math.Max(1, e.Weight);
                    if (pick >= 0)
                        continue;
                    var item = new DItem { Uid = profile.NewUid(), Cfg = e.Cfg, Count = Math.Max(1, e.Count) };
                    if (items.TryGetValue(e.Cfg, out PbMsg? info) && info.U(13) > 0)
                    {
                        item.DurMax = (uint)info.U(13);
                        item.DurCur = (uint)(item.DurMax * (0.4 + rng.NextDouble() * 0.6));
                    }

                    result.Add(item);
                    break;
                }
            }

            return result;
        }
    }
}
