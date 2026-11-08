using Griffin.Common.Networking;
using Griffin.Common.Packets;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// The furniture of room 110 (历战连廊) in the 4.0.5142 client, whose messages the protos of this server do not have:
    /// <list type="bullet">
    /// <item>辉策战服 (badge uniform): 29780 info → 29781 {1: {1: map slot→badge}}; 29782 pins {1: map} → 29783 with the same body.</item>
    /// <item>遗珍陈列台 (relic showcase): 29760 info → 29761 {1: {1: season, 2: map season→wall, 3: unlocked relics}}; 29762 {1: season},
    /// 29764 {1: season, 2: wall} and 29766 {1: relic} are answered with the same body (29763, 29765, 29767).</item>
    /// </list>
    /// Pinning a badge got no answer at all (the request is not empty), so the screen froze, and nothing was kept. Everything is stored per
    /// account now.
    /// </summary>
    public sealed class LobbyHallRawHandler(IAccountStateService accountState) : IRawPacketHandler
    {
        public sealed class State
        {
            public Dictionary<uint, uint> Badges { get; set; } = [];
            public uint Season { get; set; }
            public Dictionary<uint, uint> Walls { get; set; } = [];
            public List<uint> Relics { get; set; } = [];
        }

        private const string Key = "lobby.hall";

        public bool TryHandle(Connection connection, ushort rawMsgId, byte[] payload)
        {
            if (rawMsgId is not (29780 or 29782 or 29760 or 29762 or 29764 or 29766) || connection.Account is null)
                return false;

            uint uid = connection.Account.Uid;
            lock (AccountLock.For(uid))
            {
                State state = accountState.Get<State>(uid, Key) ?? new State();
                switch (rawMsgId)
                {
                    case 29780:
                        connection.SendRawPacket(29781, Wrap(1, MapBytes(1, state.Badges)));
                        return true;
                    case 29782:
                        state.Badges = ReadMap(payload, 1);
                        accountState.Set(uid, Key, state);
                        connection.SendRawPacket(29783, payload);
                        return true;
                    case 29760:
                        connection.SendRawPacket(29761, Wrap(1, Showcase(state)));
                        return true;
                    case 29762:
                        state.Season = ReadUInt(payload, 1);
                        break;
                    case 29764:
                        state.Walls[ReadUInt(payload, 1)] = ReadUInt(payload, 2);
                        break;
                    case 29766:
                        uint relic = ReadUInt(payload, 1);
                        if (relic != 0 && !state.Relics.Contains(relic))
                            state.Relics.Add(relic);
                        break;
                }

                accountState.Set(uid, Key, state);
                connection.SendRawPacket((ushort)(rawMsgId + 1), payload);
                return true;
            }
        }

        private static byte[] Showcase(State state)
        {
            using MemoryStream body = new();
            if (state.Season != 0)
            {
                body.WriteByte(0x08);
                WriteVarint(body, state.Season);
            }

            body.Write(MapBytes(2, state.Walls));
            if (state.Relics.Count > 0)
            {
                using MemoryStream packed = new();
                foreach (uint relic in state.Relics)
                    WriteVarint(packed, relic);
                body.WriteByte(0x1A);
                WriteVarint(body, (ulong)packed.Length);
                packed.WriteTo(body);
            }

            return body.ToArray();
        }

        /// <summary>A map&lt;uint32, uint32&gt; field: one length-delimited {1: key, 2: value} entry per pair.</summary>
        private static byte[] MapBytes(int field, Dictionary<uint, uint> map)
        {
            using MemoryStream s = new();
            foreach ((uint k, uint v) in map)
            {
                using MemoryStream entry = new();
                entry.WriteByte(0x08);
                WriteVarint(entry, k);
                entry.WriteByte(0x10);
                WriteVarint(entry, v);
                s.WriteByte((byte)(field << 3 | 2));
                WriteVarint(s, (ulong)entry.Length);
                entry.WriteTo(s);
            }

            return s.ToArray();
        }

        private static byte[] Wrap(int field, byte[] inner)
        {
            using MemoryStream s = new();
            s.WriteByte((byte)(field << 3 | 2));
            WriteVarint(s, (ulong)inner.Length);
            s.Write(inner);
            return s.ToArray();
        }

        private static Dictionary<uint, uint> ReadMap(byte[] b, int field)
        {
            Dictionary<uint, uint> map = [];
            int i = 0;
            while (i < b.Length)
            {
                ulong tag = ReadVarint(b, ref i);
                int wire = (int)(tag & 7);
                if ((int)(tag >> 3) == field && wire == 2)
                {
                    int len = (int)ReadVarint(b, ref i);
                    int end = i + len;
                    uint k = 0, v = 0;
                    while (i < end)
                    {
                        ulong t = ReadVarint(b, ref i);
                        ulong value = ReadVarint(b, ref i);
                        if (t == 0x08) k = (uint)value;
                        else if (t == 0x10) v = (uint)value;
                    }
                    map[k] = v;
                }
                else if (!Skip(b, ref i, wire))
                    break;
            }

            return map;
        }

        private static uint ReadUInt(byte[] b, int field)
        {
            int i = 0;
            while (i < b.Length)
            {
                ulong tag = ReadVarint(b, ref i);
                int wire = (int)(tag & 7);
                if ((int)(tag >> 3) == field && wire == 0)
                    return (uint)ReadVarint(b, ref i);
                if (!Skip(b, ref i, wire))
                    break;
            }

            return 0;
        }

        private static bool Skip(byte[] b, ref int i, int wire)
        {
            switch (wire)
            {
                case 0: ReadVarint(b, ref i); return true;
                case 1: i += 8; return true;
                case 5: i += 4; return true;
                case 2: int len = (int)ReadVarint(b, ref i); i += len; return true;
                default: return false;
            }
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
