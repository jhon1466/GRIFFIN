using System.Text;

namespace Griffin.GameServer.Services.Darkov
{
    /// <summary>
    /// A protobuf message read without a schema: the Darkov room protocol (ids 26012-26015) is a tree of nested messages whose generated types do not describe it
    /// (A-145), so the room is built and read field by field on the wire format.
    /// </summary>
    public sealed class PbMsg
    {
        private readonly Dictionary<int, List<(ulong Num, byte[]? Bytes)>> fields = [];

        /// <summary>The bytes this message was parsed from.</summary>
        public byte[] Original { get; private set; } = [];

        public static PbMsg Parse(ReadOnlySpan<byte> data)
        {
            var message = new PbMsg { Original = data.ToArray() };
            int i = 0;
            while (i < data.Length)
            {
                ulong tag = ReadVarint(data, ref i);
                int number = (int)(tag >> 3);
                switch (tag & 7)
                {
                    case 0:
                        message.Add(number, ReadVarint(data, ref i), null);
                        break;
                    case 1:
                        message.Add(number, BitConverter.ToUInt64(data.Slice(i, 8)), null);
                        i += 8;
                        break;
                    case 2:
                    {
                        int length = (int)ReadVarint(data, ref i);
                        message.Add(number, 0, data.Slice(i, length).ToArray());
                        i += length;
                        break;
                    }
                    case 5:
                        message.Add(number, BitConverter.ToUInt32(data.Slice(i, 4)), null);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("unsupported wire type");
                }
            }

            return message;
        }

        public static ulong ReadVarint(ReadOnlySpan<byte> data, ref int i)
        {
            ulong result = 0;
            int shift = 0;
            while (true)
            {
                byte b = data[i++];
                result |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                    return result;
                shift += 7;
            }
        }

        private void Add(int number, ulong num, byte[]? bytes)
        {
            if (!fields.TryGetValue(number, out var list))
                fields[number] = list = [];
            list.Add((num, bytes));
        }

        public bool Has(int number) => fields.ContainsKey(number);

        public IEnumerable<int> Numbers => fields.Keys;

        public ulong U(int number) => fields.TryGetValue(number, out var list) ? list[0].Num : 0;

        public long I(int number) => (long)U(number);

        public byte[]? Raw(int number) => fields.TryGetValue(number, out var list) ? list[0].Bytes : null;

        public string Str(int number) => Raw(number) is { } raw ? Encoding.UTF8.GetString(raw) : "";

        public PbMsg? Msg(int number) => Raw(number) is { } raw ? Parse(raw) : null;

        public IEnumerable<PbMsg> Msgs(int number) =>
            fields.TryGetValue(number, out var list) ? list.Where(v => v.Bytes is not null).Select(v => Parse(v.Bytes!)) : [];

        public IEnumerable<byte[]> RawList(int number) =>
            fields.TryGetValue(number, out var list) ? list.Where(v => v.Bytes is not null).Select(v => v.Bytes!) : [];

        /// <summary>A repeated numeric field, plain or packed.</summary>
        public List<ulong> Nums(int number)
        {
            var values = new List<ulong>();
            if (!fields.TryGetValue(number, out var list))
                return values;
            foreach ((ulong num, byte[]? bytes) in list)
            {
                if (bytes is null)
                    values.Add(num);
                else
                {
                    int i = 0;
                    while (i < bytes.Length)
                        values.Add(ReadVarint(bytes, ref i));
                }
            }

            return values;
        }
    }

    /// <summary>Writes a protobuf message field by field; fields appear in the order they are added (the client does not care about the order).</summary>
    public sealed class PbW
    {
        private readonly MemoryStream stream = new();

        public PbW V(int number, ulong value)
        {
            WriteVarint((ulong)number << 3);
            WriteVarint(value);
            return this;
        }

        /// <summary>A signed value; negatives are written as 64-bit two's complement (the form the client uses for coordinates).</summary>
        public PbW S(int number, long value) => V(number, (ulong)value);

        public PbW B(int number, ReadOnlySpan<byte> value)
        {
            WriteVarint(((ulong)number << 3) | 2);
            WriteVarint((ulong)value.Length);
            stream.Write(value);
            return this;
        }

        public PbW Str(int number, string value) => B(number, Encoding.UTF8.GetBytes(value));

        public PbW M(int number, PbW message) => B(number, message.ToArray());

        public PbW Packed(int number, IEnumerable<ulong> values)
        {
            var inner = new PbW();
            foreach (ulong value in values)
                inner.WriteVarint(value);
            return B(number, inner.ToArray());
        }

        private void WriteVarint(ulong value)
        {
            while (value >= 0x80)
            {
                stream.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }

            stream.WriteByte((byte)value);
        }

        public byte[] ToArray() => stream.ToArray();

        public bool IsEmpty => stream.Length == 0;
    }
}
