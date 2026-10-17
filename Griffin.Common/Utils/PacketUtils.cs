using Microsoft.Extensions.Logging;
using Griffin.Common.Protocol;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Griffin.Common.Utils
{
    public static class PacketUtils
    {
        public static void LogBasePackets(
            string packetVerb,
            string mainMessageVerb,
            IReadOnlyList<BasePacket> packets,
            ushort sequenceId,
            byte encryptionFlag,
            byte[] rawMainMessage,
            ILogger logger)
        {
            string sendMode = sequenceId == 0 ? "unsequenced" : "sequenced";

            foreach (BasePacket packet in packets)
            {
                bool authenticationPacket = IsAuthenticationPacket(packet);
                MsgIdResolver.TryResolve(
                    packet.RawMsgId,
                    out MsgId resolvedMsgId,
                    out string resolvedProtoName,
                    out string? resolvedTranslatedClassName
                );

                logger.LogInformation(
                    "{PacketVerb} {SendMode} packet raw msg id {RawMsgId}, resolved msg id {ResolvedMsgIdName} ({ResolvedMsgIdEnumValue}), proto {ResolvedProtoName}, translated proto class {ResolvedTranslatedProtoClassName}, payload length {PayloadLength}, raw length {RawLength}",
                    packetVerb,
                    sendMode,
                    packet.RawMsgId,
                    resolvedMsgId.ToString(),
                    (int)resolvedMsgId,
                    resolvedProtoName,
                    resolvedTranslatedClassName ?? "<missing>",
                    packet.RawIMessageData.Length,
                    packet.RawPacketData.Length
                );
                if (authenticationPacket)
                {
                    logger.LogInformation("{PacketVerb} authentication packet payload redacted", packetVerb);
                }
                else
                {
                    logger.LogInformation("{PacketVerb} packet raw bytes: {Bytes}", packetVerb, HexUtils.FormatBytes(packet.RawPacketData));
                    logger.LogInformation("{PacketVerb} packet payload bytes: {Bytes}", packetVerb, HexUtils.FormatBytes(packet.RawIMessageData));
                }
            }

            logger.LogInformation(
                "{MainMessageVerb} {SendMode} main message seq {SequenceId} with {PacketCount} packet(s), isEncrypted {IsEncrypted}, encryptedByte {EncryptedByte}, total bytes {TotalBytes}",
                mainMessageVerb,
                sendMode,
                sequenceId,
                packets.Count,
                encryptionFlag > 0,
                encryptionFlag,
                rawMainMessage.Length
            );
            if (packets.Any(IsAuthenticationPacket))
                logger.LogInformation("{MainMessageVerb} main message bytes redacted (contains authentication packet)", mainMessageVerb);
            else
                logger.LogInformation("{MainMessageVerb} main message bytes: {Bytes}", mainMessageVerb, HexUtils.FormatBytes(rawMainMessage));
            logger.LogInformation(
                "{MainMessageVerb} IMessage(s):{NewLine}{Messages}",
                mainMessageVerb,
                Environment.NewLine,
                FormatBasePackets(packets)
            );
        }

        private static string FormatBasePackets(IReadOnlyList<BasePacket> packets)
        {
            return string.Join(
                Environment.NewLine,
                packets.Select(packet =>
                    IsAuthenticationPacket(packet)
                        ? $"{packet.MsgId}: <authentication payload redacted>"
                        : packet.Message is not null
                        ? $"{packet.MsgId}: {packet.Message}"
                        : $"{packet.MsgId} (raw {packet.RawMsgId}): <unparsed> {packet.UnresolvedReason ?? "unknown parse error"}"
                )
            );
        }

        private static bool IsAuthenticationPacket(BasePacket packet) =>
            packet.MsgId is MsgId.MsgCsLogin or MsgId.MsgScLogin;

        private static string FormatOutgoingMessages(MainMessage message)
        {
            return FormatBasePackets(message.Packets);
        }
    }
}
