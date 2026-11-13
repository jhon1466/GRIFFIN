using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;
using Griffin.GameServer.Extensions;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// 火线突围 (Shootspace): the auto-shooter mini game of the event. There was no handler, so Info came back empty and Start was
    /// refused with ShootspaceNotOpen (-13880, "undefined:13880" in the client). The client (OPOMAFGLNKP) only needs Start echoed to
    /// launch the run; Info carries per mode the plays (field 1) and best score (field 2), the learned upgrades and the experience;
    /// Finish reports the run (field 3 experience, 4 best score, 5 this score, 8 plays). Upgrades cost StspTalentData column 7 (item 176022);
    /// that item comes from the items the client reports at the end of each run.
    /// </summary>
    public sealed class ShootspaceHandler(IAccountStateService accountState, IRewardService rewardService, IItemUseService itemUse, Griffin.Common.Table.ITableService tableService) : ShootspaceHandlerBase
    {
        public sealed class Record
        {
            public uint Times { get; set; }
            public uint Best { get; set; }
        }

        public sealed class State
        {
            public Dictionary<uint, Record> Modes { get; set; } = [];
            public List<uint> Talents { get; set; } = [];
            public uint Exp { get; set; }
        }

        private static string Key(uint theme) => $"shootspace.{theme}";

        private State Load(uint uid, uint theme) => accountState.Get<State>(uid, Key(theme)) ?? new State();

        public override void HandleShootspaceInfo(CS_ShootspaceInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            State state = Load(connection.Account.Uid, request.ThemeId);
            SC_ShootspaceInfo reply = new() { ThemeId = request.ThemeId, DEGNALHNOMI = state.Exp };
            foreach ((uint mode, Record record) in state.Modes)
                reply.FDPOAJJJKGC[mode] = new CFGNAOGCNNF { Times = record.Times, CGFMBCCDFPG = record.Best };

            // Levels (StspDifficultyData of the theme's StspConfigData): the client opens a level only when its record says unlocked (field 3, read as
            // JIIBANDCNOM by CNLJNMBPGCC). Column 4 is the best score needed on the previous level (level 1 has none, 2 needs 200 on
            // level 1, 3 needs 500 on level 2). Without the flag every level showed the padlock, the first one included.
            foreach ((uint id, bool unlocked) in Levels(request.ThemeId, state))
            {
                if (!reply.FDPOAJJJKGC.TryGetValue(id, out CFGNAOGCNNF? entry))
                    reply.FDPOAJJJKGC[id] = entry = new CFGNAOGCNNF();
                entry.MPNGHAKGLDM = unlocked;
            }
            reply.IMOHEBMLOBE.AddRange(state.Talents);
            connection.Send(reply);
        }

        public override void HandleShootspaceStart(CS_ShootspaceStart request, Connection connection)
        {
            if (connection.Account is null)
                return;

            State state = Load(connection.Account.Uid, request.ThemeId);
            var levels = Levels(request.ThemeId, state);
            if (levels.Count == 0)
            {
                connection.Refuse(Griffin.Common.Protocol.MsgId.MsgCsShootspaceStart, HNINDAOJCKP.ShootspaceNotOpen);
                return;
            }
            if (!levels.Any(l => l.Id == request.PKIHMDBPBBD && l.Unlocked))
            {
                connection.Refuse(Griffin.Common.Protocol.MsgId.MsgCsShootspaceStart, HNINDAOJCKP.ShootspaceDifficultyIsLocked);
                return;
            }

            connection.Send(new SC_ShootspaceStart { ThemeId = request.ThemeId, PKIHMDBPBBD = request.PKIHMDBPBBD });
        }

        /// <summary>
        /// Levels of the theme's own configuration: ActivityModuleData[21004] gives the StspConfigData row (theme 901 -> 1, 4201 -> 9),
        /// whose column 5 lists its StspDifficultyData ids (1, 2, 3 or 901, 902, 903 ...). A level opens when column 4 is 0 or the
        /// previous level of that list has a best score of at least column 4 (2 needs 200 on level 1, 3 needs 500 on level 2).
        /// </summary>
        private List<(uint Id, bool Unlocked)> Levels(uint theme, State state)
        {
            uint config = ThemeModules.Config(tableService, theme, ThemeModules.ShootSpace);
            Google.Protobuf.IMessage? configRow = config == 0 ? null
                : tableService.GetTable<Griffin.Common.Table.StspConfigData>().FirstOrDefault(r => TableFields.U(r, 1) == config);
            if (configRow is null)
                return [];
            Dictionary<uint, Google.Protobuf.IMessage> difficulties = tableService.GetTable<Griffin.Common.Table.StspDifficultyData>()
                .GroupBy(r => TableFields.U(r, 1)).ToDictionary(g => g.Key, g => (Google.Protobuf.IMessage)g.First());
            List<(uint, bool)> levels = [];
            uint previous = 0;
            foreach (uint id in TableFields.List(configRow, 5))
            {
                if (id == 0 || !difficulties.TryGetValue(id, out Google.Protobuf.IMessage? row))
                    continue;
                uint needed = TableFields.U(row, 4);
                bool unlocked = needed == 0 || (previous != 0 && state.Modes.TryGetValue(previous, out Record? prev) && prev.Best >= needed);
                levels.Add((id, unlocked));
                previous = id;
            }
            return levels;
        }

        public override void HandleShootspaceFinish(CS_ShootspaceFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                if (!Levels(request.ThemeId, state).Any(l => l.Id == request.PKIHMDBPBBD && l.Unlocked))
                {
                    connection.Refuse(Griffin.Common.Protocol.MsgId.MsgCsShootspaceFinish, HNINDAOJCKP.ShootspaceDifficultyIsLocked);
                    return;
                }
                Record record = state.Modes.GetValueOrDefault(request.PKIHMDBPBBD) ?? new Record();
                record.Times++;
                record.Best = Math.Max(record.Best, request.Score);
                state.Modes[request.PKIHMDBPBBD] = record;
                state.Exp += request.Score;
                accountState.Set(uid, Key(request.ThemeId), state);

                SC_ShootspaceFinish reply = new()
                {
                    ThemeId = request.ThemeId,
                    PKIHMDBPBBD = request.PKIHMDBPBBD,
                    DEGNALHNOMI = state.Exp,
                    CGFMBCCDFPG = record.Best,
                    Score = request.Score,
                    Times = record.Times,
                };
                // the items the run earned (field 5, e.g. 176022 突围经验 that buys the upgrades) are granted for real
                List<Google.Protobuf.IMessage> messages = [];
                foreach ((uint key, uint value) in request.DPIODNMOADH)
                {
                    reply.DPIODNMOADH[key] = value;
                    if (key != 0 && value > 0)
                        rewardService.Grant(uid, key, (int)Math.Min(value, int.MaxValue), messages);
                }
                messages.Add(reply);
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }

        public override void HandleShootspaceGetTalent(CS_ShootspaceGetTalent request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
                List<Google.Protobuf.IMessage> messages = [];
                if (request.TalentId != 0 && !state.Talents.Contains(request.TalentId))
                {
                    // StspTalentData column 7 = price (item -> amount), e.g. 176022 x 180 for talent 10
                    Google.Protobuf.IMessage? row = tableService.GetTable<Griffin.Common.Table.StspTalentData>().FirstOrDefault(r => r.Id == request.TalentId);
                    if (row is null)
                    {
                        connection.Refuse(Griffin.Common.Protocol.MsgId.MsgCsShootspaceGetTalent, HNINDAOJCKP.ClientInputInvalid);
                        return;
                    }

                    Dictionary<uint, int> cost = TableFields.Map(row, 7).Where(p => p.Value > 0).ToDictionary(p => p.Key, p => (int)p.Value);
                    if (cost.Count > 0 && !itemUse.Spend(uid, cost, messages))
                    {
                        connection.Refuse(Griffin.Common.Protocol.MsgId.MsgCsShootspaceGetTalent, HNINDAOJCKP.ItemNotEnough);
                        return;
                    }

                    state.Talents.Add(request.TalentId);
                    accountState.Set(uid, Key(request.ThemeId), state);
                }

                SC_ShootspaceGetTalent reply = new() { ThemeId = request.ThemeId };
                reply.IMOHEBMLOBE.AddRange(state.Talents);
                messages.Add(reply);
                connection.SendAutoEncrypted(messages.ToArray());
            }
        }
    }
}
