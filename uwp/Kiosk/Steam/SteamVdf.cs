using System;
using System.Collections.Generic;
using System.Text;

namespace Kiosk.Steam
{
    /// <summary>
    /// KeyValues, which is how PICS describes an app. It answers in text for
    /// some apps and in binary for others, so the caller should not have to
    /// know which.
    /// </summary>
    public sealed class KeyValue
    {
        public string Value { get; set; }
        public Dictionary<string, KeyValue> Children { get; } =
            new Dictionary<string, KeyValue>(StringComparer.OrdinalIgnoreCase);

        public KeyValue this[string key] =>
            Children.TryGetValue(key, out var child) ? child : null;

        public string Text(string key) => this[key]?.Value;

        public static KeyValue Parse(byte[] buffer)
        {
            var at = 0;
            while (at < buffer.Length &&
                   (buffer[at] == 0x20 || buffer[at] == 0x09 ||
                    buffer[at] == 0x0A || buffer[at] == 0x0D))
            {
                at++;
            }
            return at < buffer.Length && (buffer[at] == 0x22 || buffer[at] == 0x2F)
                ? ParseText(Encoding.UTF8.GetString(buffer, 0, buffer.Length))
                : ParseBinary(buffer);
        }

        // -------------------------------------------------------------- text

        private static KeyValue ParseText(string text)
        {
            var i = 0;

            void Skip()
            {
                while (i < text.Length)
                {
                    if (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '/')
                    {
                        while (i < text.Length && text[i] != '\n') i++;
                    }
                    else if (char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            string Token()
            {
                Skip();
                if (i >= text.Length) return null;
                if (text[i] == '{' || text[i] == '}') return text[i++].ToString();
                if (text[i] == '"')
                {
                    i++;
                    var builder = new StringBuilder();
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length)
                        {
                            i++;
                            builder.Append(text[i] == 'n' ? '\n' : text[i] == 't' ? '\t' : text[i]);
                        }
                        else
                        {
                            builder.Append(text[i]);
                        }
                        i++;
                    }
                    i++;
                    return builder.ToString();
                }
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) &&
                       text[i] != '{' && text[i] != '}' && text[i] != '"')
                {
                    i++;
                }
                return text.Substring(start, i - start);
            }

            KeyValue Object()
            {
                var node = new KeyValue();
                while (true)
                {
                    var key = Token();
                    if (key == null || key == "}") break;
                    var value = Token();
                    if (value == null) break;
                    node.Children[key] = value == "{"
                        ? Object()
                        : new KeyValue { Value = value };
                }
                return node;
            }

            var rootName = Token();
            var open = Token();
            if (rootName == null || open != "{") return new KeyValue();
            var root = new KeyValue();
            root.Children[rootName] = Object();
            return root;
        }

        // ------------------------------------------------------------ binary

        private static KeyValue ParseBinary(byte[] buffer)
        {
            var at = 0;

            string CString()
            {
                var start = at;
                while (at < buffer.Length && buffer[at] != 0) at++;
                var text = Encoding.UTF8.GetString(buffer, start, at - start);
                at++;
                return text;
            }

            KeyValue Object()
            {
                var node = new KeyValue();
                while (at < buffer.Length)
                {
                    var type = buffer[at++];
                    if (type == 0x08 || type == 0x0B) break;
                    var key = CString();
                    switch (type)
                    {
                        case 0x00:
                            node.Children[key] = Object();
                            break;
                        case 0x01:
                            node.Children[key] = new KeyValue { Value = CString() };
                            break;
                        case 0x02:
                        case 0x04:
                        case 0x06:
                            node.Children[key] = new KeyValue
                            {
                                Value = BitConverter.ToInt32(buffer, at).ToString(),
                            };
                            at += 4;
                            break;
                        case 0x03:
                            node.Children[key] = new KeyValue
                            {
                                Value = BitConverter.ToSingle(buffer, at).ToString(),
                            };
                            at += 4;
                            break;
                        case 0x07:
                            node.Children[key] = new KeyValue
                            {
                                Value = BitConverter.ToUInt64(buffer, at).ToString(),
                            };
                            at += 8;
                            break;
                        case 0x0A:
                            node.Children[key] = new KeyValue
                            {
                                Value = BitConverter.ToInt64(buffer, at).ToString(),
                            };
                            at += 8;
                            break;
                        default:
                            // An unknown type means the offset is already wrong.
                            return node;
                    }
                }
                return node;
            }

            return Object();
        }
    }

    public sealed class Depot
    {
        public uint Id { get; set; }
        public string ManifestId { get; set; }
        public long Size { get; set; }
    }

    public static class Depots
    {
        /// <summary>
        /// The depots a Windows install needs. Shared ones (redistributables)
        /// and other platforms are listed for every app and are not the game.
        /// </summary>
        public static List<Depot> ForWindows(KeyValue appInfo, string branch = "public")
        {
            var out_ = new List<Depot>();
            var root = appInfo["appinfo"] ?? appInfo;
            var depots = root?["depots"];
            if (depots == null) return out_;

            foreach (var pair in depots.Children)
            {
                if (!uint.TryParse(pair.Key, out var id)) continue;
                var depot = pair.Value;
                if (depot["depotfromapp"] != null) continue;

                var os = depot["config"]?.Text("oslist");
                if (!string.IsNullOrEmpty(os) && Array.IndexOf(os.Split(','), "windows") < 0)
                {
                    continue;
                }

                var manifests = depot["manifests"];
                var entry = manifests?[branch];
                var manifestId = entry?.Value ?? entry?.Text("gid");
                if (string.IsNullOrEmpty(manifestId)) continue;

                long.TryParse(depot.Text("maxsize") ?? "0", out var size);
                out_.Add(new Depot { Id = id, ManifestId = manifestId, Size = size });
            }
            return out_;
        }
    }
}
