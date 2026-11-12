using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.Common.Table;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// RichMan board game of the events (module 21003, theme 901 -> RichmanConfigData 1001). As in the client data manager (client 4.0.5142):
    /// the config row's season (column 4) is a MissionBySeasonData row whose column 2 lists the stages (MissionData: 1 id, 4 type, 2 = endless,
    /// 5 minutes after the PlanData start of the module (ActivityModuleData column 10) before the stage opens, 6 stage that must be passed
    /// first). SC_RichManData carries per stage: stage id, best score (the client ranks the endless stage by it), whether a game is being
    /// played on it, and a passed counter (field 4; the client opens the next stage when it is above 0).
    /// The board itself (dice, events, shops, buildings, opponents) is simulated by the official server through RichManCmd; that engine is
    /// not implemented here, so StartGame is refused (the client cannot draw a board without a full room) and no game ever exists.
    /// </summary>
    public sealed class RichManHandler(IAccountStateService accountState, ITableService tableService) : RichManHandlerBase
    {
        public sealed class Record
        {
            public int BestScore { get; set; }
            public int Passed { get; set; }
        }

        private static string Key(uint theme) => $"richman.{theme}";

        private Dictionary<uint, Record> Load(uint uid, uint theme) => accountState.Get<Dictionary<uint, Record>>(uid, Key(theme)) ?? [];

        /// <summary>The MissionData rows of the theme, in the order of MissionBySeasonData.</summary>
        private List<Google.Protobuf.IMessage> Stages(uint theme)
        {
            uint config = ThemeModules.Config(tableService, theme, ThemeModules.RichMan);
            var configRow = tableService.GetTable<RichmanConfigData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
            if (configRow is null)
                return [];
            uint season = TableFields.U(configRow, 4);
            var bySeason = tableService.GetTable<MissionBySeasonData>().FirstOrDefault(r => TableFields.U(r, 1) == season);
            if (bySeason is null)
                return [];
            Dictionary<uint, Google.Protobuf.IMessage> missions = new();
            foreach (Google.Protobuf.IMessage row in tableService.GetTable<MissionData>())
                missions.TryAdd(TableFields.U(row, 1), row);
            return TableFields.List(bySeason, 2).Where(missions.ContainsKey).Select(id => missions[id]).ToList();
        }

        /// <summary>Unix start of the PlanData window of the module in this theme, or 0.</summary>
        private long PlanStart(uint theme)
        {
            var entrance = tableService.GetTable<ActivityEntranceData>().FirstOrDefault(r => TableFields.U(r, 1) == theme);
            if (entrance is null)
                return 0;
            uint moduleRow = TableFields.U(entrance, 2);
            var module = tableService.GetTable<ActivityModuleData>().FirstOrDefault(r => TableFields.U(r, 1) == moduleRow);
            if (module is null)
                return 0;
            uint plan = TableFields.Map(module, 10).Where(p => p.Key == ThemeModules.RichMan).Select(p => p.Value).FirstOrDefault();
            var planRow = tableService.GetTable<PlanData>().FirstOrDefault(r => TableFields.U(r, 1) == plan);
            return planRow is null ? 0 : (long)TableFields.U64(planRow, 4);
        }

        private SC_RichManData Data(uint uid, uint theme)
        {
            Dictionary<uint, Record> records = Load(uid, theme);
            KMOMJFOFEDD.Types.IGCLEIEPMLE data = new();
            // every stage of the season is sent; the client locks them itself (previous stage passed and opening time)
            foreach (Google.Protobuf.IMessage row in Stages(theme))
            {
                uint stage = TableFields.U(row, 1);
                records.TryGetValue(stage, out Record? record);
                data.DIPHLGDEIED[stage] = new KMOMJFOFEDD.Types.GEFEEBGPPCM
                {
                    StageId = stage,
                    MaxScore = record?.BestScore ?? 0,
                    KNFOFFBOCML = false, // no game is ever in progress (see the class summary)
                    PGANMDGDEAP = record?.Passed ?? 0,
                };
            }

            return new SC_RichManData { ThemeId = theme, Data = data };
        }

        public override void HandleRichManData(CS_RichManData request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(Data(connection.Account.Uid, request.ThemeId));
        }

        public override void HandleRichManStartGame(CS_RichManStartGame request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            Google.Protobuf.IMessage? stage = Stages(request.ThemeId).FirstOrDefault(r => TableFields.U(r, 1) == request.StageId);
            if (stage is null)
            {
                connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.ClientInputInvalid);
                return;
            }

            uint previous = TableFields.U(stage, 6);
            if (previous != 0 && !(Load(uid, request.ThemeId).TryGetValue(previous, out Record? before) && before.Passed > 0))
            {
                connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.RichManStagePreStageNotPass);
                return;
            }

            // ASSUMPTION: the opening time counts from the PlanData start in the table, as the client computes it (start + minutes * 60)
            long opens = PlanStart(request.ThemeId) + TableFields.U(stage, 5) * 60L;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < opens)
            {
                connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.RichManStageTimeLock);
                return;
            }

            // the board engine of the official server (RichManCmd simulation) is not implemented: a room without players, dice and map
            // would leave the client on a broken board, so the game cannot be started
            connection.Refuse(MsgId.MsgCsRichManStartGame, HNINDAOJCKP.RichManGameNotExist);
        }

        public override void HandleRichManResume(CS_RichManResume request, Connection connection)
        {
            // no game is ever in progress (StartGame is refused)
            if (connection.Account is not null)
                connection.Refuse(MsgId.MsgCsRichManResume, HNINDAOJCKP.RichManGameNotExist);
        }

        public override void HandleRichManSettleGame(CS_RichManSettleGame request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Refuse(MsgId.MsgCsRichManSettleGame, HNINDAOJCKP.RichManGameNotExist);
        }

        public override void HandleRichManCmd(CS_RichManCmd request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Refuse(MsgId.MsgCsRichManCmd, HNINDAOJCKP.RichManGameNotExist);
        }

        public override void HandleRichManRank(CS_RichManRank request, Connection connection)
        {
            // a local server has no other players to rank against; the client adds the own endless score to the list itself
            if (connection.Account is not null)
                connection.Send(new SC_RichManRank { ThemeId = request.ThemeId });
        }
    }
}
