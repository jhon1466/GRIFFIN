using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Protocol;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Read-only queries about content this server does not have (public announcements, leaderboards, other players, camp
    /// files, event galleries...): the answer is the empty counterpart message, with the identifiers of the request copied
    /// into the response so the client matches the answer. Nothing is stored and nothing is granted; the board lists these as
    /// <i>read-empty</i>. Only names on <see cref="Names"/> are answered here.
    /// </summary>
    public sealed class QueryResponder : IFallbackResponder
    {
        /// <summary>Requests (without the MsgCs prefix) that are plain queries with no content behind them.</summary>
        public static readonly HashSet<string> Names =
        [
            "Broadcast", "Npcs", "CampFiles", "ExGetThemesInfo", "LeaderBoard", "RealRanking", "InspirationOrders", "GetActivityGacha",
            "GetAvgRewards", "HeroModeMark", "LobbyGetScheme", "LobbyWeeklyFresh", "LobbyPartyGetAvgSelectionRate",
            "OtherUserDisplayBoard", "ThirdPartSocialBlackSync", "FriendRecommend", "FriendSearch", "EventEndlessTowerRankGlobal",
            "UnlockFinalMode", "GetGachaCounter", "GroupMailReceive",
        ];

        public bool TryRespond(Connection connection, BasePacket packet)
        {
            string name = packet.MsgId.ToString();
            if (!name.StartsWith("MsgCs", StringComparison.Ordinal) || !Names.Contains(name[5..]) || packet.Message is null)
                return false;

            if (!Enum.TryParse("MsgSc" + name[5..], out MsgId replyId) || BasePacket.CreateEmptyMessage(replyId) is not { } reply)
                return false;

            PreferenceResponder.Copy(packet.Message, reply);
            connection.SendAutoEncrypted<IMessage>(reply);
            Log.Information("QUERY {Request} answered with an empty {Reply}.", name, replyId);
            return true;
        }
    }
}
