using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Griffin.GameServer.Extensions;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class ActivityBackHandler(IActivityBackService back) : ActivityBackHandlerBase
    {
        public override void HandleActivityBackInfo(CS_ActivityBackInfo request, Connection connection)
        {
            CheckInState state = connection.Account is null ? new CheckInState() : back.State(connection.Account.Uid);
            connection.Send(new SC_ActivityBackInfo
            {
                Info = new IGHEKKLGNLA
                {
                    IAPDLJKLLNM = 1,
                    OpenTime = connection.GameClockSeconds() - 18800,
                    HNAKPBEGGFK = 3,
                    HPABIPEOBKN = connection.GameClockSeconds(),
                    IEHFGOMCBCN = false,
                    CJOINHJCPII = state.DaysClaimed,
                    CheckinDone = state.LastClaimDate == ActivityBackService.Today(),
                    Version = 2,
                    ANGHCINLCJO = new OCLANMMGEGA
                    {
                        Type = 0,
                        Level = 0,
                        LMNBPEBDCKG = { },
                        IMMBMDAPFEN = { },
                    },
                },
            });
        }

        // The paid track is sold for real money (store 7504), which this server never charges.
        public override void HandleActivityBackPaidCheckIn(CS_ActivityBackPaidCheckIn request, Connection connection) =>
            connection.Refuse(MsgId.MsgCsActivityBackPaidCheckIn, HNINDAOJCKP.ClientInputInvalid);
    }
}
