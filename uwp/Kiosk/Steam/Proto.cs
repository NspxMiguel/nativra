using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kiosk.Steam
{
    /// <summary>
    /// A protobuf codec with no schema: fields go in and come out by number.
    /// Steam's messages are read once and thrown away, so generated classes
    /// would cost more than they save. The field numbers are in
    /// docs/STEAM-DEPOT.md, read from the published .proto files.
    /// </summary>
    public sealed class ProtoWriter
    {
        private readonly MemoryStream stream = new MemoryStream();

        private void Varint(ulong value)
        {
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                stream.WriteByte(b);
            } while (value != 0);
        }

        private void Tag(int field, int wire) => Varint((ulong)((field << 3) | wire));

        public ProtoWriter Uint(int field, ulong value)
        {
            Tag(field, 0);
            Varint(value);
            return this;
        }

        public ProtoWriter Bool(int field, bool value) => Uint(field, value ? 1UL : 0UL);

        public ProtoWriter Fixed64(int field, ulong value)
        {
            Tag(field, 1);
            var bytes = BitConverter.GetBytes(value);
            stream.Write(bytes, 0, 8);
            return this;
        }

        public ProtoWriter Bytes(int field, byte[] value)
        {
            Tag(field, 2);
            Varint((ulong)value.Length);
            stream.Write(value, 0, value.Length);
            return this;
        }

        public ProtoWriter String(int field, string value) =>
            Bytes(field, Encoding.UTF8.GetBytes(value ?? string.Empty));

        public ProtoWriter Message(int field, ProtoWriter inner) => Bytes(field, inner.Finish());

        public byte[] Finish() => stream.ToArray();
    }

    /// <summary>Field number to every value seen; repeated fields are normal.</summary>
    public sealed class ProtoMessage
    {
        private readonly Dictionary<int, List<object>> fields = new Dictionary<int, List<object>>();

        public static ProtoMessage Read(byte[] buffer) => Read(buffer, 0, buffer.Length);

        public static ProtoMessage Read(byte[] buffer, int start, int length)
        {
            var message = new ProtoMessage();
            var i = start;
            var end = start + length;

            ulong Varint()
            {
                ulong value = 0;
                var shift = 0;
                while (i < end)
                {
                    var b = buffer[i++];
                    value |= (ulong)(b & 0x7F) << shift;
                    shift += 7;
                    if ((b & 0x80) == 0) break;
                }
                return value;
            }

            while (i < end)
            {
                var tag = Varint();
                var field = (int)(tag >> 3);
                var wire = (int)(tag & 7);
                if (wire == 0)
                {
                    message.Add(field, Varint());
                }
                else if (wire == 1)
                {
                    message.Add(field, BitConverter.ToUInt64(buffer, i));
                    i += 8;
                }
                else if (wire == 2)
                {
                    var size = (int)Varint();
                    var data = new byte[size];
                    Array.Copy(buffer, i, data, 0, size);
                    i += size;
                    message.Add(field, data);
                }
                else if (wire == 5)
                {
                    message.Add(field, (ulong)BitConverter.ToUInt32(buffer, i));
                    i += 4;
                }
                else
                {
                    break;
                }
            }
            return message;
        }

        private void Add(int field, object value)
        {
            if (!fields.TryGetValue(field, out var list))
            {
                list = new List<object>(1);
                fields[field] = list;
            }
            list.Add(value);
        }

        public ulong Num(int field, ulong fallback = 0)
        {
            if (fields.TryGetValue(field, out var list) && list.Count > 0 && list[0] is ulong value)
            {
                return value;
            }
            return fallback;
        }

        public byte[] Raw(int field)
        {
            if (fields.TryGetValue(field, out var list) && list.Count > 0 && list[0] is byte[] value)
            {
                return value;
            }
            return null;
        }

        public string Str(int field)
        {
            var raw = Raw(field);
            return raw == null ? null : Encoding.UTF8.GetString(raw, 0, raw.Length);
        }

        public List<ProtoMessage> List(int field)
        {
            var out_ = new List<ProtoMessage>();
            if (!fields.TryGetValue(field, out var list)) return out_;
            foreach (var value in list)
            {
                if (value is byte[] bytes) out_.Add(Read(bytes));
            }
            return out_;
        }

        public bool Has(int field) => fields.ContainsKey(field);
    }
}
