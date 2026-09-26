using System;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // gdi32 text: fonts as LOGFONTs, and metrics from a simple proportional
    // model (a cell of the requested height, an average width of half of it,
    // narrow and wide characters a little narrower and wider), consistent
    // across GetTextMetrics, GetTextExtentPoint, GetCharWidth, GetCharABCWidths
    // and GetGlyphOutline so layout code agrees with itself. Glyph shapes
    // are not rasterised: bitmaps come back blank, ExtTextOut paints only its
    // opaque background.
    public sealed partial class GuestKernel
    {
        private void InstallGdiText(GuestImports i, string g)
        {
            i.Register(g, "CreateFontA", CallConv.Stdcall, 14, c => CreateFont(c, false));
            i.Register(g, "CreateFontW", CallConv.Stdcall, 14, c => CreateFont(c, true));
            i.Register(g, "CreateFontIndirectA", CallConv.Stdcall, 1, c => NewGdi(ReadLogFont(c.Arg(0), false)));
            i.Register(g, "CreateFontIndirectW", CallConv.Stdcall, 1, c => NewGdi(ReadLogFont(c.Arg(0), true)));
            i.Register(g, "CreateFontIndirectExA", CallConv.Stdcall, 1, c => NewGdi(ReadLogFont(c.Arg(0), false)));
            i.Register(g, "CreateFontIndirectExW", CallConv.Stdcall, 1, c => NewGdi(ReadLogFont(c.Arg(0), true)));
            i.Register(g, "AddFontResourceA", CallConv.Stdcall, 1, c => 1);
            i.Register(g, "AddFontResourceW", CallConv.Stdcall, 1, c => 1);
            i.Register(g, "AddFontResourceExA", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "AddFontResourceExW", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "RemoveFontResourceA", CallConv.Stdcall, 1, c => 1);
            i.Register(g, "RemoveFontResourceW", CallConv.Stdcall, 1, c => 1);
            i.Register(g, "RemoveFontResourceExA", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "RemoveFontResourceExW", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "AddFontMemResourceEx", CallConv.Stdcall, 4, c => { if (c.Arg(3) != 0) memory.Write32(c.Arg(3), 1); return 0x0FE00001; });
            i.Register(g, "RemoveFontMemResourceEx", CallConv.Stdcall, 1, c => 1);
            i.Register(g, "EnumFontFamiliesExA", CallConv.Stdcall, 5, c => 1);
            i.Register(g, "EnumFontFamiliesExW", CallConv.Stdcall, 5, c => 1);
            i.Register(g, "EnumFontFamiliesA", CallConv.Stdcall, 4, c => 1);
            i.Register(g, "EnumFontFamiliesW", CallConv.Stdcall, 4, c => 1);
            i.Register(g, "EnumFontsA", CallConv.Stdcall, 4, c => 1);
            i.Register(g, "EnumFontsW", CallConv.Stdcall, 4, c => 1);
            i.Register(g, "GetTextMetricsA", CallConv.Stdcall, 2, c => TextMetrics(c.Arg(0), c.Arg(1), false));
            i.Register(g, "GetTextMetricsW", CallConv.Stdcall, 2, c => TextMetrics(c.Arg(0), c.Arg(1), true));
            i.Register(g, "GetTextFaceA", CallConv.Stdcall, 3, c => TextFace(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(g, "GetTextFaceW", CallConv.Stdcall, 3, c => TextFace(c.Arg(0), c.Arg(1), c.Arg(2), true));
            foreach (var name in new[] { "GetTextExtentPoint32", "GetTextExtentPoint" })
            {
                i.Register(g, name + "A", CallConv.Stdcall, 4, c => TextExtent(c.Arg(0), ReadCount(c.Arg(1), c.Arg(2), false), c.Arg(3)));
                i.Register(g, name + "W", CallConv.Stdcall, 4, c => TextExtent(c.Arg(0), ReadCount(c.Arg(1), c.Arg(2), true), c.Arg(3)));
            }
            i.Register(g, "GetTextExtentExPointA", CallConv.Stdcall, 7, c => TextExtentEx(c, false));
            i.Register(g, "GetTextExtentExPointW", CallConv.Stdcall, 7, c => TextExtentEx(c, true));
            i.Register(g, "GetTextExtentPointI", CallConv.Stdcall, 4, c => TextExtent(c.Arg(0), new string('x', (int)c.Arg(2)), c.Arg(3)));
            foreach (var name in new[] { "GetCharWidth32", "GetCharWidth" })
            {
                i.Register(g, name + "A", CallConv.Stdcall, 4, c => CharWidths(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 0));
                i.Register(g, name + "W", CallConv.Stdcall, 4, c => CharWidths(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 0));
            }
            i.Register(g, "GetCharWidthFloatA", CallConv.Stdcall, 4, c => CharWidths(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 1));
            i.Register(g, "GetCharWidthFloatW", CallConv.Stdcall, 4, c => CharWidths(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 1));
            i.Register(g, "GetCharABCWidthsA", CallConv.Stdcall, 4, c => CharWidths(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 2));
            i.Register(g, "GetCharABCWidthsW", CallConv.Stdcall, 4, c => CharWidths(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 2));
            i.Register(g, "GetCharABCWidthsFloatA", CallConv.Stdcall, 4, c => CharWidths(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 3));
            i.Register(g, "GetCharABCWidthsFloatW", CallConv.Stdcall, 4, c => CharWidths(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), 3));
            i.Register(g, "GetGlyphOutlineA", CallConv.Stdcall, 7, c => GlyphOutline(c));
            i.Register(g, "GetGlyphOutlineW", CallConv.Stdcall, 7, c => GlyphOutline(c));
            i.Register(g, "GetGlyphIndicesA", CallConv.Stdcall, 5, c => GlyphIndices(c, false));
            i.Register(g, "GetGlyphIndicesW", CallConv.Stdcall, 5, c => GlyphIndices(c, true));
            i.Register(g, "GetKerningPairsA", CallConv.Stdcall, 3, c => 0);
            i.Register(g, "GetKerningPairsW", CallConv.Stdcall, 3, c => 0);
            i.Register(g, "GetOutlineTextMetricsA", CallConv.Stdcall, 3, c => 0);
            i.Register(g, "GetOutlineTextMetricsW", CallConv.Stdcall, 3, c => 0);
            i.Register(g, "GetFontData", CallConv.Stdcall, 5, c => GdiError);
            i.Register(g, "GetFontUnicodeRanges", CallConv.Stdcall, 2, c => 0);
            i.Register(g, "GetTextCharset", CallConv.Stdcall, 1, c => 0);   // ANSI_CHARSET
            i.Register(g, "GetTextCharsetInfo", CallConv.Stdcall, 3, c => 0);
            i.Register(g, "GetFontLanguageInfo", CallConv.Stdcall, 1, c => 0);
            i.Register(g, "TextOutA", CallConv.Stdcall, 5, c => 1);
            i.Register(g, "TextOutW", CallConv.Stdcall, 5, c => 1);
            i.Register(g, "ExtTextOutA", CallConv.Stdcall, 8, c => ExtTextOut(c));
            i.Register(g, "ExtTextOutW", CallConv.Stdcall, 8, c => ExtTextOut(c));
            i.Register(g, "PolyTextOutA", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "PolyTextOutW", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "GetCharacterPlacementA", CallConv.Stdcall, 6, c => Placement(c, false));
            i.Register(g, "GetCharacterPlacementW", CallConv.Stdcall, 6, c => Placement(c, true));
        }

        private uint CreateFont(GuestCall c, bool wide)
        {
            var face = c.Arg(13) != 0 ? ReadText(c.Arg(13), wide) : "";
            return NewGdi(new GdiObject
            {
                Kind = GdiKind.Font,
                FontHeight = (int)c.Arg(0), FontWidth = (int)c.Arg(1), FontEscapement = (int)c.Arg(2), FontWeight = (int)c.Arg(4),
                Italic = (byte)c.Arg(5), Underline = (byte)c.Arg(6), StrikeOut = (byte)c.Arg(7), CharSet = (byte)c.Arg(8),
                Quality = (byte)c.Arg(11), PitchAndFamily = (byte)c.Arg(12),
                Face = face.Length > 0 ? face : "Arial",
            });
        }

        private GdiObject ReadLogFont(uint p, bool wide)
        {
            var face = wide ? memory.ReadUnicode(p + 28, 32) : Ansi.Decode(memory.ReadBytes(p + 28, 32)).Split('\0')[0];
            return new GdiObject
            {
                Kind = GdiKind.Font,
                FontHeight = (int)memory.Read32(p), FontWidth = (int)memory.Read32(p + 4), FontEscapement = (int)memory.Read32(p + 8),
                FontWeight = (int)memory.Read32(p + 16),
                Italic = memory.Read8(p + 20), Underline = memory.Read8(p + 21), StrikeOut = memory.Read8(p + 22), CharSet = memory.Read8(p + 23),
                Quality = memory.Read8(p + 26), PitchAndFamily = memory.Read8(p + 27),
                Face = face.Length > 0 ? face : "Arial",
            };
        }

        private void WriteLogFont(uint p, GdiObject f, bool wide)
        {
            memory.WriteBytes(p, new byte[wide ? 92 : 60]);
            memory.Write32(p, (uint)f.FontHeight);
            memory.Write32(p + 4, (uint)f.FontWidth);
            memory.Write32(p + 8, (uint)f.FontEscapement);
            memory.Write32(p + 12, (uint)f.FontEscapement);
            memory.Write32(p + 16, (uint)f.FontWeight);
            memory.Write8(p + 20, f.Italic);
            memory.Write8(p + 21, f.Underline);
            memory.Write8(p + 22, f.StrikeOut);
            memory.Write8(p + 23, f.CharSet);
            memory.Write8(p + 26, f.Quality);
            memory.Write8(p + 27, f.PitchAndFamily);
            var face = f.Face.Length > 31 ? f.Face.Substring(0, 31) : f.Face;
            WriteText(p + 28, face, wide);
        }

        private GdiObject DcFont(uint hdc) =>
            dcs.TryGetValue(hdc, out var d) && gdiObjects.TryGetValue(d.Font, out var f) ? f : gdiObjects[StockBase + 13 * 4];

        /// <summary>The model's cell: height, ascent, descent, internal leading, average width.</summary>
        private static void FontCell(GdiObject f, out int height, out int ascent, out int descent, out int leading, out int average)
        {
            // A positive height is the cell, a negative one the character height (the cell less the leading).
            var h = f.FontHeight == 0 ? 16 : Math.Abs(f.FontHeight);
            leading = f.FontHeight > 0 ? (h + 4) / 8 : 0;
            height = f.FontHeight < 0 ? h + (h + 6) / 7 : h;
            if (f.FontHeight < 0) leading = height - h;
            ascent = (height * 4 + 2) / 5;
            descent = height - ascent;
            average = f.FontWidth > 0 ? f.FontWidth : Math.Max(1, (height * 11 + 12) / 24);
        }

        /// <summary>The advance of one character in the model.</summary>
        private static int Advance(GdiObject f, char ch)
        {
            FontCell(f, out _, out _, out _, out _, out var average);
            if (f.FontWeight >= 600) average += Math.Max(1, average / 10);
            if ("iljI.,:;'!|`".IndexOf(ch) >= 0) return Math.Max(1, average * 2 / 5);
            if ("ftr() []{}".IndexOf(ch) >= 0) return Math.Max(1, average * 3 / 5);
            if ("mwMW@%".IndexOf(ch) >= 0) return average * 3 / 2;
            if (char.IsUpper(ch) || char.IsDigit(ch)) return average * 11 / 10;
            return average;
        }

        private int Extent(uint hdc, string text)
        {
            var f = DcFont(hdc);
            var extra = dcs.TryGetValue(hdc, out var d) ? d.CharExtra : 0;
            var width = 0;
            foreach (var ch in text) width += Advance(f, ch) + extra;
            return width;
        }

        private string ReadCount(uint text, uint count, bool wide) =>
            wide ? memory.ReadUnicode(text, (int)count) : Ansi.Decode(memory.ReadBytes(text, (int)count));

        private uint TextMetrics(uint hdc, uint p, bool wide)
        {
            var f = DcFont(hdc);
            FontCell(f, out var height, out var ascent, out var descent, out var leading, out var average);
            memory.WriteBytes(p, new byte[wide ? 60 : 56]);
            memory.Write32(p, (uint)height);
            memory.Write32(p + 4, (uint)ascent);
            memory.Write32(p + 8, (uint)descent);
            memory.Write32(p + 12, (uint)leading);
            memory.Write32(p + 16, 0);
            memory.Write32(p + 20, (uint)average);
            memory.Write32(p + 24, (uint)(average * 2));
            memory.Write32(p + 28, (uint)(f.FontWeight == 0 ? 400 : f.FontWeight));
            memory.Write32(p + 32, 0);
            memory.Write32(p + 36, 96);
            memory.Write32(p + 40, 96);
            if (wide)
            {
                memory.Write16(p + 44, 0x20); memory.Write16(p + 46, 0xFFFC); memory.Write16(p + 48, 0x1F); memory.Write16(p + 50, 0x20);
                memory.Write8(p + 52, f.Italic); memory.Write8(p + 53, f.Underline); memory.Write8(p + 54, f.StrikeOut);
                memory.Write8(p + 55, 0x26); memory.Write8(p + 56, f.CharSet);   // variable pitch, TrueType, swiss
            }
            else
            {
                memory.Write8(p + 44, 0x20); memory.Write8(p + 45, 0xFF); memory.Write8(p + 46, 0x1F); memory.Write8(p + 47, 0x20);
                memory.Write8(p + 48, f.Italic); memory.Write8(p + 49, f.Underline); memory.Write8(p + 50, f.StrikeOut);
                memory.Write8(p + 51, 0x26); memory.Write8(p + 52, f.CharSet);
            }
            return 1;
        }

        private uint TextFace(uint hdc, uint count, uint buffer, bool wide)
        {
            var face = DcFont(hdc).Face;
            if (buffer == 0) return (uint)face.Length + 1;
            return CopyTruncated(face, buffer, count, wide) + 1;
        }

        private uint TextExtent(uint hdc, string text, uint size)
        {
            FontCell(DcFont(hdc), out var height, out _, out _, out _, out _);
            memory.Write32(size, (uint)Extent(hdc, text));
            memory.Write32(size + 4, (uint)height);
            return 1;
        }

        private uint TextExtentEx(GuestCall c, bool wide)
        {
            // (hdc, text, count, maxExtent, fit*, dx*, size*)
            var text = ReadCount(c.Arg(1), c.Arg(2), wide);
            var f = DcFont(c.Arg(0));
            var total = 0;
            var fit = 0;
            for (var n = 0; n < text.Length; n++)
            {
                total += Advance(f, text[n]);
                if (c.Arg(5) != 0) memory.Write32(c.Arg(5) + (uint)n * 4, (uint)total);
                if (c.Arg(4) != 0 && total <= (int)c.Arg(3)) fit = n + 1;
            }
            if (c.Arg(4) != 0) memory.Write32(c.Arg(4), (uint)fit);
            return TextExtent(c.Arg(0), text, c.Arg(6));
        }

        /// <summary>mode 0: int widths; 1: float widths; 2: ABC ints; 3: ABC floats.</summary>
        private uint CharWidths(uint hdc, uint first, uint last, uint buffer, int mode)
        {
            var f = DcFont(hdc);
            for (var ch = first; ch <= last && ch - first < 0x10000; ch++)
            {
                var advance = Advance(f, (char)ch);
                var n = ch - first;
                switch (mode)
                {
                    case 0: memory.Write32(buffer + n * 4, (uint)advance); break;
                    case 1: memory.Write32(buffer + n * 4, (uint)Bits.SingleToInt32Bits(advance)); break;
                    case 2:
                        memory.Write32(buffer + n * 12, 0);
                        memory.Write32(buffer + n * 12 + 4, (uint)advance);
                        memory.Write32(buffer + n * 12 + 8, 0);
                        break;
                    default:
                        memory.Write32(buffer + n * 12, 0);
                        memory.Write32(buffer + n * 12 + 4, (uint)Bits.SingleToInt32Bits(advance));
                        memory.Write32(buffer + n * 12 + 8, 0);
                        break;
                }
            }
            return 1;
        }

        /// <summary>GetGlyphOutline: metrics from the model; bitmaps of the black box's size, blank.</summary>
        private uint GlyphOutline(GuestCall c)
        {
            // (hdc, char, format, GLYPHMETRICS*, bufferSize, buffer, MAT2*)
            const uint GgoGlyphIndex = 0x80, GgoUnhinted = 0x100;
            var format = c.Arg(2) & ~(GgoGlyphIndex | GgoUnhinted);
            var f = DcFont(c.Arg(0));
            FontCell(f, out _, out var ascent, out _, out _, out var average);
            var ch = (char)c.Arg(1);
            var advance = Advance(f, ch);
            var blank = ch == ' ' || ch == '\t';
            var boxWidth = blank ? 1 : Math.Max(1, advance - 1);
            var boxHeight = blank ? 1 : Math.Max(1, ascent);
            if (c.Arg(3) != 0)
            {
                memory.Write32(c.Arg(3), (uint)boxWidth);
                memory.Write32(c.Arg(3) + 4, (uint)boxHeight);
                memory.Write32(c.Arg(3) + 8, 0);                   // origin x
                memory.Write32(c.Arg(3) + 12, (uint)ascent);       // origin y
                memory.Write16(c.Arg(3) + 16, (ushort)advance);
                memory.Write16(c.Arg(3) + 18, 0);
            }
            uint size;
            switch (format)
            {
                case 0: return 1;   // GGO_METRICS
                case 1: size = blank ? 0 : (uint)(((boxWidth + 31) / 32) * 4 * boxHeight); break;          // GGO_BITMAP
                case 4: case 5: case 6: size = blank ? 0 : (uint)(((boxWidth + 3) & ~3) * boxHeight); break; // GGO_GRAY2/4/8
                default: return GdiError;                                                                   // outlines: none
            }
            if (c.Arg(5) == 0 || c.Arg(4) == 0) return size;
            if (c.Arg(4) < size) return GdiError;
            FillMemory(c.Arg(5), size, 0);
            return size;
        }

        private uint GlyphIndices(GuestCall c, bool wide)
        {
            var text = ReadCount(c.Arg(1), c.Arg(2), wide);
            for (var n = 0; n < text.Length; n++) memory.Write16(c.Arg(3) + (uint)n * 2, text[n]);
            return (uint)text.Length;
        }

        private uint ExtTextOut(GuestCall c)
        {
            // (hdc, x, y, options, rect*, text, count, dx*)
            const uint Opaque = 2;
            if ((c.Arg(3) & Opaque) != 0 && c.Arg(4) != 0 && dcs.TryGetValue(c.Arg(0), out var d))
            {
                var r = c.Arg(4);
                FillArea(c.Arg(0), (int)memory.Read32(r), (int)memory.Read32(r + 4), (int)memory.Read32(r + 8), (int)memory.Read32(r + 12), 0, Rgb(d.BkColor));
            }
            return 1;
        }

        private uint Placement(GuestCall c, bool wide)
        {
            // (hdc, text, count, maxExtent, GCP_RESULTS*, flags): the extent in the low and high words.
            var text = ReadCount(c.Arg(1), c.Arg(2), wide);
            FontCell(DcFont(c.Arg(0)), out var height, out _, out _, out _, out _);
            return ((uint)height << 16) | (uint)(Extent(c.Arg(0), text) & 0xFFFF);
        }
    }
}
