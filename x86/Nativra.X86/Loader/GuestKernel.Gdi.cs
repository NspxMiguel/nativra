using System;
using System.Collections.Generic;
using Nativra.X86.Cpu;

namespace Nativra.X86.Loader
{
    // gdi32 for games: a real object model with no display behind it.
    // Device contexts, bitmaps and DIB sections whose pixels live in guest
    // memory, pens, brushes, fonts, regions and palettes are all tracked, and
    // the pixel operations between memory DCs (BitBlt, StretchBlt, PatBlt,
    // StretchDIBits, GetDIBits, SetPixel, FillRect) really move pixels, so a
    // game or d3dx9 that builds a texture through GDI gets the image it drew.
    // Drawing on the screen DC is accepted and dropped: the frame reaches the
    // screen through Direct3D. Fonts answer with consistent metrics from a
    // simple proportional model; glyph shapes are not rasterised yet, so text
    // drawn through GDI comes out blank.
    public sealed partial class GuestKernel
    {
        private const uint GdiBase = 0x0C000000;
        private const uint StockBase = 0x0C00F000;
        private const uint GdiError = 0xFFFFFFFF;

        private enum GdiKind { Pen = 1, Brush = 2, Dc = 3, Font = 6, Bitmap = 7, Region = 8, Palette = 5 }

        private sealed class GdiBitmap
        {
            public int Width, Height, Bpp, Stride;
            public uint Bits;
            public bool TopDown, Section;
            public uint[] Palette;   // 0x00RRGGBB, for 1/4/8 bits per pixel
            public uint HeaderAt;    // DIBSECTION's BITMAPINFOHEADER copy, for GetObject
        }

        private sealed class GdiObject
        {
            public GdiKind Kind;
            public bool Stock;
            public GdiBitmap Bitmap;
            public uint Color;            // COLORREF for pens and brushes
            public int Style, Width;      // pen/brush style, pen width
            public int FontHeight, FontWidth, FontWeight, FontEscapement;
            public byte Italic, Underline, StrikeOut, CharSet, Quality, PitchAndFamily;
            public string Face = "Arial";
            public int Left, Top, Right, Bottom;   // regions
            public uint[] Entries;        // palettes
        }

        private sealed class DeviceContext
        {
            public bool Screen;
            public uint Bitmap, Font, Brush, Pen, Palette, Region;
            public uint TextColor, BkColor = 0xFFFFFF, DcBrushColor = 0xFFFFFF, DcPenColor;
            public int BkMode = 2, TextAlign, MapMode = 1, StretchMode = 1, Rop2 = 13, CharExtra;
            public int PosX, PosY, ViewX, ViewY, WindowX, WindowY;
            public readonly Stack<DeviceContext> Saved = new Stack<DeviceContext>();

            public DeviceContext Copy()
            {
                var d = (DeviceContext)MemberwiseClone();
                return d;
            }
        }

        private readonly Dictionary<uint, GdiObject> gdiObjects = new Dictionary<uint, GdiObject>();
        private readonly Dictionary<uint, DeviceContext> dcs = new Dictionary<uint, DeviceContext>();
        private uint nextGdi = GdiBase;
        private uint defaultBitmap;

        private uint NewGdi(GdiObject o)
        {
            var h = nextGdi;
            nextGdi += 4;
            gdiObjects[h] = o;
            return h;
        }

        private static uint Rgb(uint colorref) => ((colorref & 0xFF) << 16) | (colorref & 0xFF00) | ((colorref >> 16) & 0xFF);
        private static uint ColorRef(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

        private void InstallGdi(GuestImports i)
        {
            const string g = "gdi32.dll";

            // Stock objects, at fixed handles.
            void Stock(int index, GdiObject o) { o.Stock = true; gdiObjects[StockBase + (uint)index * 4] = o; }
            Stock(0, new GdiObject { Kind = GdiKind.Brush, Color = 0xFFFFFF });
            Stock(1, new GdiObject { Kind = GdiKind.Brush, Color = 0xC0C0C0 });
            Stock(2, new GdiObject { Kind = GdiKind.Brush, Color = 0x808080 });
            Stock(3, new GdiObject { Kind = GdiKind.Brush, Color = 0x404040 });
            Stock(4, new GdiObject { Kind = GdiKind.Brush, Color = 0 });
            Stock(5, new GdiObject { Kind = GdiKind.Brush, Style = 1 });            // NULL_BRUSH (BS_NULL)
            Stock(6, new GdiObject { Kind = GdiKind.Pen, Color = 0xFFFFFF, Width = 1 });
            Stock(7, new GdiObject { Kind = GdiKind.Pen, Color = 0, Width = 1 });
            Stock(8, new GdiObject { Kind = GdiKind.Pen, Style = 5 });              // NULL_PEN (PS_NULL)
            foreach (var font in new[] { 10, 11, 12, 13, 14, 16, 17 })
                Stock(font, new GdiObject { Kind = GdiKind.Font, FontHeight = font == 17 ? -11 : 16, FontWeight = 400, Face = font == 10 || font == 11 || font == 16 ? "Courier" : "Tahoma" });
            Stock(15, new GdiObject { Kind = GdiKind.Palette, Entries = new uint[20] });
            Stock(18, new GdiObject { Kind = GdiKind.Brush, Color = 0xFFFFFF });    // DC_BRUSH
            Stock(19, new GdiObject { Kind = GdiKind.Pen, Color = 0, Width = 1 });  // DC_PEN
            defaultBitmap = NewGdi(new GdiObject { Kind = GdiKind.Bitmap, Stock = true, Bitmap = new GdiBitmap { Width = 1, Height = 1, Bpp = 1, Stride = 4, Bits = heap.Alloc(4, zero: true) } });

            dcs[FakeDc] = NewDc(true);

            i.Register(g, "GetStockObject", CallConv.Stdcall, 1, c => c.Arg(0) <= 19 && gdiObjects.ContainsKey(StockBase + c.Arg(0) * 4) ? StockBase + c.Arg(0) * 4 : 0);
            i.Register(g, "DeleteObject", CallConv.Stdcall, 1, c => DeleteGdi(c.Arg(0)));
            i.Register(g, "SelectObject", CallConv.Stdcall, 2, c => SelectGdi(c.Arg(0), c.Arg(1)));
            i.Register(g, "GetCurrentObject", CallConv.Stdcall, 2, c => CurrentObject(c.Arg(0), c.Arg(1)));
            i.Register(g, "GetObjectType", CallConv.Stdcall, 1, c => dcs.ContainsKey(c.Arg(0)) ? 3u : gdiObjects.TryGetValue(c.Arg(0), out var o) ? (uint)o.Kind : 0);
            i.Register(g, "GetObjectA", CallConv.Stdcall, 3, c => GetGdiObject(c.Arg(0), c.Arg(1), c.Arg(2), false));
            i.Register(g, "GetObjectW", CallConv.Stdcall, 3, c => GetGdiObject(c.Arg(0), c.Arg(1), c.Arg(2), true));
            i.Register(g, "UnrealizeObject", CallConv.Stdcall, 1, c => 1);
            i.Register(g, "GdiFlush", CallConv.Stdcall, 0, c => 1);
            i.Register(g, "GdiSetBatchLimit", CallConv.Stdcall, 1, c => 20);

            // Device contexts.
            i.Register(g, "CreateCompatibleDC", CallConv.Stdcall, 1, c => NewDcHandle(false));
            i.Register(g, "CreateDCA", CallConv.Stdcall, 4, c => NewDcHandle(true));
            i.Register(g, "CreateDCW", CallConv.Stdcall, 4, c => NewDcHandle(true));
            i.Register(g, "CreateICA", CallConv.Stdcall, 4, c => NewDcHandle(true));
            i.Register(g, "CreateICW", CallConv.Stdcall, 4, c => NewDcHandle(true));
            i.Register(g, "DeleteDC", CallConv.Stdcall, 1, c => c.Arg(0) != FakeDc && dcs.Remove(c.Arg(0)) ? 1u : c.Arg(0) == FakeDc ? 1u : 0u);
            i.Register(g, "SaveDC", CallConv.Stdcall, 1, c =>
            {
                if (!dcs.TryGetValue(c.Arg(0), out var dc)) return 0;
                dc.Saved.Push(dc.Copy());
                return (uint)dc.Saved.Count;
            });
            i.Register(g, "RestoreDC", CallConv.Stdcall, 2, c =>
            {
                if (!dcs.TryGetValue(c.Arg(0), out var dc) || dc.Saved.Count == 0) return 0;
                var level = (int)c.Arg(1);
                var target = level < 0 ? dc.Saved.Count + level + 1 : level;
                DeviceContext state = null;
                while (dc.Saved.Count >= Math.Max(1, target)) state = dc.Saved.Pop();
                if (state == null) return 0;
                var saved = new Stack<DeviceContext>(dc.Saved);
                var restored = state.Copy();
                dcs[c.Arg(0)] = restored;
                foreach (var s in saved) restored.Saved.Push(s);
                return 1;
            });
            i.Register(g, "SetTextColor", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => d.TextColor, (d, v) => d.TextColor = v, c.Arg(1)));
            i.Register(g, "GetTextColor", CallConv.Stdcall, 1, c => dcs.TryGetValue(c.Arg(0), out var d) ? d.TextColor : 0);
            i.Register(g, "SetBkColor", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => d.BkColor, (d, v) => d.BkColor = v, c.Arg(1)));
            i.Register(g, "GetBkColor", CallConv.Stdcall, 1, c => dcs.TryGetValue(c.Arg(0), out var d) ? d.BkColor : 0);
            i.Register(g, "SetBkMode", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => (uint)d.BkMode, (d, v) => d.BkMode = (int)v, c.Arg(1)));
            i.Register(g, "GetBkMode", CallConv.Stdcall, 1, c => dcs.TryGetValue(c.Arg(0), out var d) ? (uint)d.BkMode : 0);
            i.Register(g, "SetTextAlign", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => (uint)d.TextAlign, (d, v) => d.TextAlign = (int)v, c.Arg(1)));
            i.Register(g, "GetTextAlign", CallConv.Stdcall, 1, c => dcs.TryGetValue(c.Arg(0), out var d) ? (uint)d.TextAlign : GdiError);
            i.Register(g, "SetMapMode", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => (uint)d.MapMode, (d, v) => d.MapMode = (int)v, c.Arg(1)));
            i.Register(g, "GetMapMode", CallConv.Stdcall, 1, c => dcs.TryGetValue(c.Arg(0), out var d) ? (uint)d.MapMode : 0);
            i.Register(g, "SetStretchBltMode", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => (uint)d.StretchMode, (d, v) => d.StretchMode = (int)v, c.Arg(1)));
            i.Register(g, "GetStretchBltMode", CallConv.Stdcall, 1, c => dcs.TryGetValue(c.Arg(0), out var d) ? (uint)d.StretchMode : 0);
            i.Register(g, "SetROP2", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => (uint)d.Rop2, (d, v) => d.Rop2 = (int)v, c.Arg(1)));
            i.Register(g, "SetTextCharacterExtra", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => (uint)d.CharExtra, (d, v) => d.CharExtra = (int)v, c.Arg(1)));
            i.Register(g, "SetDCBrushColor", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => d.DcBrushColor, (d, v) => d.DcBrushColor = v, c.Arg(1)));
            i.Register(g, "SetDCPenColor", CallConv.Stdcall, 2, c => DcSwap(c.Arg(0), d => d.DcPenColor, (d, v) => d.DcPenColor = v, c.Arg(1)));
            i.Register(g, "SetGraphicsMode", CallConv.Stdcall, 2, c => 1);
            i.Register(g, "SetLayout", CallConv.Stdcall, 2, c => 0);
            i.Register(g, "SetPolyFillMode", CallConv.Stdcall, 2, c => 1);
            i.Register(g, "SetBrushOrgEx", CallConv.Stdcall, 4, c => { if (c.Arg(3) != 0) memory.Write64(c.Arg(3), 0); return 1; });
            i.Register(g, "MoveToEx", CallConv.Stdcall, 4, c => PointSwap(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), d => (d.PosX, d.PosY), (d, x, y) => { d.PosX = x; d.PosY = y; }));
            i.Register(g, "SetViewportOrgEx", CallConv.Stdcall, 4, c => PointSwap(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), d => (d.ViewX, d.ViewY), (d, x, y) => { d.ViewX = x; d.ViewY = y; }));
            i.Register(g, "SetWindowOrgEx", CallConv.Stdcall, 4, c => PointSwap(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), d => (d.WindowX, d.WindowY), (d, x, y) => { d.WindowX = x; d.WindowY = y; }));
            i.Register(g, "GetViewportOrgEx", CallConv.Stdcall, 2, c => { if (dcs.TryGetValue(c.Arg(0), out var d)) { memory.Write32(c.Arg(1), (uint)d.ViewX); memory.Write32(c.Arg(1) + 4, (uint)d.ViewY); } return 1; });
            i.Register(g, "GetWindowOrgEx", CallConv.Stdcall, 2, c => { if (dcs.TryGetValue(c.Arg(0), out var d)) { memory.Write32(c.Arg(1), (uint)d.WindowX); memory.Write32(c.Arg(1) + 4, (uint)d.WindowY); } return 1; });
            i.Register(g, "SetViewportExtEx", CallConv.Stdcall, 4, c => 1);
            i.Register(g, "SetWindowExtEx", CallConv.Stdcall, 4, c => 1);
            i.Register(g, "LPtoDP", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "DPtoLP", CallConv.Stdcall, 3, c => 1);

            // Bitmaps.
            i.Register(g, "CreateCompatibleBitmap", CallConv.Stdcall, 3, c => NewBitmap((int)c.Arg(1), (int)c.Arg(2), 32, 0, false, null));
            i.Register(g, "CreateDiscardableBitmap", CallConv.Stdcall, 3, c => NewBitmap((int)c.Arg(1), (int)c.Arg(2), 32, 0, false, null));
            i.Register(g, "CreateBitmap", CallConv.Stdcall, 5, c => NewBitmap((int)c.Arg(0), (int)c.Arg(1), (int)Math.Max(1, c.Arg(3)), c.Arg(4), false, null));
            i.Register(g, "CreateBitmapIndirect", CallConv.Stdcall, 1, c =>
                NewBitmap((int)memory.Read32(c.Arg(0) + 4), (int)memory.Read32(c.Arg(0) + 8), memory.Read16(c.Arg(0) + 18), memory.Read32(c.Arg(0) + 20), false, null));
            i.Register(g, "CreateDIBSection", CallConv.Stdcall, 6, c => CreateDibSection(c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(g, "CreateDIBitmap", CallConv.Stdcall, 6, c =>
            {
                var header = c.Arg(1);
                var h = NewBitmap((int)memory.Read32(header + 4), Math.Abs((int)memory.Read32(header + 8)), 32, 0, false, null);
                if ((c.Arg(2) & 4) != 0 && c.Arg(3) != 0 && c.Arg(4) != 0)   // CBM_INIT
                    CopyDibIntoBitmap(gdiObjects[h].Bitmap, 0, 0, c.Arg(3), c.Arg(4), 0, int.MaxValue);
                return h;
            });
            i.Register(g, "GetDIBits", CallConv.Stdcall, 7, c => GetDiBits(c.Arg(1), c.Arg(2), c.Arg(3), c.Arg(4), c.Arg(5)));
            i.Register(g, "SetDIBits", CallConv.Stdcall, 7, c =>
            {
                if (!gdiObjects.TryGetValue(c.Arg(1), out var o) || o.Bitmap == null) return 0;
                return (uint)CopyDibIntoBitmap(o.Bitmap, 0, 0, c.Arg(4), c.Arg(5), (int)c.Arg(2), (int)c.Arg(3));
            });
            i.Register(g, "GetBitmapBits", CallConv.Stdcall, 3, c =>
            {
                if (!gdiObjects.TryGetValue(c.Arg(0), out var o) || o.Bitmap == null) return 0;
                var n = Math.Min(c.Arg(1), (uint)(o.Bitmap.Stride * o.Bitmap.Height));
                MoveMemory(c.Arg(2), o.Bitmap.Bits, n);
                return n;
            });
            i.Register(g, "SetBitmapBits", CallConv.Stdcall, 3, c =>
            {
                if (!gdiObjects.TryGetValue(c.Arg(0), out var o) || o.Bitmap == null) return 0;
                var n = Math.Min(c.Arg(1), (uint)(o.Bitmap.Stride * o.Bitmap.Height));
                MoveMemory(o.Bitmap.Bits, c.Arg(2), n);
                return n;
            });
            i.Register(g, "GetDIBColorTable", CallConv.Stdcall, 4, c => ColorTable(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), false));
            i.Register(g, "SetDIBColorTable", CallConv.Stdcall, 4, c => ColorTable(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3), true));
            i.Register(g, "GetBitmapDimensionEx", CallConv.Stdcall, 2, c => { memory.Write64(c.Arg(1), 0); return 1; });

            // Pixels.
            i.Register(g, "BitBlt", CallConv.Stdcall, 9, c =>
                Blit(c.Arg(0), (int)c.Arg(1), (int)c.Arg(2), (int)c.Arg(3), (int)c.Arg(4), c.Arg(5), (int)c.Arg(6), (int)c.Arg(7), (int)c.Arg(3), (int)c.Arg(4), c.Arg(8)));
            i.Register(g, "StretchBlt", CallConv.Stdcall, 11, c =>
                Blit(c.Arg(0), (int)c.Arg(1), (int)c.Arg(2), (int)c.Arg(3), (int)c.Arg(4), c.Arg(5), (int)c.Arg(6), (int)c.Arg(7), (int)c.Arg(8), (int)c.Arg(9), c.Arg(10)));
            i.Register(g, "PatBlt", CallConv.Stdcall, 6, c => PatBlt(c.Arg(0), (int)c.Arg(1), (int)c.Arg(2), (int)c.Arg(3), (int)c.Arg(4), c.Arg(5)));
            i.Register(g, "StretchDIBits", CallConv.Stdcall, 13, c => StretchDiBits(c));
            i.Register(g, "SetDIBitsToDevice", CallConv.Stdcall, 12, c =>
            {
                var bmp = DcBitmap(c.Arg(0));
                if (bmp == null) return c.Arg(6) != 0 ? c.Arg(6) : Math.Abs((int)memory.Read32(c.Arg(10) + 8)) is var h ? (uint)h : 0;
                return (uint)CopyDibIntoBitmap(bmp, (int)c.Arg(1), (int)c.Arg(2), c.Arg(9), c.Arg(10), (int)c.Arg(5), (int)c.Arg(6));
            });
            i.Register(g, "SetPixel", CallConv.Stdcall, 4, c =>
            {
                var bmp = DcBitmap(c.Arg(0));
                if (bmp == null) return c.Arg(3);
                PutPixel(bmp, (int)c.Arg(1), (int)c.Arg(2), Rgb(c.Arg(3)));
                return c.Arg(3);
            });
            i.Register(g, "SetPixelV", CallConv.Stdcall, 4, c => { var bmp = DcBitmap(c.Arg(0)); if (bmp != null) PutPixel(bmp, (int)c.Arg(1), (int)c.Arg(2), Rgb(c.Arg(3))); return 1; });
            i.Register(g, "GetPixel", CallConv.Stdcall, 3, c =>
            {
                var bmp = DcBitmap(c.Arg(0));
                if (bmp == null || c.Arg(1) >= bmp.Width || c.Arg(2) >= bmp.Height) return GdiError;   // CLR_INVALID
                return ColorRef(PixelAt(bmp, (int)c.Arg(1), (int)c.Arg(2)));
            });
            i.Register(g, "Rectangle", CallConv.Stdcall, 5, c => { FillArea(c.Arg(0), (int)c.Arg(1), (int)c.Arg(2), (int)c.Arg(3), (int)c.Arg(4), 0); return 1; });
            i.Register(g, "Ellipse", CallConv.Stdcall, 5, c => 1);
            i.Register(g, "RoundRect", CallConv.Stdcall, 7, c => 1);
            i.Register(g, "LineTo", CallConv.Stdcall, 3, c => { if (dcs.TryGetValue(c.Arg(0), out var d)) { d.PosX = (int)c.Arg(1); d.PosY = (int)c.Arg(2); } return 1; });
            i.Register(g, "Polyline", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "Polygon", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "PolyPolygon", CallConv.Stdcall, 4, c => 1);
            i.Register(g, "FillRgn", CallConv.Stdcall, 3, c => 1);
            i.Register(g, "PaintRgn", CallConv.Stdcall, 2, c => 1);
            i.Register(g, "FrameRgn", CallConv.Stdcall, 5, c => 1);

            // Pens, brushes, regions, palettes.
            i.Register(g, "CreatePen", CallConv.Stdcall, 3, c => NewGdi(new GdiObject { Kind = GdiKind.Pen, Style = (int)c.Arg(0), Width = (int)c.Arg(1), Color = c.Arg(2) }));
            i.Register(g, "CreatePenIndirect", CallConv.Stdcall, 1, c => NewGdi(new GdiObject { Kind = GdiKind.Pen, Style = (int)memory.Read32(c.Arg(0)), Width = (int)memory.Read32(c.Arg(0) + 4), Color = memory.Read32(c.Arg(0) + 12) }));
            i.Register(g, "ExtCreatePen", CallConv.Stdcall, 5, c => NewGdi(new GdiObject { Kind = GdiKind.Pen, Style = (int)c.Arg(0), Width = (int)c.Arg(1), Color = memory.Read32(c.Arg(2) + 4) }));
            i.Register(g, "CreateSolidBrush", CallConv.Stdcall, 1, c => NewGdi(new GdiObject { Kind = GdiKind.Brush, Color = c.Arg(0) }));
            i.Register(g, "CreateHatchBrush", CallConv.Stdcall, 2, c => NewGdi(new GdiObject { Kind = GdiKind.Brush, Style = 2, Color = c.Arg(1) }));
            i.Register(g, "CreatePatternBrush", CallConv.Stdcall, 1, c => NewGdi(new GdiObject { Kind = GdiKind.Brush, Style = 3 }));
            i.Register(g, "CreateBrushIndirect", CallConv.Stdcall, 1, c => NewGdi(new GdiObject { Kind = GdiKind.Brush, Style = (int)memory.Read32(c.Arg(0)), Color = memory.Read32(c.Arg(0) + 4) }));
            i.Register(g, "CreateDIBPatternBrushPt", CallConv.Stdcall, 2, c => NewGdi(new GdiObject { Kind = GdiKind.Brush, Style = 6 }));
            i.Register(g, "CreateRectRgn", CallConv.Stdcall, 4, c => NewRegion((int)c.Arg(0), (int)c.Arg(1), (int)c.Arg(2), (int)c.Arg(3)));
            i.Register(g, "CreateRectRgnIndirect", CallConv.Stdcall, 1, c => NewRegion((int)memory.Read32(c.Arg(0)), (int)memory.Read32(c.Arg(0) + 4), (int)memory.Read32(c.Arg(0) + 8), (int)memory.Read32(c.Arg(0) + 12)));
            i.Register(g, "CreateRoundRectRgn", CallConv.Stdcall, 6, c => NewRegion((int)c.Arg(0), (int)c.Arg(1), (int)c.Arg(2), (int)c.Arg(3)));
            i.Register(g, "CreateEllipticRgn", CallConv.Stdcall, 4, c => NewRegion((int)c.Arg(0), (int)c.Arg(1), (int)c.Arg(2), (int)c.Arg(3)));
            i.Register(g, "SetRectRgn", CallConv.Stdcall, 5, c =>
            {
                if (!gdiObjects.TryGetValue(c.Arg(0), out var r)) return 0;
                r.Left = (int)c.Arg(1); r.Top = (int)c.Arg(2); r.Right = (int)c.Arg(3); r.Bottom = (int)c.Arg(4);
                return 1;
            });
            i.Register(g, "CombineRgn", CallConv.Stdcall, 4, c => CombineRegion(c.Arg(0), c.Arg(1), c.Arg(2), c.Arg(3)));
            i.Register(g, "GetRgnBox", CallConv.Stdcall, 2, c =>
            {
                if (!gdiObjects.TryGetValue(c.Arg(0), out var r)) return 0;
                WriteRect(c.Arg(1), r.Left, r.Top, r.Right, r.Bottom);
                return r.Right > r.Left && r.Bottom > r.Top ? 2u : 1u;   // SIMPLEREGION / NULLREGION
            });
            i.Register(g, "OffsetRgn", CallConv.Stdcall, 3, c =>
            {
                if (!gdiObjects.TryGetValue(c.Arg(0), out var r)) return 0;
                r.Left += (int)c.Arg(1); r.Right += (int)c.Arg(1); r.Top += (int)c.Arg(2); r.Bottom += (int)c.Arg(2);
                return 2;
            });
            i.Register(g, "PtInRegion", CallConv.Stdcall, 3, c =>
                gdiObjects.TryGetValue(c.Arg(0), out var r) && (int)c.Arg(1) >= r.Left && (int)c.Arg(1) < r.Right && (int)c.Arg(2) >= r.Top && (int)c.Arg(2) < r.Bottom ? 1u : 0u);
            i.Register(g, "SelectClipRgn", CallConv.Stdcall, 2, c => 2);
            i.Register(g, "ExtSelectClipRgn", CallConv.Stdcall, 3, c => 2);
            i.Register(g, "IntersectClipRect", CallConv.Stdcall, 5, c => 2);
            i.Register(g, "ExcludeClipRect", CallConv.Stdcall, 5, c => 2);
            i.Register(g, "GetClipBox", CallConv.Stdcall, 2, c =>
            {
                var bmp = DcBitmap(c.Arg(0));
                WriteRect(c.Arg(1), 0, 0, bmp?.Width ?? ScreenWidth, bmp?.Height ?? ScreenHeight);
                return 2;
            });
            i.Register(g, "GetClipRgn", CallConv.Stdcall, 2, c => 0);
            i.Register(g, "CreatePalette", CallConv.Stdcall, 1, c =>
            {
                var count = memory.Read16(c.Arg(0) + 2);
                var entries = new uint[count];
                for (var n = 0; n < count; n++) { var e = memory.Read32(c.Arg(0) + 4 + (uint)n * 4); entries[n] = e & 0xFFFFFF; }
                return NewGdi(new GdiObject { Kind = GdiKind.Palette, Entries = entries });
            });
            i.Register(g, "SelectPalette", CallConv.Stdcall, 3, c => DcSwap(c.Arg(0), d => d.Palette == 0 ? StockBase + 15 * 4 : d.Palette, (d, v) => d.Palette = v, c.Arg(1)));
            i.Register(g, "RealizePalette", CallConv.Stdcall, 1, c => 0);
            i.Register(g, "GetPaletteEntries", CallConv.Stdcall, 4, c =>
            {
                if (!gdiObjects.TryGetValue(c.Arg(0), out var pal) || pal.Entries == null) return 0;
                if (c.Arg(3) == 0) return (uint)pal.Entries.Length;
                uint n = 0;
                for (; n < c.Arg(2) && c.Arg(1) + n < pal.Entries.Length; n++) memory.Write32(c.Arg(3) + n * 4, pal.Entries[c.Arg(1) + n]);
                return n;
            });
            i.Register(g, "SetPaletteEntries", CallConv.Stdcall, 4, c =>
            {
                if (!gdiObjects.TryGetValue(c.Arg(0), out var pal) || pal.Entries == null) return 0;
                uint n = 0;
                for (; n < c.Arg(2) && c.Arg(1) + n < pal.Entries.Length; n++) pal.Entries[c.Arg(1) + n] = memory.Read32(c.Arg(3) + n * 4) & 0xFFFFFF;
                return n;
            });
            i.Register(g, "GetSystemPaletteEntries", CallConv.Stdcall, 4, c => 0);
            i.Register(g, "GetNearestColor", CallConv.Stdcall, 2, c => c.Arg(1) & 0xFFFFFF);
            i.Register(g, "GetNearestPaletteIndex", CallConv.Stdcall, 2, c => 0);

            InstallGdiText(i, g);

            // OpenGL's pixel-format calls: the console has no OpenGL; say so.
            i.Register(g, "ChoosePixelFormat", CallConv.Stdcall, 2, c => 0);
            i.Register(g, "SetPixelFormat", CallConv.Stdcall, 3, c => 0);
            i.Register(g, "DescribePixelFormat", CallConv.Stdcall, 4, c => 0);
            i.Register(g, "GetPixelFormat", CallConv.Stdcall, 1, c => 0);
            i.Register(g, "SwapBuffers", CallConv.Stdcall, 1, c => 0);
            i.Register(g, "GetDeviceGammaRamp", CallConv.Stdcall, 2, c =>
            {
                for (uint ch = 0; ch < 3; ch++)
                    for (uint n = 0; n < 256; n++) memory.Write16(c.Arg(1) + (ch * 256 + n) * 2, (ushort)(n * 257));
                return 1;
            });
            i.Register(g, "SetDeviceGammaRamp", CallConv.Stdcall, 2, c => 1);
        }

        private DeviceContext NewDc(bool screen)
        {
            return new DeviceContext
            {
                Screen = screen,
                Bitmap = screen ? 0 : defaultBitmap,
                Font = StockBase + 13 * 4,
                Brush = StockBase + 0 * 4,
                Pen = StockBase + 7 * 4,
            };
        }

        private uint NewDcHandle(bool screen)
        {
            var h = nextGdi;
            nextGdi += 4;
            dcs[h] = NewDc(screen);
            return h;
        }

        private uint DcSwap(uint hdc, Func<DeviceContext, uint> get, Action<DeviceContext, uint> set, uint value)
        {
            if (!dcs.TryGetValue(hdc, out var d)) return GdiError;
            var old = get(d);
            set(d, value);
            return old;
        }

        private uint PointSwap(uint hdc, uint x, uint y, uint old, Func<DeviceContext, (int, int)> get, Action<DeviceContext, int, int> set)
        {
            if (!dcs.TryGetValue(hdc, out var d)) return 0;
            var (ox, oy) = get(d);
            if (old != 0) { memory.Write32(old, (uint)ox); memory.Write32(old + 4, (uint)oy); }
            set(d, (int)x, (int)y);
            return 1;
        }

        private uint DeleteGdi(uint handle)
        {
            if (!gdiObjects.TryGetValue(handle, out var o)) return 0;
            if (o.Stock) return 1;
            foreach (var d in dcs.Values)
                if (d.Bitmap == handle || d.Font == handle || d.Brush == handle || d.Pen == handle) return 0;   // selected: refused, as GDI does
            if (o.Bitmap != null && o.Bitmap.Bits != 0 && !o.Bitmap.Section) heap.Free(o.Bitmap.Bits);
            if (o.Bitmap != null && o.Bitmap.Section) memory.Unmap(o.Bitmap.Bits, (uint)(o.Bitmap.Stride * o.Bitmap.Height));
            gdiObjects.Remove(handle);
            return 1;
        }

        private uint SelectGdi(uint hdc, uint handle)
        {
            if (!dcs.TryGetValue(hdc, out var d) || !gdiObjects.TryGetValue(handle, out var o)) return 0;
            uint old;
            switch (o.Kind)
            {
                case GdiKind.Bitmap:
                    if (d.Screen) return 0;
                    old = d.Bitmap; d.Bitmap = handle; return old;
                case GdiKind.Font: old = d.Font; d.Font = handle; return old;
                case GdiKind.Brush: old = d.Brush; d.Brush = handle; return old;
                case GdiKind.Pen: old = d.Pen; d.Pen = handle; return old;
                case GdiKind.Region: d.Region = handle; return 2;   // SIMPLEREGION
                default: return 0;
            }
        }

        private uint CurrentObject(uint hdc, uint type)
        {
            if (!dcs.TryGetValue(hdc, out var d)) return 0;
            switch (type)
            {
                case 1: return d.Pen;
                case 2: return d.Brush;
                case 5: return d.Palette == 0 ? StockBase + 15 * 4 : d.Palette;
                case 6: return d.Font;
                case 7: return d.Bitmap;
                default: return 0;
            }
        }

        private GdiBitmap DcBitmap(uint hdc) =>
            dcs.TryGetValue(hdc, out var d) && !d.Screen && gdiObjects.TryGetValue(d.Bitmap, out var o) ? o.Bitmap : null;

        // --- bitmaps -----------------------------------------------------------------------

        private uint NewBitmap(int width, int height, int bpp, uint initial, bool topDown, uint[] palette)
        {
            if (width <= 0 || height <= 0) return defaultBitmap;
            var stride = ((width * bpp + 31) / 32) * 4;
            var bits = heap.Alloc((uint)(stride * height), zero: true);
            if (bits == 0) { process.LastError = ErrorNotEnoughMemory; return 0; }
            var bmp = new GdiBitmap { Width = width, Height = height, Bpp = bpp, Stride = stride, Bits = bits, TopDown = true, Palette = palette };
            if (initial != 0)
            {
                // CreateBitmap's bits are rows padded to 16 bits, not 32.
                var sourceStride = ((width * bpp + 15) / 16) * 2;
                for (var y = 0; y < height; y++) MoveMemory(bits + (uint)(y * stride), initial + (uint)(y * sourceStride), (uint)sourceStride);
            }
            return NewGdi(new GdiObject { Kind = GdiKind.Bitmap, Bitmap = bmp });
        }

        private uint[] ReadColorTable(uint info, int bpp, uint used)
        {
            if (bpp > 8) return null;
            var count = used != 0 ? (int)used : 1 << bpp;
            var table = new uint[count];
            var at = info + memory.Read32(info);
            for (var n = 0; n < count; n++) table[n] = memory.Read32(at + (uint)n * 4) & 0xFFFFFF;
            return table;
        }

        /// <summary>CreateDIBSection: the pixels get their own pages, which the guest writes directly.</summary>
        private uint CreateDibSection(uint info, uint usage, uint bitsOut)
        {
            var width = (int)memory.Read32(info + 4);
            var height = (int)memory.Read32(info + 8);
            var bpp = memory.Read16(info + 14);
            if (width <= 0 || height == 0 || bpp == 0) { process.LastError = ErrorInvalidParameter; return 0; }
            var stride = ((width * bpp + 31) / 32) * 4;
            var size = (uint)(stride * Math.Abs(height));
            var bits = VirtualAlloc(0, size);
            if (bits == 0) { process.LastError = ErrorNotEnoughMemory; return 0; }
            var header = heap.Alloc(40);
            MoveMemory(header, info, 40);
            var bmp = new GdiBitmap
            {
                Width = width, Height = Math.Abs(height), Bpp = bpp, Stride = stride, Bits = bits,
                TopDown = height < 0, Section = true, Palette = ReadColorTable(info, bpp, memory.Read32(info + 32)), HeaderAt = header,
            };
            if (bitsOut != 0) memory.Write32(bitsOut, bits);
            return NewGdi(new GdiObject { Kind = GdiKind.Bitmap, Bitmap = bmp });
        }

        private uint RowAddress(GdiBitmap b, int y) => b.Bits + (uint)((b.TopDown ? y : b.Height - 1 - y) * b.Stride);

        /// <summary>A pixel as 0x00RRGGBB.</summary>
        private uint PixelAt(GdiBitmap b, int x, int y)
        {
            var row = RowAddress(b, y);
            switch (b.Bpp)
            {
                case 32: return memory.Read32(row + (uint)x * 4) & 0xFFFFFF;
                case 24: { var at = row + (uint)x * 3; return memory.Read8(at) | ((uint)memory.Read8(at + 1) << 8) | ((uint)memory.Read8(at + 2) << 16); }
                case 16:
                {
                    var v = memory.Read16(row + (uint)x * 2);
                    uint r = (uint)((v >> 10) & 31) * 255 / 31, gr = (uint)((v >> 5) & 31) * 255 / 31, bl = (uint)(v & 31) * 255 / 31;
                    return (r << 16) | (gr << 8) | bl;
                }
                case 8: return Palette(b, memory.Read8(row + (uint)x));
                case 4: return Palette(b, (memory.Read8(row + (uint)x / 2) >> (x % 2 == 0 ? 4 : 0)) & 15);
                case 1: return (memory.Read8(row + (uint)x / 8) >> (7 - x % 8) & 1) != 0 ? (b.Palette != null ? Palette(b, 1) : 0xFFFFFF) : (b.Palette != null ? Palette(b, 0) : 0);
                default: return 0;
            }
        }

        private static uint Palette(GdiBitmap b, int index) => b.Palette != null && index < b.Palette.Length ? b.Palette[index] : (uint)(index * 0x010101);

        private void PutPixel(GdiBitmap b, int x, int y, uint rgb)
        {
            if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return;
            var row = RowAddress(b, y);
            switch (b.Bpp)
            {
                case 32: memory.Write32(row + (uint)x * 4, rgb & 0xFFFFFF); break;
                case 24: { var at = row + (uint)x * 3; memory.Write8(at, (byte)rgb); memory.Write8(at + 1, (byte)(rgb >> 8)); memory.Write8(at + 2, (byte)(rgb >> 16)); break; }
                case 16: memory.Write16(row + (uint)x * 2, (ushort)((((rgb >> 19) & 31) << 10) | (((rgb >> 11) & 31) << 5) | ((rgb >> 3) & 31))); break;
                case 8: memory.Write8(row + (uint)x, NearestIndex(b, rgb)); break;
                case 1:
                {
                    var at = row + (uint)x / 8;
                    var bit = 1 << (7 - x % 8);
                    var on = (rgb & 0xFFFFFF) != 0 && (b.Palette == null || NearestIndex(b, rgb) == 1);
                    memory.Write8(at, (byte)(on ? memory.Read8(at) | bit : memory.Read8(at) & ~bit));
                    break;
                }
            }
        }

        private static byte NearestIndex(GdiBitmap b, uint rgb)
        {
            if (b.Palette == null) return (byte)(((rgb >> 16) & 0xFF) * 30 / 100 + ((rgb >> 8) & 0xFF) * 59 / 100 + (rgb & 0xFF) * 11 / 100);
            var best = 0; var bestDistance = int.MaxValue;
            for (var n = 0; n < b.Palette.Length; n++)
            {
                var p = b.Palette[n];
                int dr = (int)((p >> 16) & 0xFF) - (int)((rgb >> 16) & 0xFF), dg = (int)((p >> 8) & 0xFF) - (int)((rgb >> 8) & 0xFF), db = (int)(p & 0xFF) - (int)(rgb & 0xFF);
                var d = dr * dr + dg * dg + db * db;
                if (d < bestDistance) { best = n; bestDistance = d; }
            }
            return (byte)best;
        }

        /// <summary>A view of packed DIB bits described by a BITMAPINFO, for reading pixels.</summary>
        private GdiBitmap DibView(uint info, uint bits)
        {
            var width = (int)memory.Read32(info + 4);
            var height = (int)memory.Read32(info + 8);
            var bpp = memory.Read16(info + 14);
            return new GdiBitmap
            {
                Width = width, Height = Math.Abs(height), Bpp = bpp, Stride = ((width * bpp + 31) / 32) * 4, Bits = bits,
                TopDown = height < 0, Palette = ReadColorTable(info, bpp, memory.Read32(info + 32)),
            };
        }

        /// <summary>SetDIBits and friends: copies scan lines of a DIB into a bitmap at (x, y). Returns the lines copied.</summary>
        private int CopyDibIntoBitmap(GdiBitmap target, int x, int y, uint bits, uint info, int startLine, int lines)
        {
            var source = DibView(info, bits);
            var count = Math.Min(lines, source.Height - startLine);
            for (var row = 0; row < count; row++)
            {
                // Scan line n of a bottom-up DIB is its displayed row height-1-n.
                var sourceY = source.TopDown ? startLine + row : source.Height - 1 - (startLine + row);
                for (var col = 0; col < source.Width; col++) PutPixel(target, x + col, y + sourceY, PixelAt(source, col, sourceY));
            }
            return Math.Max(0, count);
        }

        private uint GetDiBits(uint hbm, uint start, uint lines, uint bits, uint info)
        {
            if (!gdiObjects.TryGetValue(hbm, out var o) || o.Bitmap == null) return 0;
            var b = o.Bitmap;
            if (bits == 0)
            {
                // Only the header is wanted.
                memory.Write32(info + 4, (uint)b.Width);
                memory.Write32(info + 8, (uint)b.Height);
                memory.Write16(info + 12, 1);
                memory.Write16(info + 14, (ushort)b.Bpp);
                memory.Write32(info + 16, 0);
                memory.Write32(info + 20, (uint)(b.Stride * b.Height));
                return (uint)b.Height;
            }
            var view = DibView(info, bits);
            if (view.Bpp == 0) return 0;
            var count = (int)Math.Min(lines, (uint)Math.Max(0, b.Height - (int)start));
            for (var row = 0; row < count; row++)
            {
                // Scan line n of a bottom-up DIB is the bitmap's row height-1-n.
                var sourceY = view.TopDown ? (int)start + row : b.Height - 1 - ((int)start + row);
                var targetY = view.TopDown ? row : view.Height - 1 - row;
                if (targetY < 0 || targetY >= view.Height) continue;
                for (var col = 0; col < Math.Min(view.Width, b.Width); col++) PutPixel(view, col, targetY, PixelAt(b, col, sourceY));
            }
            return (uint)count;
        }

        private uint ColorTable(uint hdc, uint start, uint count, uint colors, bool set)
        {
            var b = DcBitmap(hdc);
            if (b?.Palette == null) return 0;
            uint n = 0;
            for (; n < count && start + n < b.Palette.Length; n++)
            {
                if (set) b.Palette[start + n] = memory.Read32(colors + n * 4) & 0xFFFFFF;
                else memory.Write32(colors + n * 4, b.Palette[start + n]);
            }
            return n;
        }

        /// <summary>BitBlt / StretchBlt between memory DCs: SRCCOPY, the constant fills, and SRCAND/SRCPAINT/SRCINVERT.</summary>
        private uint Blit(uint hdcDest, int dx, int dy, int dw, int dh, uint hdcSrc, int sx, int sy, int sw, int sh, uint rop)
        {
            const uint SrcCopy = 0x00CC0020, SrcPaint = 0x00EE0086, SrcAnd = 0x008800C6, SrcInvert = 0x00660046;
            const uint Blackness = 0x00000042, Whiteness = 0x00FF0062, PatCopy = 0x00F00021, NotSrcCopy = 0x00330008;
            if (!dcs.ContainsKey(hdcDest)) return 0;
            var target = DcBitmap(hdcDest);
            if (target == null) return 1;   // the screen: Direct3D shows the frame
            if (rop == Blackness || rop == Whiteness || rop == PatCopy) return PatBlt(hdcDest, dx, dy, dw, dh, rop);
            var source = DcBitmap(hdcSrc);
            if (source == null || dw == 0 || dh == 0 || sw == 0 || sh == 0) return 1;

            // Same format, same size, SRCCOPY, 32 bits: whole rows at a time.
            if (rop == SrcCopy && dw == sw && dh == sh && source.Bpp == 32 && target.Bpp == 32 && dx >= 0 && dy >= 0 && sx >= 0 && sy >= 0 &&
                dx + dw <= target.Width && dy + dh <= target.Height && sx + sw <= source.Width && sy + sh <= source.Height)
            {
                for (var row = 0; row < dh; row++)
                    MoveMemory(RowAddress(target, dy + row) + (uint)dx * 4, RowAddress(source, sy + row) + (uint)sx * 4, (uint)dw * 4);
                return 1;
            }
            for (var row = 0; row < Math.Abs(dh); row++)
            {
                var ty = dy + (dh < 0 ? -row : row);
                if (ty < 0 || ty >= target.Height) continue;
                var syRow = sy + (int)((long)row * sh / dh);
                if (syRow < 0 || syRow >= source.Height) continue;
                for (var col = 0; col < Math.Abs(dw); col++)
                {
                    var tx = dx + (dw < 0 ? -col : col);
                    if (tx < 0 || tx >= target.Width) continue;
                    var sxCol = sx + (int)((long)col * sw / dw);
                    if (sxCol < 0 || sxCol >= source.Width) continue;
                    var s = PixelAt(source, sxCol, syRow);
                    uint result;
                    switch (rop)
                    {
                        case SrcCopy: result = s; break;
                        case NotSrcCopy: result = ~s & 0xFFFFFF; break;
                        case SrcAnd: result = s & PixelAt(target, tx, ty); break;
                        case SrcPaint: result = s | PixelAt(target, tx, ty); break;
                        case SrcInvert: result = s ^ PixelAt(target, tx, ty); break;
                        default: result = s; break;
                    }
                    PutPixel(target, tx, ty, result);
                }
            }
            return 1;
        }

        private uint PatBlt(uint hdc, int x, int y, int w, int h, uint rop)
        {
            if (!dcs.TryGetValue(hdc, out var d)) return 0;
            uint color;
            switch (rop)
            {
                case 0x00000042: color = 0; break;                   // BLACKNESS
                case 0x00FF0062: color = 0xFFFFFF; break;            // WHITENESS
                default: color = BrushColor(d, d.Brush); break;       // PATCOPY and the rest
            }
            if (w < 0) { x += w; w = -w; }
            if (h < 0) { y += h; h = -h; }
            FillArea(hdc, x, y, x + w, y + h, 0, color);
            return 1;
        }

        private uint BrushColor(DeviceContext d, uint brush)
        {
            if (brush == StockBase + 18 * 4) return Rgb(d.DcBrushColor);
            return gdiObjects.TryGetValue(brush, out var b) ? Rgb(b.Color) : 0xFFFFFF;
        }

        /// <summary>Fills a rectangle of a memory DC with a brush (or a given colour when brush is 0 and color is set).</summary>
        private void FillArea(uint hdc, int left, int top, int right, int bottom, uint brush, uint? color = null)
        {
            if (!dcs.TryGetValue(hdc, out var d)) return;
            var b = DcBitmap(hdc);
            if (b == null) return;
            if (brush == 0) brush = d.Brush;
            if (color == null && gdiObjects.TryGetValue(brush, out var bo) && bo.Style == 1) return;   // hollow
            var rgb = color ?? (brush <= 31 ? GetSysColorRgb(brush - 1) : BrushColor(d, brush));
            left = Math.Max(0, left); top = Math.Max(0, top);
            right = Math.Min(b.Width, right); bottom = Math.Min(b.Height, bottom);
            if (b.Bpp == 32)
            {
                var row = new byte[Math.Max(0, right - left) * 4];
                for (var n = 0; n < row.Length; n += 4) { row[n] = (byte)rgb; row[n + 1] = (byte)(rgb >> 8); row[n + 2] = (byte)(rgb >> 16); }
                for (var y = top; y < bottom; y++) memory.WriteBytes(RowAddress(b, y) + (uint)left * 4, row);
                return;
            }
            for (var y = top; y < bottom; y++)
                for (var x = left; x < right; x++) PutPixel(b, x, y, rgb);
        }

        /// <summary>FillRect's brush may be a system colour index plus one.</summary>
        private static uint GetSysColorRgb(uint index)
        {
            switch (index)
            {
                case 5: return 0xFFFFFF;    // COLOR_WINDOW
                case 8: return 0;           // COLOR_WINDOWTEXT
                case 15: return 0xF0F0F0;   // COLOR_BTNFACE
                case 1: return 0;           // COLOR_BACKGROUND
                default: return 0xC0C0C0;
            }
        }

        private uint StretchDiBits(GuestCall c)
        {
            // (hdc, xDest, yDest, destW, destH, xSrc, ySrc, srcW, srcH, bits, info, usage, rop)
            var target = DcBitmap(c.Arg(0));
            var info = c.Arg(10);
            var height = Math.Abs((int)memory.Read32(info + 8));
            if (target == null) return (uint)height;   // the screen
            var source = DibView(info, c.Arg(9));
            int dx = (int)c.Arg(1), dy = (int)c.Arg(2), dw = (int)c.Arg(3), dh = (int)c.Arg(4);
            int sx = (int)c.Arg(5), sy = (int)c.Arg(6), sw = (int)c.Arg(7), sh = (int)c.Arg(8);
            if (dw == 0 || dh == 0 || sw == 0 || sh == 0) return 0;
            for (var row = 0; row < Math.Abs(dh); row++)
            {
                var ty = dy + (dh < 0 ? -row : row);
                // The source rectangle's y is measured from the DIB's top as displayed.
                var syRow = sy + (int)((long)row * sh / dh);
                if (ty < 0 || ty >= target.Height || syRow < 0 || syRow >= source.Height) continue;
                for (var col = 0; col < Math.Abs(dw); col++)
                {
                    var tx = dx + (dw < 0 ? -col : col);
                    var sxCol = sx + (int)((long)col * sw / dw);
                    if (tx < 0 || tx >= target.Width || sxCol < 0 || sxCol >= source.Width) continue;
                    PutPixel(target, tx, ty, PixelAt(source, sxCol, syRow));
                }
            }
            return (uint)Math.Abs(dh);
        }

        // --- regions ---------------------------------------------------------------------------

        private uint NewRegion(int left, int top, int right, int bottom) =>
            NewGdi(new GdiObject { Kind = GdiKind.Region, Left = left, Top = top, Right = right, Bottom = bottom });

        /// <summary>Regions are kept as their bounding rectangles: AND intersects, the others take the union's box.</summary>
        private uint CombineRegion(uint destination, uint a, uint b, uint mode)
        {
            if (!gdiObjects.TryGetValue(destination, out var d) || !gdiObjects.TryGetValue(a, out var ra)) return 0;
            gdiObjects.TryGetValue(b, out var rb);
            if (mode == 5 || rb == null) { d.Left = ra.Left; d.Top = ra.Top; d.Right = ra.Right; d.Bottom = ra.Bottom; }   // RGN_COPY
            else if (mode == 1)
            {
                d.Left = Math.Max(ra.Left, rb.Left); d.Top = Math.Max(ra.Top, rb.Top);
                d.Right = Math.Min(ra.Right, rb.Right); d.Bottom = Math.Min(ra.Bottom, rb.Bottom);
            }
            else if (mode == 4) { d.Left = ra.Left; d.Top = ra.Top; d.Right = ra.Right; d.Bottom = ra.Bottom; }            // RGN_DIFF: approximated
            else
            {
                d.Left = Math.Min(ra.Left, rb.Left); d.Top = Math.Min(ra.Top, rb.Top);
                d.Right = Math.Max(ra.Right, rb.Right); d.Bottom = Math.Max(ra.Bottom, rb.Bottom);
            }
            return d.Right > d.Left && d.Bottom > d.Top ? 2u : 1u;
        }

        // --- GetObject ---------------------------------------------------------------------------

        private uint GetGdiObject(uint handle, uint size, uint buffer, bool wide)
        {
            if (!gdiObjects.TryGetValue(handle, out var o)) return 0;
            switch (o.Kind)
            {
                case GdiKind.Bitmap:
                {
                    var b = o.Bitmap;
                    var needed = b.Section && size >= 84 ? 84u : 24u;
                    if (buffer == 0) return needed;
                    if (size < 24) return 0;
                    memory.Write32(buffer, 0);
                    memory.Write32(buffer + 4, (uint)b.Width);
                    memory.Write32(buffer + 8, (uint)b.Height);
                    memory.Write32(buffer + 12, (uint)(b.Section ? b.Stride : ((b.Width * b.Bpp + 15) / 16) * 2));
                    memory.Write16(buffer + 16, 1);
                    memory.Write16(buffer + 18, (ushort)b.Bpp);
                    memory.Write32(buffer + 20, b.Section ? b.Bits : 0);
                    if (needed == 84)
                    {
                        MoveMemory(buffer + 24, b.HeaderAt, 40);
                        FillMemory(buffer + 64, 20, 0);
                    }
                    return needed;
                }
                case GdiKind.Font:
                {
                    var needed = wide ? 92u : 60u;
                    if (buffer == 0) return needed;
                    WriteLogFont(buffer, o, wide);
                    return needed;
                }
                case GdiKind.Pen:
                    if (buffer == 0) return 16;
                    memory.Write32(buffer, (uint)o.Style);
                    memory.Write32(buffer + 4, (uint)o.Width);
                    memory.Write32(buffer + 8, 0);
                    memory.Write32(buffer + 12, o.Color);
                    return 16;
                case GdiKind.Brush:
                    if (buffer == 0) return 12;
                    memory.Write32(buffer, (uint)(o.Style == 1 ? 1 : o.Style == 2 ? 2 : 0));
                    memory.Write32(buffer + 4, o.Color);
                    memory.Write32(buffer + 8, 0);
                    return 12;
                case GdiKind.Palette:
                    if (buffer != 0 && size >= 2) memory.Write16(buffer, (ushort)o.Entries.Length);
                    return 2;
                default:
                    return 0;
            }
        }
    }
}
