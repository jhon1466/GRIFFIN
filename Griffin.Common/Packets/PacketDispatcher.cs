using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Griffin.Common.Networking;
using Griffin.Common.Protocol;

namespace Griffin.Common.Packets
{
    public sealed class PacketDispatcher(
            IServiceProvider rootProvider,
            PacketHandlerRegistry registry,
            ILogger<PacketDispatcher> logger)
    {
        private readonly ConcurrentDictionary<Type, MessageParser> _parsers = new(); // TODO: Parser cache

        // Fallback for read-only requests the server does not implement: reply with the empty counterpart message so the
        // client does not wait forever on that screen. Requests that change state (buy, claim, start battle...) are never
        // answered this way, because an invented success would hide the missing feature or duplicate rewards.
        private static readonly Regex ReadOnlyName = new(
            @"^(Get|Query|Fetch|Check|Search|View|Load|Read|List|Info|Detail|Find|Has|Is|Can|Need|Require|Preview|Show|Sync|Refresh|Update|Download|Upload|Share|Send|Apply|Submit|Request|Pull|Push|Ack|Notify|Report|Probe|Ping|Heart|KeepAlive)|(Fetch|Info|Infos|List|Lists|Data|Record|Records|Status|Count|Counts|Rank|Ranks|Detail|Details|Brief|Overview|Teams|Plan|Plans|Config|State|Summary|Statistics)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // An action verb at the start of the name (BuyItem, StartStage, SetTeam...) or a word that grants/consumes value
        // anywhere in it (Reward, Claim, CheckIn, Gacha...) marks a request that changes state.
        private static readonly Regex StateChangingName = new(
            @"^(Set|Buy|Claim|Take|Receive|Settle|Finish|Start|Enter|Exit|Quit|Upgrade|Change|Select|Use|Unlock|Equip|Dismantle|Compose|Create|Delete|Remove|Send|Join|Apply|Accept|Refuse|Mark|Refresh|Reset|Save|Cancel|Confirm|Submit|Fight|Add|Del|Exchange|Sign|Upload|Report|Bind|Kick|Invite|Transfer|Convert|Sell|Dispatch)|Reward|Claim|Receive|Settle|Buy|Purchase|CheckIn|Lottery|Gacha|Draw|Exchange|Consume|Unlock",
            RegexOptions.Compiled);

        /// <summary>True for a request that is not a plain query (the ones the dispatcher answers with the "function locked" error when nothing else answers).</summary>
        public static bool IsActionRequest(string suffix) => !ReadOnlyName.IsMatch(suffix) || StateChangingName.IsMatch(suffix);

        /// <summary><c>HNINDAOJCKP.UserSystemLocked</c>: the client shows its "function locked" hint.</summary>
        private const int UserSystemLockedError = -10018;
        private const int ClientInputInvalidError = -10008;

        private static readonly ConcurrentDictionary<string, bool> LoggedFallbacks = new();

        public void Dispatch(Connection connection, MainMessage message)
        {
            foreach (BasePacket packet in message.Packets)
            {
                this.Dispatch(connection, packet);
            }
        }

        public void Dispatch(Connection connection, BasePacket packet)
        {
            if (packet.Message is null)
            {
                byte[] payload = packet.RawIMessageData;
                logger.LogWarning(
                    "Dropping unparseable packet raw id {RawMsgId} ({Length} bytes, payload {Payload}): {Reason}",
                    packet.RawMsgId,
                    payload.Length,
                    Convert.ToHexString(payload.AsSpan(0, Math.Min(payload.Length, 64))),
                    packet.UnresolvedReason ?? "no message");

                // Unknown id with an empty body looks like a parameterless query from a newer client. Responses follow
                // their request id + 1 in 985 of the 1020 known pairs, so answer with an empty packet to unblock the screen.
                if (payload.Length == 0 && packet.MsgId == MsgId.MsgNone && packet.RawMsgId < ushort.MaxValue)
                {
                    ushort replyId = (ushort)(packet.RawMsgId + 1);
                    LogFallbackOnce($"raw:{packet.RawMsgId}",
                        "AUTO-EMPTY raw reply for unknown request {Request} -> id {Reply} (guess: request id + 1)",
                        packet.RawMsgId, replyId);
                    connection.SendRawPacket(replyId, RawReplyTemplates.For(replyId));
                }

                return;
            }

            if (!registry.TryGet(packet.Header.MsgId, out var entry))
            {
                logger.LogWarning("No handler for {MsgId}; packet dropped", packet.Header.MsgId);
                TryAutoReply(connection, packet, "no handler");
                return;
            }

            using var scope = rootProvider.CreateScope();
            var handler = scope.ServiceProvider.GetRequiredService(entry.Type);

            int sentBefore = connection.SentMessageCount;
            try
            {
                IMessage? request = packet.Message;
                object? result = entry.Method.Invoke(handler, new object?[] { request, connection });

                // A handler with an empty body leaves the client waiting on that screen. For read-only requests answer
                // with the empty counterpart message (same rules as for unimplemented handlers).
                if (connection.SentMessageCount == sentBefore)
                {
                    TryAutoReply(connection, packet, "handler sent nothing");
                }

                connection.SendKept();

                // If the handler maps to a reply MsgId, frame its returned message and send it.
                //if (entry.ReplyMsgId != MsgId.MsgNone && result is IMessage reply)
                //{
                //    connection.Send(new BasePacket(reply.ToByteArray()));
                //}
            }
            catch (TargetInvocationException ex) when (ex.InnerException is NotImplementedException notImplemented)
            {
                logger.LogWarning(
                    "Handler {Type}.{Method} was called for {MsgId} but is not implemented",
                    entry.Type.Name, entry.Method.Name, packet.MsgId);
                TryAutoReply(connection, packet, "not implemented");
            }
            catch (NotImplementedException ex)
            {
                logger.LogWarning(ex,
                    "Handler {Type}.{Method} was called for {MsgId} but is not implemented",
                    entry.Type.Name, entry.Method.Name, packet.MsgId);
                TryAutoReply(connection, packet, "not implemented");
            }
            catch (Exception ex)
            {
                var actual = (ex as TargetInvocationException)?.InnerException ?? ex;
                logger.LogError(actual, "Handler {Type}.{Method} failed for {MsgId}",
                    entry.Type.Name, entry.Method.Name, packet.MsgId);

                // A request that has a reply and failed before answering would leave the client waiting for ever: answer with the client's
                // "invalid input" error (-10008) instead.
                string failedName = packet.MsgId.ToString();
                if (connection.SentMessageCount == sentBefore && failedName.StartsWith("MsgCs", StringComparison.Ordinal) && failedName.Length > 5
                    && Enum.TryParse("MsgSc" + failedName[5..], out MsgId failedReply) && failedReply != MsgId.MsgNone)
                {
                    connection.SendRawPacket(ReportErrorMsgId, ReportError((uint)(int)packet.MsgId, ClientInputInvalidError));
                }
            }
        }

        private void TryAutoReply(Connection connection, BasePacket packet, string reason)
        {
            // Registered responders (real behaviour for requests without a handler) come before the empty-reply rules,
            // except when a handler existed and deliberately sent nothing.
            if (reason != "handler sent nothing")
            {
                using IServiceScope scope = rootProvider.CreateScope();
                foreach (IFallbackResponder responder in scope.ServiceProvider.GetServices<IFallbackResponder>())
                {
                    try
                    {
                        if (responder.TryRespond(connection, packet))
                            return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "{Responder} failed for {MsgId}", responder.GetType().Name, packet.MsgId);
                    }
                }
            }

            string name = packet.MsgId.ToString();
            if (!name.StartsWith("MsgCs", StringComparison.Ordinal) || name.Length <= 5)
            {
                return;
            }

            string suffix = name[5..];
            if (!ReadOnlyName.IsMatch(suffix) || StateChangingName.IsMatch(suffix))
            {
                LogFallbackOnce($"skip:{name}",
                    "NOT auto-answered (not a plain read-only request): {Request}", name);

                // A request that is not a plain query (buy, claim, start, use, plant, cook...), that this server does not implement and that has a reply message, is
                // answered with the client's own "system locked" error instead of leaving the screen waiting for ever.
                if (Enum.TryParse("MsgSc" + suffix, out MsgId actionReply) && actionReply != MsgId.MsgNone)
                {
                    // A handler that exists but sent nothing rejected the input (an invalid or unknown id...): that is "invalid input", not a locked
                    // function. "Function locked" stays for requests that have no handler at all.
                    int refusal = reason == "handler sent nothing" ? ClientInputInvalidError : UserSystemLockedError;
                    connection.SendRawPacket(ReportErrorMsgId, ReportError((uint)(int)packet.MsgId, refusal));
                }

                return;
            }

            if (!Enum.TryParse("MsgSc" + suffix, out MsgId replyId) || replyId == MsgId.MsgNone)
            {
                LogFallbackOnce($"nosc:{name}", "NOT auto-answered (no MsgSc counterpart): {Request}", name);
                return;
            }

            IMessage? reply = BasePacket.CreateEmptyMessage(replyId);
            if (reply is null)
            {
                LogFallbackOnce($"notype:{name}", "NOT auto-answered (no proto type for {Reply}): {Request}", replyId, name);
                return;
            }

            LogFallbackOnce($"auto:{name}", "AUTO-EMPTY reply for {Request} -> {Reply} ({Reason})", name, replyId, reason);
            connection.SendAutoEncrypted<IMessage>(reply);
        }

        private const ushort ReportErrorMsgId = 19900;

        /// <summary>Payload of <c>SC_ReportError { protocol_id = 1; error_no = 2 (int32) }</c> built by hand (this project does not reference the generated protos).</summary>
        private static byte[] ReportError(uint protocolId, int errorNo)
        {
            List<byte> bytes = [0x08];
            WriteVarint(bytes, protocolId);
            bytes.Add(0x10);
            WriteVarint(bytes, unchecked((ulong)(long)errorNo));
            return bytes.ToArray();
        }

        private static void WriteVarint(List<byte> bytes, ulong value)
        {
            while (value >= 0x80)
            {
                bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }

            bytes.Add((byte)value);
        }

        private void LogFallbackOnce(string key, string template, params object[] args)
        {
            if (LoggedFallbacks.TryAdd(key, true))
            {
                logger.LogWarning(template, args);
            }
        }
    }
}
