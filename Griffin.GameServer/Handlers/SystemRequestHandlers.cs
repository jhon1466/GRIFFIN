using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.GameServer.Services;

namespace Griffin.GameServer.Handlers
{
    /// <summary>Full currency list of the account (the same balances the login sends).</summary>
    public sealed class ResourceHandler(IResourceService resourceService) : ResourceHandlerBase
    {
        public override void HandleResource(CS_Resource request, Connection connection)
        {
            if (connection.Account is null)
                return;

            Resource resource = new();
            resourceService.ApplyTo(connection.Account.Uid, resource);
            connection.Send(new SC_Resource { Res = resource });
        }
    }

    /// <summary>The server time zone (UTC+8, the same value the login announces).</summary>
    public sealed class ZoneHandler : ZoneHandlerBase
    {
        public override void HandleTimeZone(CS_TimeZone request, Connection connection) =>
            connection.Send(new SC_TimeZone { OFLLLHEJEDO = 8 });
    }

    /// <summary>
    /// Gift codes: this private server has no code list, so every code answers 13200 ("the code does not exist or was typed
    /// wrong", <c>RedeemCodeHintData</c>) and 13206 when the length is not the usual 12 characters.
    /// </summary>
    public sealed class CdkRedeemHandler : CdkRedeemHandlerBase
    {
        public override void HandleCdkRedeem(CS_CdkRedeem request, Connection connection) =>
            connection.Send(new SC_CdkRedeem { RedeemResult = (request.CdkCode ?? string.Empty).Length == 12 ? 13200 : 13206 });
    }

    /// <summary>"I have seen it": the red dot of the given type is cleared (nothing is stored, the dots are rebuilt from the data).</summary>
    public sealed class RedDotReadHandler : RedDotHandlerBase
    {
        public override void HandleRedDotInfo(CS_RedDotInfo request, Connection connection) =>
            connection.Send(new SC_RedDotInfo());

        public override void HandleRedDotRead(CS_RedDotRead request, Connection connection) =>
            connection.Send(new SC_RedDotRead { Type = request.Type });
    }

    public sealed class PrefacePassHandler : PrefaceHandlerBase
    {
        public override void HandlePrefacePass(CS_PrefacePass request, Connection connection) =>
            connection.Send(new SC_PrefacePass { Preface = true });
    }

    /// <summary>A timer is refreshed to the next daily reset (05:00 UTC+8 = 21:00 UTC).</summary>
    public sealed class TimerRefreshHandler : TimerHandlerBase
    {
        public override void HandleTimers(CS_Timers request, Connection connection) => connection.Send(new SC_Timers());

        public override void HandleTimerRefresh(CS_TimerRefresh request, Connection connection)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset next = new(now.Year, now.Month, now.Day, 21, 0, 0, TimeSpan.Zero);
            if (next <= now)
                next = next.AddDays(1);

            connection.Send(new SC_TimerRefresh { Timer = new Griffin.Common.Proto.Timer { Id = request.Id, RefreshTime = next.ToUnixTimeSeconds() } });
        }
    }
}
