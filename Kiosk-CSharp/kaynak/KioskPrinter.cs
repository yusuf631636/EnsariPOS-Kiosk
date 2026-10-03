// Kiosk'un KENDI fis yazicisi (bazi kiosklarda dahili termal yazici olur) - 03.10.2026.
// Musteriye siparis fisi basar: isletme adi, SIPARIS NO (buyuk), siparis yonu/masa, urunler, toplam.
// Windows'ta kurulu yaziciya HAM ESC/POS gonderilir (winspool, RAW) -> Windows servisi (LocalSystem) icinden de
// calisir ve kagidi keser. Urun/fiyat bilgisi SambaPOS adisyonundan gelir (Samba.Order donusu), kiosktan degil.
// Ayarlar: kioskPrinter (yazici adi; bos = kapali), kioskPrinterChars (satir genisligi: 58mm=32, 80mm=42/48),
// kioskPrinterAscii ("true" = Turkce karakterleri sadelestir; yazici CP857 desteklemiyorsa), kioskPrinterFooter.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Alfa;

namespace Kiosk
{
    public static class KioskPrinter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        class DOCINFO { [MarshalAs(UnmanagedType.LPWStr)] public string pDocName; [MarshalAs(UnmanagedType.LPWStr)] public string pOutputFile; [MarshalAs(UnmanagedType.LPWStr)] public string pDataType; }
        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool OpenPrinter(string name, out IntPtr h, IntPtr pd);
        [DllImport("winspool.drv", SetLastError = true)] static extern bool ClosePrinter(IntPtr h);
        [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool StartDocPrinter(IntPtr h, int level, [In, MarshalAs(UnmanagedType.LPStruct)] DOCINFO di);
        [DllImport("winspool.drv", SetLastError = true)] static extern bool EndDocPrinter(IntPtr h);
        [DllImport("winspool.drv", SetLastError = true)] static extern bool StartPagePrinter(IntPtr h);
        [DllImport("winspool.drv", SetLastError = true)] static extern bool EndPagePrinter(IntPtr h);
        [DllImport("winspool.drv", SetLastError = true)] static extern bool WritePrinter(IntPtr h, byte[] buf, int count, out int written);

        static string V(string k) { return J.S(Cfg.Read(), k); }
        public static string Name { get { return V("kioskPrinter").Trim(); } }
        public static bool Enabled { get { return Name.Length > 0; } }
        static int Width { get { int w; return int.TryParse(V("kioskPrinterChars"), out w) && w >= 24 && w <= 64 ? w : 42; } }
        static bool Ascii { get { return V("kioskPrinterAscii") == "true"; } }

        // Bu bilgisayarda (servis hesabinin gordugu) kurulu yazicilar.
        public static List<object> List()
        {
            var l = new List<object>();
            try { foreach (string p in System.Drawing.Printing.PrinterSettings.InstalledPrinters) l.Add(p); } catch { }
            return l;
        }

        // ---- ESC/POS tampon ----
        class Buf
        {
            readonly List<byte> b = new List<byte>(); readonly Encoding enc; readonly bool ascii;
            public Buf(bool ascii)
            {
                this.ascii = ascii;
                enc = ascii ? Encoding.ASCII : Encoding.GetEncoding(857);   // PC857 = Turkce
                Raw(0x1B, 0x40);                                              // ESC @  sifirla
                if (!ascii) Raw(0x1B, 0x74, 13);                              // ESC t 13  kod sayfasi PC857
            }
            public Buf Raw(params byte[] x) { b.AddRange(x); return this; }
            public Buf Text(string s)
            {
                s = (s ?? "").Replace("₺", "TL").Replace("—", "-").Replace("•", "-").Replace("“", "\"").Replace("”", "\"");
                if (ascii) s = Tr2Ascii(s);
                b.AddRange(enc.GetBytes(s)); return this;
            }
            public Buf Line(string s) { return Text(s + "\n"); }
            public Buf Center() { return Raw(0x1B, 0x61, 1); }
            public Buf Left() { return Raw(0x1B, 0x61, 0); }
            public Buf Bold(bool on) { return Raw(0x1B, 0x45, (byte)(on ? 1 : 0)); }
            public Buf Size(int m) { byte n = (byte)(((m - 1) << 4) | (m - 1)); return Raw(0x1D, 0x21, n); }   // GS ! n  (m x m)
            public Buf Feed(int n) { return Raw(0x1B, 0x64, (byte)n); }
            public Buf Cut() { return Raw(0x1D, 0x56, 0x42, 0x00); }                                         // GS V 66 0  besle + kes
            public byte[] Bytes() { return b.ToArray(); }
        }
        static string Tr2Ascii(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case 'ç': sb.Append('c'); break; case 'Ç': sb.Append('C'); break; case 'ğ': sb.Append('g'); break; case 'Ğ': sb.Append('G'); break;
                    case 'ı': sb.Append('i'); break; case 'İ': sb.Append('I'); break; case 'ö': sb.Append('o'); break; case 'Ö': sb.Append('O'); break;
                    case 'ş': sb.Append('s'); break; case 'Ş': sb.Append('S'); break; case 'ü': sb.Append('u'); break; case 'Ü': sb.Append('U'); break;
                    default: sb.Append(c < 128 ? c : '?'); break;
                }
            }
            return sb.ToString();
        }
        static string Trunc(string s, int w) { s = s ?? ""; return s.Length > w ? s.Substring(0, w) : s; }
        static string Row(string l, string r, int w) { l = l ?? ""; r = r ?? ""; if (l.Length + r.Length + 1 > w) l = l.Substring(0, Math.Max(0, w - r.Length - 1)); return l + new string(' ', Math.Max(1, w - l.Length - r.Length)) + r; }
        static string Money(double v) { return v.ToString(Math.Abs(v % 1) < 0.005 ? "0" : "0.00", J.Inv) + " TL"; }
        static string Qty(double q) { return q.ToString(Math.Abs(q % 1) < 0.005 ? "0" : "0.##", J.Inv); }

        // order: Samba.Order donusu {orderNo, total, lines:[{name, quantity, price(birim, etiketler dahil), tags:[{tag}]}]}
        public static Dictionary<string, object> PrintOrder(Dictionary<string, object> order, string dest, string table)
        {
            if (!Enabled) return J.Obj("ok", false, "skipped", true);
            int w = Width; var c = Cfg.Read();
            string biz = J.S(c, "name").Length > 0 ? J.S(c, "name") : "EnsariPOS Kiosk";
            string no = J.S(order, "orderNo"); if (no.Length == 0) no = "-";
            var p = new Buf(Ascii);
            p.Center().Bold(true).Size(2).Line(Trunc(biz, w / 2)).Size(1).Bold(false);
            p.Line(DateTime.Now.ToString("dd.MM.yyyy HH:mm"));
            p.Line(new string('-', w));
            p.Line("SİPARİŞ NO");
            p.Bold(true).Size(4).Line(no).Size(1).Bold(false);
            string where = ((dest ?? "") + (string.IsNullOrEmpty(table) ? "" : " - " + table)).Trim();
            if (where.Length > 0) p.Bold(true).Size(2).Line(Trunc(where, w / 2)).Size(1).Bold(false);
            p.Left().Line(new string('-', w));
            foreach (var o in J.LL(J.Get(order, "lines")))
            {
                var od = J.DD(o); double q = J.Num(od, "quantity"); if (q <= 0) q = 1;
                p.Line(Row(Qty(q) + "x " + J.S(od, "name"), Money(J.Num(od, "price") * q), w));
                foreach (var t in J.LL(J.Get(od, "tags"))) p.Line("   + " + Trunc(J.S(J.DD(t), "tag"), w - 5));
            }
            p.Line(new string('-', w));
            p.Bold(true).Size(2).Line(Row("TOPLAM", Money(J.Num(order, "total")), w / 2)).Size(1).Bold(false);
            p.Center().Line("");
            string pm = J.S(c, "payMode");
            if (pm.Length == 0 || pm == "counter") p.Bold(true).Line("Lütfen ödemeyi kasada yapınız").Bold(false);
            string foot = J.S(c, "kioskPrinterFooter"); if (foot.Length > 0) p.Line(foot);
            p.Line("Afiyet olsun!");
            p.Feed(4).Cut();
            return Send(p.Bytes(), "Kiosk Siparis " + no);
        }

        public static Dictionary<string, object> Test()
        {
            if (!Enabled) return J.Obj("ok", false, "error", "Önce bir yazıcı seçin ve Kaydet'e basın.");
            var order = J.Obj("orderNo", "TEST", "total", J.NumVal(150),
                "lines", new List<object> {
                    J.Obj("name", "Deneme Ürünü (Çorba)", "quantity", J.NumVal(1), "price", J.NumVal(100), "tags", new List<object> { J.Obj("tag", "Acısız") }),
                    J.Obj("name", "Ayran", "quantity", J.NumVal(2), "price", J.NumVal(25), "tags", new List<object>()) });
            return PrintOrder(order, "Test fişi", "");
        }

        static Dictionary<string, object> Send(byte[] data, string doc)
        {
            IntPtr h;
            if (!OpenPrinter(Name, out h, IntPtr.Zero)) return J.Obj("ok", false, "error", "Yazıcı açılamadı: \"" + Name + "\" (bu bilgisayarda kurulu mu? Kiosk servisi bu yazıcıyı görebiliyor mu?)");
            try
            {
                var di = new DOCINFO { pDocName = doc, pDataType = "RAW" };
                if (!StartDocPrinter(h, 1, di)) return J.Obj("ok", false, "error", "Yazdırma başlatılamadı (Windows hata " + Marshal.GetLastWin32Error() + ").");
                try
                {
                    StartPagePrinter(h);
                    int wr; bool ok = WritePrinter(h, data, data.Length, out wr);
                    EndPagePrinter(h);
                    if (!ok || wr != data.Length) return J.Obj("ok", false, "error", "Yazıcıya veri gönderilemedi.");
                }
                finally { EndDocPrinter(h); }
                return J.Obj("ok", true, "info", "Fiş \"" + Name + "\" yazıcısına gönderildi.");
            }
            finally { ClosePrinter(h); }
        }
    }
}
