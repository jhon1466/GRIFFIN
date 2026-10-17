using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Griffin.GameServer.Services
{
    /// <summary>
    /// Reads a column of a client table row by its **field number**. The generated table types come from an older build and
    /// several of their field *names* no longer describe the data of the current tables (e.g. <c>GunWeaponData.rank</c> holds an
    /// icon name; the rank is field 11), so game logic reads by number after checking the real values.
    /// </summary>
    public static class TableFields
    {
        /// <summary>A scalar column; when the checked-in table type declares the column as a repeated field (older build), the first element is the value.</summary>
        private static object? Scalar(IMessage row, int number)
        {
            object? value = Value(row, number);
            if (value is System.Collections.IEnumerable list and not string)
                return list.Cast<object>().FirstOrDefault();
            return value;
        }

        public static uint U(IMessage row, int number) => Convert.ToUInt32(Scalar(row, number) ?? 0u);

        public static ulong U64(IMessage row, int number) => Convert.ToUInt64(Scalar(row, number) ?? 0UL);

        public static double Double(IMessage row, int number) =>
            Convert.ToDouble(Scalar(row, number) ?? 0.0, System.Globalization.CultureInfo.InvariantCulture);

        public static int I(IMessage row, int number) => Convert.ToInt32(Scalar(row, number) ?? 0);

        public static string S(IMessage row, int number) => Value(row, number)?.ToString() ?? string.Empty;

        /// <summary>
        /// A map&lt;uint32,uint32&gt; column as (key, value) pairs. Tables store maps in two ways: as a real protobuf map (one entry per
        /// pair) or as one message with two parallel lists (keys = field 1, values = field 2); both are read here.
        /// </summary>
        public static IReadOnlyList<(uint Key, uint Value)> Map(IMessage row, int number)
        {
            FieldDescriptor? field = row.Descriptor.FindFieldByNumber(number);
            if (field is null)
                return [];

            if (field.IsMap && field.Accessor.GetValue(row) is System.Collections.IDictionary dictionary)
            {
                List<(uint, uint)> pairs = [];
                foreach (System.Collections.DictionaryEntry entry in dictionary)
                    pairs.Add((Convert.ToUInt32(entry.Key), Convert.ToUInt32(entry.Value)));
                return pairs;
            }

            if (field.FieldType == FieldType.Message && field.IsRepeated && !field.IsMap && field.Accessor.GetValue(row) is System.Collections.IEnumerable entries)
            {
                // a list of {1: key, 2: value} messages (several rewards)
                List<(uint, uint)> listed = [];
                foreach (object entry in entries)
                    if (entry is IMessage pair)
                        listed.Add((U(pair, 1), U(pair, 2)));
                return listed;
            }

            if (field.FieldType == FieldType.Message && !field.IsRepeated && field.Accessor.GetValue(row) is IMessage parallel)
            {
                IReadOnlyList<uint> keys = List(parallel, 1);
                IReadOnlyList<uint> values = List(parallel, 2);
                return keys.Zip(values, (key, value) => (key, value)).ToList();
            }

            return [];
        }

        /// <summary>A repeated numeric column as a list (empty when the column is absent).</summary>
        public static IReadOnlyList<uint> List(IMessage row, int number)
        {
            FieldDescriptor? field = row.Descriptor.FindFieldByNumber(number);
            if (field is null || !field.IsRepeated || field.IsMap || field.Accessor.GetValue(row) is not System.Collections.IEnumerable values)
                return [];

            return values.Cast<object>().Select(Convert.ToUInt32).ToList();
        }

        /// <summary>A repeated 64-bit column (or a single value) as a list.</summary>
        public static IReadOnlyList<ulong> U64List(IMessage row, int number)
        {
            FieldDescriptor? field = row.Descriptor.FindFieldByNumber(number);
            object? value = field?.Accessor.GetValue(row);
            if (value is null)
                return [];

            if (field!.IsRepeated && value is System.Collections.IEnumerable values)
                return values.Cast<object>().Select(Convert.ToUInt64).ToList();

            return [Convert.ToUInt64(value)];
        }

        private static object? Value(IMessage row, int number) =>
            row.Descriptor.FindFieldByNumber(number)?.Accessor.GetValue(row);
    }
}
