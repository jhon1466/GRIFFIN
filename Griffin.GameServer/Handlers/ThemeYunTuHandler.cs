using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Table;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// YunTu auto chess of the events (CS_ThemeYunTu*, client 4.0.5142 data manager IMAAGPBINJC). There was no handler: every request
    /// threw and the chapter list stayed empty. What the client reads from the server:
    /// - ADJMEGKGDBF field 5 = ChessMainData id (client default 101), found through ActivityModuleData[24002] of the theme (the client
    ///   reads PlanData from the same row with MHDIFOJLILD[24002]); ChessMainData column 4 = ChessChapterByGroupData group = chapter list.
    /// - field 2 = chapter records keyed by ChessChapterData id {id, count, best score (field 3)}. The client unlocks a chapter itself
    ///   from the PlanData open time + column 7 minutes and its column 6 "previousChapter:score" against field 3 of that record.
    /// - field 1 = per card (ChessPlayerData id) record {id, selected skill (4), skill levels (6)}.
    /// - field 4 = the running room (DBDNHIGGEBB); the client runs the room itself and sends its state back (settle/finish).
    /// Rewards: ChessRewardData rows (2 = chapter, 3 = score needed, 4 = items) are not read by the client, so they are granted here once
    /// each when a finished run reaches their score.
    /// </summary>
    public sealed class ThemeYunTuHandler(IAccountStateService accountState, ITableService tableService, IRewardService rewardService) : ThemeYunTuHandlerBase
    {
        public sealed class Chapter
        {
            public uint Count { get; set; }
            public uint BestScore { get; set; }
        }

        public sealed class Card
        {
            public uint SelectedSkill { get; set; }
            public Dictionary<uint, uint> SkillLevels { get; set; } = [];
        }

        public sealed class State
        {
            public Dictionary<uint, Chapter> Chapters { get; set; } = [];
            public Dictionary<uint, Card> Cards { get; set; } = [];
            public List<uint> ClaimedRewards { get; set; } = [];
            public List<uint> Collection { get; set; } = [];
            /// <summary>The running room, protobuf bytes in base64 (null when no run is open).</summary>
            public string? Room { get; set; }
        }

        private static string Key(uint theme) => $"yuntu.{theme}";

        private State Load(uint uid, uint theme) => accountState.Get<State>(uid, Key(theme)) ?? new State();

        private void Save(uint uid, uint theme, State state) => accountState.Set(uid, Key(theme), state);

        private static DBDNHIGGEBB? Room(State state) =>
            string.IsNullOrEmpty(state.Room) ? null : DBDNHIGGEBB.Parser.ParseFrom(Convert.FromBase64String(state.Room));

        private static void SetRoom(State state, DBDNHIGGEBB? room) =>
            state.Room = room is null ? null : Convert.ToBase64String(room.ToByteArray());

        /// <summary>ChessMainData id of the theme (ActivityModuleData[24002]); 0 when the theme has no auto chess.</summary>
        private uint Config(uint theme) => ThemeModules.Config(tableService, theme, ThemeModules.YunTuChess);

        private ADJMEGKGDBF Data(uint theme, State state)
        {
            ADJMEGKGDBF data = new() { LOEHFANKMDM = Config(theme) };
            foreach ((uint id, Card card) in state.Cards)
            {
                ADJMEGKGDBF.Types.KFMEBKMJAKI entry = new() { Id = id, JHJCBBCFOCI = card.SelectedSkill };
                foreach ((uint skill, uint level) in card.SkillLevels)
                    entry.PAFDNBAHKOI[skill] = level;
                data.KIAPMEBNELL[id] = entry;
            }

            foreach ((uint id, Chapter chapter) in state.Chapters)
                data.DIPHLGDEIED[id] = new ADJMEGKGDBF.Types.MPELHAJCOFN { Id = id, Count = chapter.Count, JDMNPHFLLLL = chapter.BestScore };
            data.KJEDFCCLLCN.AddRange(state.Collection);
            DBDNHIGGEBB? room = Room(state);
            if (room is not null)
                data.RoomInfo = room;
            return data;
        }

        /// <summary>Chapters of the theme: ChessMainData column 4 -> ChessChapterData column 3.</summary>
        private bool IsChapter(uint theme, uint stage)
        {
            uint config = Config(theme);
            var main = tableService.GetTable<ChessMainData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
            if (main is null)
                return false;
            uint group = TableFields.U(main, 4);
            return tableService.GetTable<ChessChapterData>().Any(r => TableFields.U(r, 1) == stage && TableFields.U(r, 3) == group);
        }

        public override void HandleThemeYunTuInfo(CS_ThemeYunTuInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;
            State state = Load(connection.Account.Uid, request.ThemeId);
            connection.Send(new SC_ThemeYunTuInfo { ThemeId = request.ThemeId, Data = Data(request.ThemeId, state) });
        }

        public override void HandleThemeYunTuStageStart(CS_ThemeYunTuStageStart request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            if (!IsChapter(request.ThemeId, request.StageId))
            {
                connection.Refuse(MsgId.MsgCsThemeYunTuStageStart, HNINDAOJCKP.YunTuChessRoomInfoMismatch);
                return;
            }

            // ASSUMPTION: a new run starts from an empty room (theme + chapter); the client fills cards, gold and level while it plays
            // and reports them back with settle/finish. Starting a run replaces an unfinished one.
            DBDNHIGGEBB room = new() { ThemeId = request.ThemeId, StageId = request.StageId };
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                SetRoom(state, room);
                Save(uid, request.ThemeId, state);
            }

            connection.Send(new SC_ThemeYunTuStageStart { ThemeId = request.ThemeId, StageId = request.StageId, RoomInfo = room });
        }

        /// <summary>Stores the room state the client reports (it plays the room locally).</summary>
        private DBDNHIGGEBB? Store(uint uid, uint theme, DBDNHIGGEBB? reported)
        {
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, theme);
                DBDNHIGGEBB? room = Room(state);
                if (reported is not null && (room is null || reported.StageId == room.StageId))
                {
                    room = reported.Clone();
                    room.ThemeId = theme;
                    SetRoom(state, room);
                    Save(uid, theme, state);
                }
                return room;
            }
        }

        public override void HandleThemeYunTuStageRoomSettle(CS_ThemeYunTuStageRoomSettle request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint theme = request.RoomInfo?.ThemeId ?? 0;
            DBDNHIGGEBB? room = Store(connection.Account.Uid, theme, request.RoomInfo);
            connection.Send(new SC_ThemeYunTuStageRoomSettle { RoomInfo = room ?? request.RoomInfo ?? new DBDNHIGGEBB() });
        }

        public override void HandleThemeYunTuStageRoomRestart(CS_ThemeYunTuStageRoomRestart request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            // ASSUMPTION: a restart begins the same chapter again from an empty room, as StageStart does.
            DBDNHIGGEBB room = new() { ThemeId = request.ThemeId, StageId = request.StageId };
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                SetRoom(state, room);
                Save(uid, request.ThemeId, state);
            }
            connection.Send(new SC_ThemeYunTuStageRoomRestart { RoomInfo = room });
        }

        public override void HandleThemeYunTuStageFinish(CS_ThemeYunTuStageFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<IMessage> messages = [];
            SC_ThemeYunTuStageFinish reply = new() { ThemeId = request.ThemeId };
            using (RewardDrops drops = RewardDrops.Begin())
            {
                lock (AccountLock.For(uid))
                {
                    State state = Load(uid, request.ThemeId);
                    DBDNHIGGEBB? room = request.RoomInfo ?? Room(state);
                    uint stage = request.StageId != 0 ? request.StageId : room?.StageId ?? 0;
                    // the score field of the request is deprecated: the run's score is in the room state (DBDNHIGGEBB field 13)
                    uint score = room?.Score ?? 0;
                    // ASSUMPTION: HOOEPGMCLMF set means the run was abandoned; it is counted but neither scored nor rewarded.
                    bool abandoned = request.HOOEPGMCLMF;
                    if (stage != 0 && IsChapter(request.ThemeId, stage))
                    {
                        Chapter chapter = state.Chapters.GetValueOrDefault(stage) ?? new Chapter();
                        chapter.Count++;
                        if (!abandoned)
                            chapter.BestScore = Math.Max(chapter.BestScore, score);
                        state.Chapters[stage] = chapter;

                        if (!abandoned)
                        {
                            foreach (IMessage row in tableService.GetTable<ChessRewardData>()
                                         .Where(r => TableFields.U(r, 2) == stage && TableFields.U(r, 3) <= chapter.BestScore)
                                         .OrderBy(r => TableFields.U(r, 1)))
                            {
                                uint id = TableFields.U(row, 1);
                                if (state.ClaimedRewards.Contains(id))
                                    continue;
                                state.ClaimedRewards.Add(id);
                                // ASSUMPTION: NAHFOBJDAAN of the reply lists the ChessRewardData ids reached by this run.
                                reply.NAHFOBJDAAN.Add(id);
                                foreach ((uint item, uint count) in TableFields.Map(row, 4))
                                {
                                    if (item == 0 || count == 0)
                                        continue;
                                    rewardService.Grant(uid, item, (int)count, messages);
                                    reply.Rewards[item] = reply.Rewards.GetValueOrDefault(item) + count;
                                }
                            }
                        }
                    }

                    SetRoom(state, null);
                    Save(uid, request.ThemeId, state);
                    reply.Data = Data(request.ThemeId, state);
                }
                drops.Finish(messages);
            }
            messages.Add(reply);
            connection.SendAutoEncrypted(messages.ToArray());
        }

        public override void HandleThemeYunTuStageLeave(CS_ThemeYunTuStageLeave request, Connection connection)
        {
            if (connection.Account is null)
                return;
            // ASSUMPTION: leaving keeps the room so the run can be resumed (the client shows "room in progress" while field 4 is set);
            // only Finish or a new Start closes it.
            connection.Send(new SC_ThemeYunTuStageLeave { ThemeId = request.ThemeId });
        }

        public override void HandleThemeYunTuSkillLvUp(CS_ThemeYunTuSkillLvUp request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            // ChessSkillLevelData: 3 = skill, 4 = level. The highest level listed for the skill is its maximum.
            List<uint> levels = tableService.GetTable<ChessSkillLevelData>().Where(r => TableFields.U(r, 3) == request.JJOKKNFDAGE)
                .Select(r => TableFields.U(r, 4)).ToList();
            if (levels.Count == 0)
            {
                connection.Refuse(MsgId.MsgCsThemeYunTuSkillLvUp, HNINDAOJCKP.YunTuChessSkillLevelGroupNotFound);
                return;
            }

            uint level;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                Card card = state.Cards.GetValueOrDefault(request.NDEHHNKLFLJ) ?? new Card();
                // ASSUMPTION: a skill starts at level 1 (the client builds the attack skill at 1 and the others at 0 before any data).
                uint current = card.SkillLevels.GetValueOrDefault(request.JJOKKNFDAGE);
                if (current >= levels.Max())
                {
                    connection.Refuse(MsgId.MsgCsThemeYunTuSkillLvUp, HNINDAOJCKP.YunTuChessSkillLvMax);
                    return;
                }
                // ASSUMPTION: ChessSkillLevelData has no cost column in this build, so a level up costs nothing.
                level = current + 1;
                card.SkillLevels[request.JJOKKNFDAGE] = level;
                state.Cards[request.NDEHHNKLFLJ] = card;
                Save(uid, request.ThemeId, state);
            }

            connection.Send(new SC_ThemeYunTuSkillLvUp
            {
                ThemeId = request.ThemeId,
                JLHFICPMAEI = level,
                NDEHHNKLFLJ = request.NDEHHNKLFLJ,
                JJOKKNFDAGE = request.JJOKKNFDAGE,
            });
        }

        public override void HandleThemeYunTuSelectSkill(CS_ThemeYunTuSelectSkill request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            // the selected skill must be a ChessSkillData row (the client reads it with MJDPGKAPJPL.GetDataById)
            if (!tableService.GetTable<ChessSkillData>().Any(r => TableFields.U(r, 1) == request.ALHAKIMMDIF))
            {
                connection.Refuse(MsgId.MsgCsThemeYunTuSelectSkill, HNINDAOJCKP.YunTuChessSkillNotFound);
                return;
            }

            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                Card card = state.Cards.GetValueOrDefault(request.NDEHHNKLFLJ) ?? new Card();
                card.SelectedSkill = request.ALHAKIMMDIF;
                state.Cards[request.NDEHHNKLFLJ] = card;
                Save(uid, request.ThemeId, state);
            }

            connection.Send(new SC_ThemeYunTuSelectSkill { ThemeId = request.ThemeId, NDEHHNKLFLJ = request.NDEHHNKLFLJ, ALHAKIMMDIF = request.ALHAKIMMDIF });
        }

        public override void HandleThemeYunTuStageSelectAth(CS_ThemeYunTuStageSelectAth request, Connection connection)
        {
            if (connection.Account is null)
                return;
            uint uid = connection.Account.Uid;
            List<uint> collection;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                // ASSUMPTION: the reply list is ADJMEGKGDBF field 7 (same name KJEDFCCLLCN), the ids picked so far; the pick is added.
                if (request.IOJLOIGOEDB != 0 && !state.Collection.Contains(request.IOJLOIGOEDB))
                    state.Collection.Add(request.IOJLOIGOEDB);
                Save(uid, request.ThemeId, state);
                collection = [.. state.Collection];
            }
            SC_ThemeYunTuStageSelectAth reply = new() { ThemeId = request.ThemeId };
            reply.KJEDFCCLLCN.AddRange(collection);
            connection.Send(reply);
        }

        // The room is played by the client; the following requests are accepted and echoed (ASSUMPTION: the official server validates
        // gold and the store pool, whose rules are not in the client).

        public override void HandleThemeYunTuStageRoomBattleStart(CS_ThemeYunTuStageRoomBattleStart request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_ThemeYunTuStageRoomBattleStart { ThemeId = request.ThemeId });
        }

        public override void HandleThemeYunTuStageFunctionRefresh(CS_ThemeYunTuStageFunctionRefresh request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_ThemeYunTuStageFunctionRefresh { ThemeId = request.ThemeId, StageId = request.StageId });
        }

        public override void HandleThemeYunTuRefreshStoreBuy(CS_ThemeYunTuRefreshStoreBuy request, Connection connection)
        {
            if (connection.Account is not null)
                connection.Send(new SC_ThemeYunTuRefreshStoreBuy
                {
                    ThemeId = request.ThemeId,
                    StageId = request.StageId,
                    GJPONLPNJNG = request.GJPONLPNJNG,
                    ALFOOKAFOIA = request.ALFOOKAFOIA,
                });
        }

        public override void HandleThemeYunTuStoreBuy(CS_ThemeYunTuStoreBuy request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SC_ThemeYunTuStoreBuy reply = new() { ThemeId = request.ThemeId, StageId = request.StageId, GJPONLPNJNG = request.GJPONLPNJNG };
            foreach ((uint key, uint value) in request.NPNMMKFDMDP)
                reply.LHLLCBJLMOE[key] = value;
            connection.Send(reply);
        }

        public override void HandleThemeYunTuLineUp(CS_ThemeYunTuLineUp request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SC_ThemeYunTuLineUp reply = new() { ThemeId = request.ThemeId, StageId = request.StageId };
            reply.LBODLHLGADD.AddRange(request.LBODLHLGADD);
            connection.Send(reply);
        }

        public override void HandleThemeYunTuCardOperation(CS_ThemeYunTuCardOperation request, Connection connection)
        {
            if (connection.Account is null)
                return;
            SC_ThemeYunTuCardOperation reply = new() { ThemeId = request.ThemeId, StageId = request.StageId };
            reply.CPGHLLIOPHA.AddRange(request.CPGHLLIOPHA);
            connection.Send(reply);
        }
    }
}
