using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class TitleHandler(IProfileService profile) : TitleHandlerBase
    {
        public override void HandleModTitle(CS_ModTitle request, Connection connection)
        {
            if (connection.Account is not null)
                profile.Update(connection.Account.Uid, state => state.Title = request.Title);

            connection.Send(new SC_ModTitle
            {
                Title = request.Title,
            });
        }
    }
}
