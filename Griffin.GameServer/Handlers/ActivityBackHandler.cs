using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class ActivityBackHandler(IActivityBackService back) : ActivityBackHandlerBase
    {
        // empty, as the official server answers an account that is not a returning player (see LoginHandler)
        public override void HandleActivityBackInfo(CS_ActivityBackInfo request, Connection connection) =>
            connection.Send(new SC_ActivityBackInfo());

        // The paid track is sold for real money (store 7504), which this server never charges.
        public override void HandleActivityBackPaidCheckIn(CS_ActivityBackPaidCheckIn request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsActivityBackPaidCheckIn, HNINDAOJCKP.ClientInputInvalid);
    }
}
