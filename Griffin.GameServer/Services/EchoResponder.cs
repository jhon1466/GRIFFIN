using System.Text.RegularExpressions;
using Google.Protobuf;
using Griffin.Common.Networking;
using Griffin.Common.Packets;
using Griffin.Common.Protocol;
using Serilog;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Requests without a handler whose response only repeats fields of the request (a selection, a toggle, a "mark as read",
    /// an unlock flag...): the last request of each kind is stored per account (<c>echo</c> in <c>AccountStates</c>) and the
    /// same-named, same-typed fields are sent back. The server does not apply any further effect, which is why the board lists
    /// them as <i>echo-only</i>. Requests whose name says they grant, charge, settle or fight (Reward, Claim, Buy, Settle,
    /// Gacha, Raid, ...) are never answered this way: an invented success would hide the missing feature or duplicate rewards.
    /// </summary>
    public sealed class EchoResponder(IAccountStateService accountState) : IFallbackResponder
    {
        private const string StateKey = "echo";

        private static readonly Regex ValueWords = new(
            "Reward|Claim|Buy|Purchase|Exchange|CheckIn|Lottery|Gacha|Draw|Consume|Compose|Dismantle|Decompose|Sell|Recycle|Withdraw|Take|Gain|Receive|Pick|Settle|Raid|Pay|Finish|Start|Fight|Battle|Rank|Upgrade|LvUp|LevelUp|Cook|Water|Dig|Invite|Apply|Join|Leave|Kick|Disband|Create|Redeem|Dispatch",
            RegexOptions.Compiled);

        private static readonly Dictionary<string, bool> Eligible = [];

        public bool TryRespond(Connection connection, BasePacket packet)
        {
            string name = packet.MsgId.ToString();
            if (!name.StartsWith("MsgCs", StringComparison.Ordinal) || name.Length <= 5 || packet.Message is null)
                return false;

            string suffix = name[5..];
            if (!IsEligible(suffix, packet.Message, out IMessage? reply))
                return false;

            if (connection.Account is not null)
            {
                uint uid = connection.Account.Uid;
                Dictionary<string, string> stored = accountState.Get<Dictionary<string, string>>(uid, StateKey) ?? [];
                stored[suffix] = Convert.ToBase64String(packet.Message.ToByteArray());
                accountState.Set(uid, StateKey, stored);
            }

            PreferenceResponder.Copy(packet.Message, reply!);
            connection.SendAutoEncrypted(reply!);
            Log.Information("ECHO-ONLY {Request} stored and echoed; no further effect.", name);
            return true;
        }

        /// <summary>Whether the request (by its message name) can be answered with an echo; builds the empty reply.</summary>
        private static bool IsEligible(string suffix, IMessage request, out IMessage? reply)
        {
            reply = null;
            if (ValueWords.IsMatch(suffix))
                return false;

            if (!Enum.TryParse("MsgSc" + suffix, out MsgId replyId) || replyId == MsgId.MsgNone)
                return false;

            lock (Eligible)
            {
                if (Eligible.TryGetValue(suffix, out bool known) && !known)
                    return false;

                reply = BasePacket.CreateEmptyMessage(replyId);
                if (reply is null)
                {
                    Eligible[suffix] = false;
                    return false;
                }

                if (known)
                    return true;

                // every field of the response must exist in the request with the same name, type and cardinality, and the
                // response must have at least one field (empty responses are plain acknowledgements: AckResponder)
                var responseFields = reply.Descriptor.Fields.InFieldNumberOrder();
                bool ok = responseFields.Count > 0 && responseFields.All(target =>
                    request.Descriptor.Fields.InFieldNumberOrder().Any(source =>
                        source.Name == target.Name
                        && source.FieldType == target.FieldType
                        && source.IsRepeated == target.IsRepeated
                        && source.IsMap == target.IsMap));
                Eligible[suffix] = ok;
                if (!ok)
                    reply = null;
                return ok;
            }
        }
    }
}
