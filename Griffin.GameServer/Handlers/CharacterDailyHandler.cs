using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>The dormitory behaviours the account bought with <c>BuyGunPerformance</c> (id → true).</summary>
    public sealed class CharacterDailyHandler(IAccountStateService accountState) : CharacterDailyHandlerBase
    {
        public override void HandleGetCharacterDailyInfo(CS_GetCharacterDailyInfo request, Connection connection)
        {
            SC_GetCharacterDailyInfo reply = new();
            if (connection.Account is not null)
                foreach ((uint id, bool bought) in accountState.Get<Dictionary<uint, bool>>(connection.Account.Uid, GunHandler.PerformancesKey) ?? [])
                    reply.GGMFOMHIBNJ[id] = bought;
            connection.Send(reply);
        }
    }
}
