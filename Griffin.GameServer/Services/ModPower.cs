using Griffin.Common.Table;
using Griffin.Database.Entities;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The power ("effect of the set", 配件特效) of weapon mods (A-78). Read from the 4.0.5136 client and the tables: a mod with a plan in
    /// <c>WeaponModData</c> column 12 (201 for the 349 mods of class 3, ranks 4 and 5) carries one <c>ModPowerData</c> row of that plan (<c>ModPowerBySuitPlanIdData</c>
    /// lists them: ids 17..32, one per effect), sent in field 4 of <c>GunWeaponMod</c>; the row names an effect of <c>ModPowerEffectData</c> whose column 3 gives a
    /// <c>BattleSkillData</c> id (1001..1020: attack, critical, shield, ...) and <c>PowerSkillCsData</c> (id = skill x 100 + rank of the mod, column 5) the number the
    /// skill reads through the weapon-part specification of the battle expressions (<c>battleExpressionParams.WeaponPartSepc</c>). The battle unit lists those skills in
    /// its "accessories" (field 15 of the skill message: skill id, base values, extra values; the engine uses base + extra). The tables do not say how a
    /// mod gets its power: it is drawn from the plan with the uid of the mod, so that it is always the same one.
    /// </summary>
    public static class ModPower
    {
        public static uint PowerOf(ITableService tables, WeaponModEntity mod)
        {
            WeaponModData? data = tables.GetTable<WeaponModData>().FirstOrDefault(row => row.Id == mod.WeaponModId);
            uint plan = data is null ? 0 : TableFields.U(data, 12);
            if (plan == 0)
                return 0;

            ModPowerBySuitPlanIdData? row = tables.GetTable<ModPowerBySuitPlanIdData>().FirstOrDefault(r => TableFields.U(r, 1) == plan);
            IReadOnlyList<uint> powers = row is null ? [] : TableFields.List(row, 2);
            return powers.Count == 0 ? 0 : powers[(int)(mod.Uid % (ulong)powers.Count)];
        }

        /// <summary>
        /// What goes in field 4 of <c>GunWeaponMod</c>: the client (<c>OEJHGIFDIJH</c> constructor) reads that number from BOTH <c>ModPowerData</c> and <c>ModPowerEffectData</c> (the log showed
        /// "ModPowerEffectData ID:27" for powers 17..32), so it is the effect id (1..16, <c>ModPowerData</c> column 3), which exists in both tables.
        /// </summary>
        public static uint EffectIdOf(ITableService tables, WeaponModEntity mod)
        {
            uint power = PowerOf(tables, mod);
            ModPowerData? row = power == 0 ? null : tables.GetTable<ModPowerData>().FirstOrDefault(r => TableFields.U(r, 1) == power);
            return row is null ? 0 : TableFields.U(row, 3);
        }

        /// <summary>The skill of a mod's power and the numbers it reads (<c>PowerSkillCsData</c> column 5 of its rank); null when the mod has no power.</summary>
        public static (uint Skill, int[] Values)? SkillOf(ITableService tables, WeaponModEntity mod)
        {
            uint power = PowerOf(tables, mod);
            ModPowerData? powerRow = power == 0 ? null : tables.GetTable<ModPowerData>().FirstOrDefault(r => TableFields.U(r, 1) == power);
            ModPowerEffectData? effect = powerRow is null ? null : tables.GetTable<ModPowerEffectData>().FirstOrDefault(r => TableFields.U(r, 1) == TableFields.U(powerRow, 3));
            uint skill = effect is null ? 0 : TableFields.Map(effect, 3).Select(pair => pair.Value).FirstOrDefault();
            if (skill == 0)
                return null;

            uint rank = tables.GetTable<WeaponModData>().Where(row => row.Id == mod.WeaponModId).Select(row => TableFields.U(row, 3)).FirstOrDefault();
            PowerSkillCsData? values = tables.GetTable<PowerSkillCsData>().FirstOrDefault(r => TableFields.U(r, 1) == skill * 100 + rank);
            int[] numbers = values is null ? [] : TableFields.List(values, 5).Select(v => (int)v).ToArray();
            if (numbers.Length == 0 && values is not null)
                numbers = [TableFields.I(values, 5)];
            return (skill, numbers);
        }
    }
}
