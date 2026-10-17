using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class DishBagHandler(ILobbyCookingService cooking) : DishBagHandlerBase
    {
        public override void HandleDishBagInfo(CS_DishBagInfo request, Connection connection)
        {
            if (connection.Account is null)
                return;

            connection.Send(new SC_DishBagInfo { Info = cooking.Bag(connection.Account.Uid) });
        }
    }
}
