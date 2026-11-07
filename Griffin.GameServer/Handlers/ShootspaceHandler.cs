using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// 火线突围 (Shootspace): the auto-shooter mini game of the event. There was no handler, so Info came back empty and Start was
    /// refused with ShootspaceNotOpen (-13880, "undefined:13880" in the client). The client (OPOMAFGLNKP) only needs Start echoed to
    /// launch the run; Info carries per mode the plays (field 1) and best score (field 2), the learned upgrades and the experience;
    /// Finish reports the run (field 3 experience, 4 best score, 5 this score, 8 plays). ASSUMPTION A-181: experience grows by the
    /// score of each run and upgrades are kept without a server-side cost check (the client checks its own requirements).
    /// </summary>
    public sealed class ShootspaceHandler(IAccountStateService accountState) : ShootspaceHandlerBase
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
                foreach ((uint key, uint value) in request.DPIODNMOADH)
                    reply.DPIODNMOADH[key] = value;
                connection.Send(reply);
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
                if (request.TalentId != 0 && !state.Talents.Contains(request.TalentId))
                    state.Talents.Add(request.TalentId);
                accountState.Set(uid, Key(request.ThemeId), state);

                SC_ShootspaceGetTalent reply = new() { ThemeId = request.ThemeId };
                reply.IMOHEBMLOBE.AddRange(state.Talents);
                connection.Send(reply);
            }
        }
    }
}
