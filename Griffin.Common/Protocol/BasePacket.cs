using Google.Protobuf;
using System.Buffers.Binary;
using System.Reflection;

namespace Griffin.Common.Protocol
{
    public sealed record BasePacket
    {
        public ushort RawMsgId => Header.RawMsgId;
        public MsgId MsgId => Header.MsgId;
        public ushort IMessageLength => Header.IMessageLength;
        public ushort RawPacketLength => (ushort)(BasePacketHeader.HeaderLength + Header.IMessageLength);

        public BasePacketHeader Header { get; private set; }

        public IMessage? Message { get; private set; }

        /// <summary>
        /// Why <see cref="Message"/> is null for a packet read from the wire (id missing from <see cref="Protocol.MsgId"/>,
        /// no generated proto type, or a payload that does not parse). A client newer than the protos sends ids the
        /// server does not know; those must be dropped and logged, never take the connection down.
        /// </summary>
        public string? UnresolvedReason { get; private set; }

        public byte[] RawPacketData { get; private set; }

        public byte[] RawIMessageData => RawPacketData[BasePacketHeader.HeaderLength..];

        public BasePacket(ReadOnlySpan<byte> data)
        {
            if (data.Length < BasePacketHeader.HeaderLength)
            {
                throw new ArgumentException("Packet data must include a 4-byte header.", nameof(data));
            }

            this.Header = BasePacketHeader.DecodeFromBytes(data);

            if (data.Length < BasePacketHeader.HeaderLength + Header.IMessageLength)
            {
                throw new ArgumentException("Packet data is shorter than its declared length.", nameof(data));
            }

            int packetByteLength = BasePacketHeader.HeaderLength + Header.IMessageLength;
            this.RawPacketData = data.Slice(0, packetByteLength).ToArray();
            this.Message = ResolveMessage();
        }

        public static BasePacket FromIMessage(IMessage message)
        {
            MsgIdResolver.TryGetRawMsgId(message, out ushort rawMsgId);
            
            return new BasePacket(rawMsgId, message);
        }

        /// <summary>Creates an empty (all defaults) instance of the proto type behind <paramref name="msgId"/>, or null.</summary>
        public static IMessage? CreateEmptyMessage(MsgId msgId)
        {
            if (!MsgIdResolver.TryResolve((ushort)(int)msgId, out _, out string protoName, out string? translated))
            {
                return null;
            }

            string[] candidates = translated is not null ? [protoName, translated] : [protoName];
            Type? type = ResolveProtoType(GetProtoAssembly(), candidates);
            return type is null ? null : Activator.CreateInstance(type) as IMessage;
        }

        public static BasePacket FromIMessageBytes(ushort rawMsgId, byte[] rawIMessage)
        {
            return new BasePacket(rawMsgId, rawIMessage);
        }

        public BasePacket(ushort msgId, IMessage message)
        {
            ushort messageLen = checked((ushort)message.CalculateSize());

            this.Header = new BasePacketHeader(msgId, messageLen);
            this.Message = message;
            this.RawPacketData = GC.AllocateUninitializedArray<byte>(BasePacketHeader.HeaderLength + messageLen);
            BinaryPrimitives.WriteUInt16LittleEndian(this.RawPacketData, msgId);
            BinaryPrimitives.WriteUInt16LittleEndian(this.RawPacketData.AsSpan(2), messageLen);
            message.WriteTo(this.RawPacketData.AsSpan(BasePacketHeader.HeaderLength));
        }

        public BasePacket(ushort msgId, byte[] rawIMessage)
        {
            if (rawIMessage is null)
            {
                throw new ArgumentNullException(nameof(rawIMessage));
            }

            ushort messageLen = checked((ushort)rawIMessage.Length);

            this.Header = new BasePacketHeader(msgId, messageLen);
            this.RawPacketData = GC.AllocateUninitializedArray<byte>(BasePacketHeader.HeaderLength + messageLen);
            BinaryPrimitives.WriteUInt16LittleEndian(this.RawPacketData, msgId);
            BinaryPrimitives.WriteUInt16LittleEndian(this.RawPacketData.AsSpan(2), messageLen);
            rawIMessage.CopyTo(this.RawPacketData.AsSpan(BasePacketHeader.HeaderLength));
            this.Message = ResolveMessage();
        }

        private IMessage? ResolveMessage()
        {
            try
            {
                return ResolveMessageCore();
            }
            catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or InvalidProtocolBufferException)
            {
                UnresolvedReason = ex.Message;
                return null;
            }
        }

        private IMessage ResolveMessageCore()
        {
            if (!MsgIdResolver.TryResolve(
                    Header.RawMsgId,
                    out MsgId _,
                    out string protoName,
                    out string? translatedProtoClassName
                ))
            {
                throw new InvalidOperationException(
                    $"Unknown raw msg id {Header.RawMsgId} (not in MsgId; the client is probably newer than the protos).");
            }

            string[] candidateClassNames = translatedProtoClassName is not null
                ? [protoName, translatedProtoClassName]
                : [protoName];

            Assembly protoAssembly = GetProtoAssembly();
            Type? targetType = ResolveProtoType(protoAssembly, candidateClassNames);
            if (targetType is null)
            {
                throw new InvalidDataException(
                    $"Type not found in assembly '{protoAssembly.GetName().Name}'. Tried: {string.Join(", ", candidateClassNames)}."
                );
            }

            PropertyInfo? parserProperty = targetType.GetProperty("Parser", BindingFlags.Static | BindingFlags.Public);
            if (parserProperty is null)
            {
                throw new InvalidOperationException($"Type {targetType.FullName} does not expose a public static Parser property.");
            }

            object? parserInstance = parserProperty.GetValue(null);
            if (parserInstance is null)
            {
                throw new InvalidOperationException($"Type {targetType.FullName} returned a null Parser instance.");
            }

            MethodInfo? parseFromMethod = parserInstance
                .GetType()
                .GetMethod("ParseFrom", [typeof(byte[])]);

            if (parseFromMethod is null)
            {
                throw new InvalidOperationException($"Parser for {targetType.FullName} does not expose ParseFrom(byte[]).");
            }

            IMessage? parsedMessage = parseFromMethod.Invoke(parserInstance, [this.RawIMessageData]) as IMessage;
            if (parsedMessage is null)
            {
                throw new InvalidOperationException($"Parser for {targetType.FullName} returned null.");
            }

            return parsedMessage;
        }

        private static Assembly GetProtoAssembly()
        {
            const string protoAssemblyName = "Griffin.Common.Proto";
            Assembly? loaded = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, protoAssemblyName, StringComparison.Ordinal));
            if (loaded is not null)
            {
                return loaded;
            }

            try
            {
                return Assembly.Load(new AssemblyName(protoAssemblyName));
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not load required assembly '{protoAssemblyName}'. Ensure it is referenced by the host project.",
                    ex
                );
            }
        }

        private static Type? ResolveProtoType(Assembly protoAssembly, IEnumerable<string> candidateClassNames)
        {
            const string protoNamespace = "Griffin.Common.Proto";
            foreach (string candidate in candidateClassNames.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct())
            {
                string[] fullNames = [candidate, $"{protoNamespace}.{candidate}"];
                foreach (string fullName in fullNames)
                {
                    Type? type = protoAssembly.GetType(fullName, throwOnError: false, ignoreCase: false);
                    if (type is not null)
                    {
                        return type;
                    }
                }
            }

            return null;
        }
    }

    public readonly record struct BasePacketHeader(ushort RawMsgId, ushort IMessageLength)
    {
        public const int HeaderLength = 4;

        public MsgId MsgId => MsgIdResolver.GetMsgId(RawMsgId);

        public static BasePacketHeader DecodeFromBytes(ReadOnlySpan<byte> headerBytes)
        {
            ushort rawMsgId = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes[..2]);
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes.Slice(2, 2));

            return new BasePacketHeader(rawMsgId, length);
        }
    }
}
