using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>The background music chosen with <c>SetBgm</c> (stored by <see cref="PreferenceResponder"/>) is what <c>GetBgm</c> returns afterwards.</summary>
    public sealed class BgmHandler(IAccountStateService accountState) : BgmHandlerBase
    {
        public override void HandleGetBgm(CS_GetBgm request, Connection connection)
        {
            uint bgm = connection.Account is null ? 0
                : PreferenceResponder.Recall<CS_SetBgm>(accountState, connection.Account.Uid, "SetBgm")?.JNHHAINDALC ?? 0;
            connection.Send(new SC_GetBgm { JNHHAINDALC = bgm });
        }
    }
}
