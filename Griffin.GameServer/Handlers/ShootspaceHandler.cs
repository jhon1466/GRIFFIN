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

            // Levels (StspDifficultyData): the client opens a level only when its record says unlocked (field 3, read as
            // JIIBANDCNOM by CNLJNMBPGCC). Column 4 is the best score needed on the previous level (level 1 has none, 2 needs 200 on
            // level 1, 3 needs 500 on level 2). Without the flag every level showed the padlock, the first one included.
            foreach (Google.Protobuf.IMessage row in tableService.GetTable<Griffin.Common.Table.StspDifficultyData>())
            {
                uint id = TableFields.U(row, 1);
                if (id == 0)
                    continue;
                uint needed = TableFields.U(row, 4);
                bool unlocked = needed == 0 || (state.Modes.TryGetValue(id - 1, out Record? previous) && previous.Best >= needed);
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

            connection.Send(new SC_ShootspaceStart { ThemeId = request.ThemeId, PKIHMDBPBBD = request.PKIHMDBPBBD });
        }

        public override void HandleShootspaceFinish(CS_ShootspaceFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                State state = Load(uid, request.ThemeId);
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
