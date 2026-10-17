using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>
    /// Presentation groups (the story intros the client plays): the three fixed groups of the old capture plus the ones the
    /// account finished (<c>ppt.finished</c>). Finishing a group is acknowledged with the group marked done (the client sends it
    /// right after the first battle; an error there leaves the tutorial flow hanging); the rewards that come with some groups are
    /// not granted.
    /// </summary>
    public sealed class PptHandler(IAccountStateService accountState) : PptHandlerBase
    {
        private const string StateKey = "ppt.finished";

        public override void HandlePptInfo(CS_PptInfo request, Connection connection)
        {
            SC_PptInfo info = new() { BPKHDANAJGF = new KIANAFJAHJC() };
            // The opening presentation group otherwise leaves new accounts inside the same
            // onboarding path that the server cannot complete reliably.
            info.BPKHDANAJGF.IGIIGDJOPFH[3913] = true;
            foreach (uint group in new uint[] { 1032, 2601, 41109 })
                info.BPKHDANAJGF.IGIIGDJOPFH[group] = false;

            if (connection.Account is not null)
                foreach (uint group in accountState.Get<List<uint>>(connection.Account.Uid, StateKey) ?? [])
                    info.BPKHDANAJGF.IGIIGDJOPFH[group] = true;

            connection.Send(info);
        }

        public override void HandlePptFinish(CS_PptFinish request, Connection connection)
        {
            if (connection.Account is null)
                return;

            uint uid = connection.Account.Uid;
            List<uint> done = accountState.Get<List<uint>>(uid, StateKey) ?? [];
            SC_PptFinish response = new() { BPKHDANAJGF = new KIANAFJAHJC() };
            foreach (uint group in request.GroupIds.Distinct())
            {
                if (!done.Contains(group))
                    done.Add(group);

                response.BPKHDANAJGF.IGIIGDJOPFH[group] = true;
            }

            accountState.Set(uid, StateKey, done);
            connection.Send(response);
        }

        public override void HandlePptGetReward(CS_PptGetReward request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsPptGetReward, HNINDAOJCKP.ClientInputInvalid);
    }
}
