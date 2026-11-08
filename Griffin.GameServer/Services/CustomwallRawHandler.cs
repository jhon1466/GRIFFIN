using Griffin.Common.Networking;
using Griffin.Common.Packets;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Custom walls of the lobby (4.0.5142 client, messages unknown to the protos of this server): 29054 asks for every wall and expects
    /// 29055 = {1: {map wall id → wall}}; 29056 saves one wall {1: id, 2: wall} and expects 29057 with the same body. They were answered
    /// empty, so what was put on a wall was gone after a restart. Each wall is kept per account as the bytes the client sent.
    /// </summary>
    public sealed class CustomwallRawHandler(IAccountStateService accountState) : IRawPacketHandler
    {
        private const ushort GetInfo = 29054, GetInfoReply = 29055, Set = 29056, SetReply = 29057;
        private const string Key = "lobby.customwall";

        public bool TryHandle(Connection connection, ushort rawMsgId, byte[] payload)
        {
            if (rawMsgId is not (GetInfo or Set) || connection.Account is null)
                return false;

            uint uid = connection.Account.Uid;
            if (rawMsgId == Set)
            {
                (uint id, byte[]? wall) = ReadSet(payload);
                lock (AccountLock.For(uid))
                {
                    Dictionary<uint, string> walls = accountState.Get<Dictionary<uint, string>>(uid, Key) ?? [];
                    walls[id] = Convert.ToBase64String(wall ?? []);
                    accountState.Set(uid, Key, walls);
                }

                connection.SendRawPacket(SetReply, payload);
                return true;
            }

            Dictionary<uint, string> saved = accountState.Get<Dictionary<uint, string>>(uid, Key) ?? [];
            using MemoryStream map = new();
            foreach ((uint id, string wall) in saved)
            {
                byte[] value = Convert.FromBase64String(wall);
                using MemoryStream entry = new();
                entry.WriteByte(0x08);
                WriteVarint(entry, id);
                entry.WriteByte(0x12);
                WriteVarint(entry, (ulong)value.Length);
                entry.Write(value);
                map.WriteByte(0x0A);
                WriteVarint(map, (ulong)entry.Length);
                entry.WriteTo(map);
            }

            using MemoryStream reply = new();
            if (map.Length > 0)
            {
                reply.WriteByte(0x0A);
                WriteVarint(reply, (ulong)map.Length);
                map.WriteTo(reply);
            }

            connection.SendRawPacket(GetInfoReply, reply.ToArray());
            return true;
        }

        private static (uint Id, byte[]? Wall) ReadSet(byte[] b)
        {
            uint id = 0;
            byte[]? wall = null;
            int i = 0;
            while (i < b.Length)
            {
                ulong tag = ReadVarint(b, ref i);
                switch (tag)
                {
                    case 0x08:
                        id = (uint)ReadVarint(b, ref i);
                        break;
                    case 0x12:
                        int len = (int)ReadVarint(b, ref i);
                        wall = b[i..(i + len)];
                        i += len;
                        break;
                    default:
                        // anything else: skip by wire type
                        switch ((int)(tag & 7))
                        {
                            case 0: ReadVarint(b, ref i); break;
                            case 1: i += 8; break;
                            case 5: i += 4; break;
                            case 2: int l = (int)ReadVarint(b, ref i); i += l; break;
                            default: return (id, wall);
                        }
                        break;
                }
            }

            return (id, wall);
        }

        private static ulong ReadVarint(byte[] b, ref int i)
        {
            ulong value = 0;
            for (int shift = 0; i < b.Length && shift < 64; shift += 7)
            {
                byte c = b[i++];
                value |= (ulong)(c & 0x7F) << shift;
                if ((c & 0x80) == 0)
                    break;
            }

            return value;
        }

        private static void WriteVarint(Stream s, ulong v)
        {
            do
            {
                byte c = (byte)(v & 0x7F);
                v >>= 7;
                s.WriteByte(v != 0 ? (byte)(c | 0x80) : c);
            } while (v != 0);
        }
    }
}
