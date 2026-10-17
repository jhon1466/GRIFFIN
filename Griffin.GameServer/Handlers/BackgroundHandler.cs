using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    public sealed class BackgroundHandler(IBackgroundService backgroundService) : BackgroundHandlerBase
    {
        public override void HandleBackgroundInfo(CS_BackgroundInfo request, Connection connection)
        {
            // the stored choice (BackgroundChange); the default for an account that never changed it
            uint background = connection.Account is null ? 3056 : backgroundService.GetCurrentBackground(connection.Account.Uid);
            connection.Send(new SC_BackgroundInfo { Background = background });
        }

        public override void HandleBackgroundChange(CS_BackgroundChange request, Connection connection)
        {
            if (connection.Account == null) return;

            if (request.BackGround != 0)
                backgroundService.SetCurrentBackground(connection.Account.Uid, request.BackGround);

            connection.Send(new SC_BackgroundChange());
        }
    }
}
