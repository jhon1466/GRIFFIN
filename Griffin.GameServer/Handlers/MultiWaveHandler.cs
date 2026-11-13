using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.Common.Table;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// MultiWave defense mode (UIMultiDefensePanel, client 4.0.5142 class HEADJKFNOAA). The client opens it for module 20002 of theme 601
    /// and asks <c>CS_MultiWaveInfo</c>; the reply's map (field 2) is MultiWaveData id -> best score, and every MultiWaveData row of the config
    /// (MultiWaveByActivityIdData 1001) is expected in it ("前后端数据不匹配" otherwise). A wave is played as an ordinary stage
    /// (MultiWaveData column 5 = StageData id) whose <c>StageOther.score</c> carries {theme, wave id}; the settlement reads
    /// <c>SC_StageResult.stage_other.score</c> {theme, point, wave id} and the previous best that <c>SC_MultiWaveInfoUpdate</c> leaves behind.
    /// Grades: MultiWaveRewardByRewardGroupIdData (MultiWaveData column 7) -> MultiWaveRewardData rows (col 3 grade, col 4 min score,
    /// col 6 "item:count;" reward).
    /// </summary>
    public sealed class MultiWaveHandler(IAccountStateService accountState, ITableService tableService) : MultiWaveHandlerBase
    {
        /// <summary>The client's own constants (HEADJKFNOAA): theme 601 and config 1001, used when the theme tables do not resolve it.</summary>
        private const uint ClientTheme = 601;
        private const uint ClientConfig = 1001;

        private static string Key(uint theme) => $"multiwave.{theme}";

        private static uint ConfigOf(ITableService tables, uint theme)
        {
            uint config = ThemeModules.Config(tables, theme, ThemeModules.MultiWave);
            return config != 0 ? config : theme == ClientTheme ? ClientConfig : 0;
        }

        private static IEnumerable<IMessage> Waves(ITableService tables, uint config) =>
            tables.GetTable<MultiWaveData>().Where(r => TableFields.U(r, 2) == config);

        public override void HandleMultiWaveInfo(CS_MultiWaveInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            var best = accountState.Get<Dictionary<uint, uint>>(connection.Account.Uid, Key(request.ThemeId)) ?? [];
            var reply = new SC_MultiWaveInfo { ThemeId = request.ThemeId };
            foreach (IMessage wave in Waves(tableService, ConfigOf(tableService, request.ThemeId)))
            {
                uint id = TableFields.U(wave, 1);
                reply.BPJANAFAHDO[id] = best.GetValueOrDefault(id);
            }
            connection.Send(reply);
        }

        /// <summary>
        /// Called by the stage result: when the stage is a MultiWave wave, keeps the best score, pays every grade the best score reaches for
        /// the first time and fills <c>stage_other.score</c> for the settlement screen.
        /// ASSUMPTION: the score of the run is <c>StageProgress.points</c> (as the other score stages) and it counts on a lost run too
        /// (a defense run ends when the base falls). ASSUMPTION: each grade reward (MultiWaveRewardData col 6) is granted once, when the best
        /// score first reaches its min score (col 4); the client has no claim request for them.
        /// </summary>
        internal static void OnStageResult(ITableService tables, IAccountStateService accountState, IRewardService rewards, uint uid, uint stageId, uint points,
            SC_StageResult response, List<IMessage> push)
        {
            IMessage? wave = tables.GetTable<MultiWaveData>().FirstOrDefault(r => TableFields.U(r, 5) == stageId);
            if (wave is null)
                return;

            uint config = TableFields.U(wave, 2);
            uint waveId = TableFields.U(wave, 1);
            // the theme of the wave: the ActivityEntranceData row whose module 20002 is this config, else the client's 601
            uint theme = tables.GetTable<ActivityEntranceData>().Select(r => TableFields.U(r, 1))
                .FirstOrDefault(t => ThemeModules.Config(tables, t, ThemeModules.MultiWave) == config);
            if (theme == 0)
                theme = ClientTheme;

            uint previous, best;
            lock (AccountLock.For(uid))
            {
                var all = accountState.Get<Dictionary<uint, uint>>(uid, Key(theme)) ?? [];
                previous = all.GetValueOrDefault(waveId);
                best = Math.Max(previous, points);
                all[waveId] = best;
                accountState.Set(uid, Key(theme), all);
            }

            uint group = TableFields.U(wave, 7);
            foreach (IMessage grade in tables.GetTable<MultiWaveRewardData>().Where(r => TableFields.U(r, 2) == group))
            {
                uint min = TableFields.U(grade, 4);
                if (min > best || min <= previous && previous != 0)
                    continue;
                foreach (string entry in TableFields.S(grade, 6).Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = entry.Split(':');
                    if (parts.Length < 2 || !uint.TryParse(parts[0], out uint item) || !int.TryParse(parts[1], out int count) || count <= 0)
                        continue;
                    rewards.Grant(uid, item, count, push);
                    response.OtherDrop.Add(new UserDropCache { ItemId = item, ItemNum = (uint)count });
                }
            }

            response.StageOther ??= new StageOther();
            response.StageOther.Score = new ICIJCEGPLCG { ThemeId = theme, Point = points, IHNPODPFPAD = waveId };
            // the client keeps the best it knew as the "history" score of the settlement, then stores the new best
            push.Insert(0, new SC_MultiWaveInfoUpdate { ThemeId = theme, StageId = waveId, Point = best });
        }
    }

    /// <summary>
    /// The dungeon activity: every message is deprecated in the 4.0.5142 protocol, the client has no code that sends them and the tables
    /// have no dungeon rows, so there is no dungeon to list or enter. The info requests answer empty; entering is refused.
    /// </summary>
    public sealed class DungeonActivityHandler : DungeonActivityHandlerBase
    {
        public override void HandleDungeonActivityInfo(CS_DungeonActivityInfo request, Connection connection) =>
            connection.Send(new SC_DungeonActivityInfo());
    }

    public sealed class DungeonHandler : DungeonHandlerBase
    {
        public override void HandleGetDungeonInfo(CS_GetDungeonInfo request, Connection connection) =>
            connection.Send(new SC_GetDungeonInfo());

        public override void HandleEnterDungeon(CS_EnterDungeon request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsEnterDungeon, HNINDAOJCKP.CmdIdIsBanned);
    }

    public sealed class LeaveDungeonHandler : LeaveDungeonHandlerBase
    {
        public override void HandleLeaveDungeon(CS_LeaveDungeon request, Connection connection) =>
            connection.Send(new SC_LeaveDungeon());
    }
}
