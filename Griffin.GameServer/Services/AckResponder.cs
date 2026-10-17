using System.Text.RegularExpressions;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Protocol;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Pure acknowledgements: requests whose response message in the protocol is <b>empty</b> (no fields), so "OK" is the whole
    /// answer. The server keeps no state for them (that is why the board lists them as <i>ack-only</i>, not as implemented),
    /// but the client stops waiting. Anything that grants, charges or settles a reward is excluded by name, and so is any
    /// request with a handler of its own (responders only run when there is none).
    /// </summary>
    public sealed class AckResponder : IFallbackResponder
    {
        private static readonly Regex ValueWords = new(
            "Reward|Claim|Buy|Purchase|Exchange|CheckIn|Lottery|Gacha|Draw|Consume|Unlock|Compose|Dismantle|Decompose|Sell|Recycle|Withdraw|Take|Gain|Receive|Pick",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, bool> Cache = [];

        public bool TryRespond(Connection connection, BasePacket packet)
        {
            string name = packet.MsgId.ToString();
            if (!name.StartsWith("MsgCs", StringComparison.Ordinal) || name.Length <= 5 || ValueWords.IsMatch(name))
                return false;

            if (!IsEmptyAck(name[5..]))
                return false;

            if (!Enum.TryParse("MsgSc" + name[5..], out MsgId replyId) || BasePacket.CreateEmptyMessage(replyId) is not { } reply)
                return false;

            connection.SendAutoEncrypted<Google.Protobuf.IMessage>(reply);
            Log.Information("ACK-ONLY {Request} answered with an empty {Reply}; no state is kept for it.", name, replyId);
            return true;
        }

        /// <summary>True when the response message of the request has no fields at all.</summary>
        public static bool IsEmptyAck(string suffix)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(suffix, out bool known))
                    return known;

                bool empty = Enum.TryParse("MsgSc" + suffix, out MsgId replyId)
                    && BasePacket.CreateEmptyMessage(replyId) is { } message
                    && message.Descriptor.Fields.InDeclarationOrder().Count == 0;
                Cache[suffix] = empty;
                return empty;
            }
        }
    }
}
