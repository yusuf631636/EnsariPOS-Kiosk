// Kiosk'ta ODEME (03.10.2026) - iki yontem, panelden ayarlar girilince calisir:
//  1) QR ile kartla odeme = iyzico Checkout Form. Kiosk QR gosterir, musteri telefonunda kartla oder; kiosk
//     token ile sonucu SORGULAR (yerel kioska callback gelmesi gerekmez). Musterinin telefonu odemeden sonra
//     bulut sayfasina doner: https://app.ornek-alanadi.com/kiosk/odeme-sonuc (sadece bilgilendirme).
//  2) Kiosk'a bagli kart terminali = marka adaptoru (IPos). Su an "Simulasyon (test)" calisir; Ingenico/Beko/
//     Hugin/Pavo/Verifone icin uretici SDK'si / GMP3 ile BrandPos.Sale() doldurulacak (yer hazir).
// GUVENLIK: tutar SUNUCUDA SambaPOS fiyatlarindan hesaplanir (kiosk ekrani herkese acik); siparis YALNIZCA odeme
// sunucuda dogrulaninca acilir ve SambaPOS'ta payTerminalTicket ile "odendi" kapanir. Bekleyen QR odemeleri 35 dk
// arka planda izlenir -> musteri kiosk ekranini iptal ettikten sonra oderse bile siparis kaybolmaz (loglanir).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Alfa;

namespace Kiosk
{
    public static class Odeme
    {
        static string V(string k) { return J.S(Cfg.Read(), k); }
        static string Or(string v, string d) { return string.IsNullOrEmpty(v) ? d : v; }

        // ---- ayarlar ----
        public static bool Online { get { return V("payMode") == "online"; } }
        public static bool QrReady { get { return V("iyzicoEnabled") == "true" && V("iyzicoApiKey").Length > 0 && V("iyzicoSecret").Length > 0; } }
        static readonly string[] Implemented = { "sim" };   // entegrasyonu TAMAM olan terminal markalari
        public static bool PosReady { get { return Implemented.Contains(V("posBrand")); } }
        static string IyzBase { get { return V("iyzicoMode") == "live" ? "https://api.iyzipay.com" : "https://sandbox-api.iyzipay.com"; } }

        // Kiosk "Nasil odemek istersiniz?" secenekleri (payMode=online). Etiket/ikon payOptions'ta ayni anahtarla ezilebilir.
        public static List<object> Methods()
        {
            var over = new Dictionary<string, Dictionary<string, object>>();
            try { foreach (var o in J.LL(J.Parse(V("payOptions")))) { var d = J.DD(o); if (d != null && J.S(d, "key").Length > 0) over[J.S(d, "key")] = d; } } catch { }
            var l = new List<object>();
            Action<string, string> add = (k, lbl) => {
                Dictionary<string, object> d; string icon = "";
                if (over.TryGetValue(k, out d)) { if (J.S(d, "label").Length > 0) lbl = J.S(d, "label"); icon = J.S(d, "icon"); }
                l.Add(J.Obj("key", k, "label", lbl, "icon", icon));
            };
            if (V("payAllowCounter") != "false") add("counter", "Kasada Öde");
            if (QrReady) add("qr", "QR ile Kartla Öde");
            if (PosReady) add("terminal", "Kartla Öde");
            return l;
        }

        // ---- tutar: SambaPOS urun fiyati + SambaPOS'ta tanimli secenek (etiket) fiyatlari ----
        public static double Total(List<object> items)
        {
            var prices = Samba.ProductPrices(); double tot = 0;
            foreach (var it in items)
            {
                var d = J.DD(it); long pid = (long)J.Num(d, "productId"); double q = J.Num(d, "quantity"); if (q <= 0) q = 1;
                double p; if (pid <= 0 || !prices.TryGetValue(pid, out p)) throw new Exception("Ürün fiyatı bulunamadı (" + pid + ").");
                var sel = J.LL(J.Get(d, "tags"));
                if (sel.Count > 0)
                {
                    var groups = Samba.OrderTagGroups(pid); var seen = new HashSet<string>();
                    foreach (var s in sel)
                    {
                        var sd = J.DD(s); string g = J.S(sd, "group"), t = J.S(sd, "tag"); if (!seen.Add(g + "|" + t)) continue;
                        foreach (var gg in groups) { var gd = J.DD(gg); if (J.S(gd, "name") != g) continue; foreach (var tt in J.LL(J.Get(gd, "tags"))) { var td = J.DD(tt); if (J.S(td, "name") == t) p += J.Num(td, "price"); } }
                    }
                }
                tot += p * q;
            }
            return Math.Round(tot, 2);
        }

        // ---- bekleyen odemeler ----
        class Pay
        {
            public string Id, Method, Token, Url, Status = "pending", OrderNo = "", Error = "", Table, Dept, Tt, Et, Label;
            public List<object> Items; public double Amount;
            public DateTime Created = DateTime.UtcNow, LastCheck = DateTime.MinValue;
            public volatile bool Cancelled, Busy;
        }
        static readonly Dictionary<string, Pay> _pays = new Dictionary<string, Pay>();
        static readonly object _pl = new object();
        static Timer _timer;
        public static Action<Dictionary<string, object>, string, string> OnPaidOrder;   // Program: kiosk fis yazicisi

        public static Dictionary<string, object> Start(string method, List<object> items, string table, string dept, string tt, string et, string label)
        {
            if (items == null || items.Count == 0) throw new Exception("Sepet boş.");
            if (method == "qr") { if (!QrReady) throw new Exception("QR ödeme ayarlı değil."); }
            else if (method == "terminal") { if (!PosReady) throw new Exception("Kart terminali ayarlı değil."); }
            else throw new Exception("Geçersiz ödeme yöntemi.");
            double amount = Total(items);
            if (!(amount > 0)) throw new Exception("Tutar sıfır.");
            var p = new Pay { Id = "K" + J.NowMs().ToString() + Crypto.RandomHex(3), Method = method, Items = items, Table = table ?? "", Dept = dept, Tt = tt, Et = et, Label = label ?? "", Amount = amount };
            if (method == "qr")
            {
                var r = IyzInit(p);
                p.Token = J.S(r, "token"); p.Url = J.S(r, "paymentPageUrl");
                if (J.S(r, "status") != "success" || p.Token.Length == 0 || p.Url.Length == 0) throw new Exception("iyzico ödeme başlatılamadı: " + Or(J.S(r, "errorMessage"), "bilinmeyen hata"));
            }
            lock (_pl) { _pays[p.Id] = p; if (_timer == null) _timer = new Timer(_ => Tick(), null, 4000, 4000); }
            if (method == "terminal") ThreadPool.QueueUserWorkItem(_ => RunTerminal(p));
            Log.Write("[odeme] başladı " + p.Id + " " + method + " " + amount.ToString("0.00", J.Inv) + " TL");
            return J.Obj("id", p.Id, "method", method, "amount", J.NumVal(amount), "url", p.Url ?? "", "expiresIn", 300);
        }

        public static Dictionary<string, object> Status(string id)
        {
            Pay p; lock (_pl) { _pays.TryGetValue(id ?? "", out p); }
            if (p == null) return J.Obj("status", "unknown");
            if (p.Method == "qr" && p.Status == "pending" && (DateTime.UtcNow - p.LastCheck).TotalSeconds > 3) Check(p);   // kiosk beklerken hizli kontrol
            return J.Obj("status", p.Status, "orderNo", p.OrderNo, "error", p.Error, "amount", J.NumVal(p.Amount));
        }

        // Kiosk ekrani vazgecti. QR: arka plan kontrolu 35 dk surer (sonradan odenirse siparis yine acilir).
        // Terminal: henuz onaylanmadiysa adaptore iptal sinyali gider.
        public static Dictionary<string, object> Cancel(string id)
        {
            Pay p; lock (_pl) { _pays.TryGetValue(id ?? "", out p); }
            if (p != null && p.Status == "pending") { p.Cancelled = true; Log.Write("[odeme] kiosk ekranı iptal etti " + p.Id); }
            return J.Obj("ok", true);
        }

        static void Tick()
        {
            List<Pay> list; lock (_pl) { list = _pays.Values.ToList(); }
            foreach (var p in list)
            {
                if (p.Method == "qr" && p.Status == "pending" && (DateTime.UtcNow - p.LastCheck).TotalSeconds > 10) Check(p);
                if ((DateTime.UtcNow - p.Created).TotalMinutes > 90) lock (_pl) { _pays.Remove(p.Id); }
            }
        }

        static void Check(Pay p)
        {
            lock (p) { if (p.Status != "pending" || p.Busy) return; p.Busy = true; }
            try
            {
                p.LastCheck = DateTime.UtcNow;
                var r = IyzPost("/payment/iyzipos/checkoutform/auth/ecom/detail", J.Str(J.Obj("locale", "tr", "conversationId", p.Id, "token", p.Token)));
                string ps = J.S(r, "paymentStatus").ToUpperInvariant();
                double fraud = J.Has(r, "fraudStatus") ? J.Num(r, "fraudStatus") : 1;
                if (J.S(r, "status") == "success" && ps == "SUCCESS" && fraud >= 0)
                {
                    double paid = J.Num(r, "paidPrice");
                    if (paid > 0 && Math.Abs(paid - p.Amount) > 0.01) Log.Write("[odeme] UYARI tutar farkı " + p.Id + ": beklenen " + p.Amount.ToString(J.Inv) + " ödenen " + paid.ToString(J.Inv));
                    if (fraud == 0) Log.Write("[odeme] UYARI " + p.Id + " iyzico fraud incelemesinde (ödeme alındı).");
                    Complete(p, Or(V("iyzicoPayType"), "Kredi Kartı"), "iyzico " + J.S(r, "paymentId"));
                }
                else if (ps == "FAILURE") { p.Status = "failed"; p.Error = Or(J.S(r, "errorMessage"), "Ödeme reddedildi."); }
                else if ((DateTime.UtcNow - p.Created).TotalMinutes > 35) p.Status = "expired";
            }
            catch (Exception e) { Log.Write("[odeme] kontrol hatası " + p.Id + ": " + e.Message); }
            finally { p.Busy = false; }
        }

        static void RunTerminal(Pay p)
        {
            try
            {
                var res = Pos().Sale(p.Amount, p.Id, () => p.Cancelled);
                if (J.IsTrue(res, "ok")) Complete(p, Or(V("posPayType"), "Kredi Kartı"), "POS " + V("posBrand") + " " + J.S(res, "auth"));
                else { p.Status = p.Cancelled ? "cancelled" : "failed"; p.Error = Or(J.S(res, "error"), "Ödeme onaylanmadı."); }
            }
            catch (Exception e) { p.Status = "failed"; p.Error = e.Message; }
        }

        // Para alindi -> SambaPOS siparisi (odenmis) + fis. Siparis acilamazsa 1 kez daha denenir, olmazsa personele net uyari.
        static void Complete(Pay p, string payType, string desc)
        {
            var payment = J.Obj("type", payType, "amount", J.NumVal(p.Amount), "desc", "Kiosk " + desc);
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    var r = Samba.Order(p.Items, p.Table, p.Dept, p.Tt, p.Et, payment);
                    p.OrderNo = J.S(r, "orderNo"); p.Status = "paid";
                    Log.Write("[odeme] ÖDENDİ " + p.Id + " -> sipariş " + p.OrderNo + (p.Cancelled ? " (kiosk ekranı iptal ettikten SONRA ödendi)" : ""));
                    var cb = OnPaidOrder; if (cb != null) try { cb(r, p.Label, p.Table); } catch { }
                    return;
                }
                catch (Exception e)
                {
                    if (attempt == 2)
                    {
                        p.Status = "paid_noorder"; p.Error = "Ödemeniz alındı ancak sipariş mutfağa iletilemedi. Lütfen kasaya bu numarayı söyleyin: " + p.Id;
                        Log.Write("[odeme] KRİTİK " + p.Id + " " + p.Amount.ToString("0.00", J.Inv) + " TL ÖDENDİ, sipariş açılamadı: " + e.Message);
                    }
                    else Thread.Sleep(3000);
                }
            }
        }

        // ---- iyzico (IYZWSv2: HMACSHA256(randomKey + uriPath + body, secretKey) hex) ----
        static Dictionary<string, object> IyzPost(string path, string body)
        {
            string rnd = J.NowMs().ToString() + Crypto.RandomHex(4), key = V("iyzicoApiKey"), sec = V("iyzicoSecret"), sig;
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(sec)))
                sig = BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(rnd + path + body))).Replace("-", "").ToLowerInvariant();
            string auth = "IYZWSv2 " + Convert.ToBase64String(Encoding.UTF8.GetBytes("apiKey:" + key + "&randomKey:" + rnd + "&signature:" + sig));
            var r = Web.Request("POST", IyzBase + path, body, new Dictionary<string, string> {
                { "Content-Type", "application/json" }, { "Accept", "application/json" }, { "Authorization", auth }, { "x-iyzi-rnd", rnd } }, 20000);
            if (r.Status == 0) throw new Exception("iyzico'ya ulaşılamadı (internet bağlantısını kontrol edin).");
            var j = J.DD(J.Parse(r.Text));
            if (j == null) throw new Exception("iyzico beklenmeyen yanıt verdi (HTTP " + r.Status + ").");
            return j;
        }
        static Dictionary<string, object> IyzInit(Pay p)
        {
            string amt = p.Amount.ToString("0.00", J.Inv), biz = Or(J.S(Cfg.Read(), "name"), "EnsariPOS Kiosk"), city = Or(V("iyzicoCity"), "Istanbul");
            var addr = J.Obj("contactName", "Kiosk Musteri", "city", city, "country", "Turkey", "address", biz);
            var body = J.Obj("locale", "tr", "conversationId", p.Id, "price", amt, "paidPrice", amt, "currency", "TRY", "basketId", p.Id, "paymentGroup", "PRODUCT",
                "callbackUrl", Or(V("iyzicoCallback"), "https://app.ornek-alanadi.com/kiosk/odeme-sonuc"), "enabledInstallments", new List<object> { 1 },
                "buyer", J.Obj("id", "kiosk", "name", "Kiosk", "surname", "Musteri", "gsmNumber", "+905000000000", "email", Or(V("iyzicoEmail"), "kiosk@ornek-alanadi.com"),
                    "identityNumber", "11111111111", "registrationAddress", biz, "city", city, "country", "Turkey"),
                "shippingAddress", addr, "billingAddress", addr,
                "basketItems", new List<object> { J.Obj("id", p.Id, "name", "Kiosk siparisi", "category1", "Yiyecek", "itemType", "PHYSICAL", "price", amt) });
            return IyzPost("/payment/iyzipos/checkoutform/initialize/auth/ecom", J.Str(body));
        }

        public static Dictionary<string, object> TestQr()
        {
            if (V("iyzicoApiKey").Length == 0 || V("iyzicoSecret").Length == 0) return J.Obj("ok", false, "error", "API anahtarı ve gizli anahtar girilmeli.");
            try
            {
                var r = IyzPost("/payment/bin/check", J.Str(J.Obj("locale", "tr", "conversationId", "kiosk-test", "binNumber", "554960")));
                if (J.S(r, "status") == "success") return J.Obj("ok", true, "info", "iyzico bağlantısı başarılı (" + (V("iyzicoMode") == "live" ? "CANLI" : "test / sandbox") + ")." + (V("iyzicoEnabled") == "true" ? "" : " Kioskta görünmesi için 'Açık' yapın."));
                return J.Obj("ok", false, "error", Or(J.S(r, "errorMessage"), "iyzico isteği reddetti."));
            }
            catch (Exception e) { return J.Obj("ok", false, "error", e.Message); }
        }
        public static Dictionary<string, object> TestPos()
        {
            if (V("posBrand").Length == 0) return J.Obj("ok", false, "error", "Terminal markası seçilmedi.");
            return Pos().Test();
        }

        // ---- kart terminali adaptorleri ----
        public interface IPos
        {
            Dictionary<string, object> Sale(double amount, string refNo, Func<bool> cancelled);   // {ok, auth} / {ok:false, error}
            Dictionary<string, object> Test();
        }
        // Gercek para cekmeden akisi denemek icin: 4 sn sonra onaylar (iptal edilirse vazgecer).
        class SimPos : IPos
        {
            public Dictionary<string, object> Sale(double amount, string refNo, Func<bool> cancelled)
            {
                for (int i = 0; i < 40; i++) { if (cancelled()) return J.Obj("ok", false, "error", "İptal edildi."); Thread.Sleep(100); }
                return J.Obj("ok", true, "auth", "SIM" + Crypto.RandomHex(3).ToUpperInvariant());
            }
            public Dictionary<string, object> Test() { return J.Obj("ok", true, "info", "Simülasyon terminali hazır — ödeme 4 sn sonra otomatik onaylanır (gerçek para çekilmez). Canlı kullanımdan önce gerçek markayı seçin."); }
        }
        // Gercek cihaz markalari: uretici SDK'si / GMP3 protokolu ile Sale() doldurulacak. Baglanti: posConn (COM3 / 192.168.1.50:5000).
        class BrandPos : IPos
        {
            readonly string brand; public BrandPos(string b) { brand = b; }
            public Dictionary<string, object> Sale(double amount, string refNo, Func<bool> cancelled)
            { return J.Obj("ok", false, "error", brand + " kart terminali entegrasyonu henüz eklenmedi (üretici SDK'sı gerekli)."); }
            public Dictionary<string, object> Test()
            { return J.Obj("ok", false, "error", brand + " entegrasyonu henüz eklenmedi — cihazın SDK'sı ile eklenecek (bağlantı: " + Or(V("posConn"), "girilmedi") + "). Şimdilik QR ödeme ya da Simülasyon kullanın."); }
        }
        static IPos Pos() { string b = V("posBrand"); if (b == "sim") return new SimPos(); return new BrandPos(b); }
    }
}
