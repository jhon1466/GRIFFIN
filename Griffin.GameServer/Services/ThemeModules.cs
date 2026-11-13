using Griffin.Common.Table;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// How the client finds the configuration of an event mini game, as in client 4.0.5142 (TpsShoot data manager): the theme id of the
    /// request is an ActivityEntranceData row, its column 2 is an ActivityModuleData row, whose column 4 maps the module id of the mini game
    /// (21001 TpsShoot, ...) to the id of its own configuration row (TpsshootBaseData 101...). Column 10 maps the module to its PlanData window.
    /// Tables of the mini games are keyed by that configuration id, never by the theme id (A-213).
    /// </summary>
    public static class ThemeModules
    {
        public const uint TpsShoot = 21001;
        public const uint RichMan = 21003;
        public const uint KittyCafe2 = 22003;

        /// <summary>The configuration id of <paramref name="module"/> in theme <paramref name="themeId"/>, or 0.</summary>
        public static uint Config(ITableService tables, uint themeId, uint module)
        {
            var entrance = tables.GetTable<ActivityEntranceData>().FirstOrDefault(r => TableFields.U(r, 1) == themeId);
            if (entrance is null)
                return 0;
            uint moduleRow = TableFields.U(entrance, 2);
            var row = tables.GetTable<ActivityModuleData>().FirstOrDefault(r => TableFields.U(r, 1) == moduleRow);
            if (row is null) return 0;
            foreach ((uint key, uint value) in TableFields.Map(row, 4))
                if (key == module) return value;
            return 0;
        }
    }
}
