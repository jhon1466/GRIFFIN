using Griffin.Common.Networking;
using Griffin.Common.Proto;
using Griffin.Common.Protocol;
using Serilog;

namespace Griffin.GameServer.Extensions
{
    /// <summary>
    /// None of the 遗珍回收 error codes (-14003 ... -14086) has a text in the client's <c>ErrorCodeHintData</c>/<c>ErrorcodeHintNewData</c>, so the client shows
    /// "undefined:NNNN" for them (the 14061 and 14069 reports of 2026-10-18). Every Rouge refusal is therefore sent with <c>ActivityMiniGameStatusInvalid</c>
    /// (-10085, the text «状态异常»), which has one; the exact reason stays in the server log. Codes that already have a text (item not enough) are kept (A-125).
    /// </summary>
    public static class RougeErrors
    {
        public static HNINDAOJCKP Visible(HNINDAOJCKP error) =>
            (int)error is <= -14000 and >= -14099 or <= -18400 and >= -18499 ? HNINDAOJCKP.ActivityMiniGameStatusInvalid : error;

        public static void RefuseRouge(this Connection connection, MsgId request, HNINDAOJCKP error)
        {
            if (Visible(error) != error)
                Log.Information("Rouge: {Request} refused: {Error} ({Code}); the client is sent {Shown}.", request, error, (int)error, Visible(error));
            connection.Refuse(request, Visible(error));
        }
    }
}
