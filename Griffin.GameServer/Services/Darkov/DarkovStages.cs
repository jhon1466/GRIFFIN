namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>Facts about the stages (maps) of the mode read from the client tables.</summary>
    public static class DarkovStages
    {
        /// <summary>
        /// The level variant of a stage: the official server used the easiest one that is fully populated (variant 1 of the open map, 0 of the small ones), which is the
        /// variant with the most interactive objects in <c>EsIobjDepotV1</c> (the lowest one on a tie).
        /// </summary>
        public static uint Variant(uint stage, Random rng)
        {
            var variants = RawTables.Rows("EsStageLevelData").Where(r => r.U(2) == stage).Select(r => (uint)r.U(3)).Distinct().OrderBy(v => v).ToList();
            if (variants.Count == 0)
                return 0;
            var rows = RawTables.Rows("EsIobjDepotV1Data");
            uint best = variants[0];
            int bestCount = -1;
            foreach (uint variant in variants)
            {
                string prefix = $"{stage}{variant}";
                int count = rows.Count(r => r.U(1).ToString().StartsWith(prefix));
                if (count > bestCount)
                {
                    best = variant;
                    bestCount = count;
                }
            }

            return best;
        }

        /// <summary>Extraction points: the interactive objects whose depot type holds a channel time (<c>6000;0</c>, <c>5000;0</c>).</summary>
        public static bool IsExit(DUnit unit) => unit.Kind is 303 or 313 or 314;
    }
}
