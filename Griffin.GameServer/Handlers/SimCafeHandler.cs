using Griffin.Common.Networking;
using Griffin.Common.Proto;

namespace Griffin.GameServer.Handlers
{
    public sealed class SimCafeHandler : SimCafeHandlerBase
    {
        /// <summary>
        /// The client stores the warm-up info by theme and reads field 1 unconditionally (NIPDKHAAPIC.EOMJOLJPGDN). Only theme 3601
        /// was answered; for any other theme (the current event asks 4201) the reply came without field 1 and the client threw a
        /// NullReferenceException on every request. Every theme now gets the same fresh state the 3601 capture had.
        /// </summary>
        public override void HandleGetSimCafeWarmUpInfo(CS_GetSimCafeWarmUpInfo request, Connection connection)
        {
            connection.Send(new SC_GetSimCafeWarmUpInfo
            {
                ECOOFMAFCBG = new PIBACHAPBJH
                {
                    NCINIEDEDFG = 1,
                    OKGBJPCOMPA = { },
                    FNOAGJBJGGG = { },
                    Score = 0,
                    BPJMFIBFGKC = 1779500060L,
                },
                ThemeId = request.ThemeId,
            });
        }
    }
}
