// EnsariPOS Kiosk - bagimsiz self-servis siparis kiosk'u (03.10.2026).
// AYRI urun, AYRI bulut "kiosk" lisansi (restoranin ana lisansi DEGIL). QR Menu'ye DOKUNMAZ.
// Ekran herkese acik -> SambaPOS kullanici/sifresi sunucuda kalir (Samba.cs proxy). Siparis "gel-al".
// Odeme simdilik alinmaz ama altyapi hazir (payMode) - ileride odeme + yazar kasa (OKC) entegrasyonu buraya.
// Dogrudan calistirma: servis :8790'da sunar; kiosk-baslat.bat Edge'i tam ekran (--kiosk) acar.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Alfa;

[assembly: System.Reflection.AssemblyTitle("EnsariPOS Kiosk Sunucusu")]
[assembly: System.Reflection.AssemblyProduct("EnsariPOS Kiosk")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace Kiosk
{
    public static class Program
    {
        public const string ServiceName = "EnsariKiosk";
        public static Dictionary<string, string> Opts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string Opt(string k) { string v; return Opts.TryGetValue(k, out v) && v.Length > 0 ? v : null; }
        public static void Main(string[] args)
        {
            foreach (var a in args) { string s = a.TrimStart('/', '-'); int i = s.IndexOf(':'); if (i > 0) Opts[s.Substring(0, i)] = s.Substring(i + 1); else Opts[s] = ""; }
            if (!(Opts.ContainsKey("console") || Environment.UserInteractive)) { ServiceBase.Run(new Svc()); return; }
            try { App.Start(); } catch (Exception ex) { Console.WriteLine("BASLATILAMADI: " + ex); Environment.ExitCode = 1; return; }
            Console.WriteLine("EnsariPOS Kiosk (konsol) http://127.0.0.1:" + App.Port + " - Ctrl+C ile durdurun");
            Thread.Sleep(Timeout.Infinite);
        }
    }
    public class Svc : ServiceBase
    {
        public Svc() { ServiceName = Program.ServiceName; CanStop = true; CanShutdown = true; }
        protected override void OnStart(string[] args) { App.Start(); }
        protected override void OnStop() { App.Stop(); }
        protected override void OnShutdown() { App.Stop(); }
    }

    public static class App
    {
        public const string Version = "1.0.0";
        public static string Root, RuntimeDir;
        public static int Port = 8790;
        static string CloudUrl = "https://app.ornek-alanadi.com";
        static LocalHost _host;

        public static void Start()
        {
            Web.Init();
            Root = Path.GetFullPath(Program.Opt("root") ?? AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\');
            string pd = Environment.GetEnvironmentVariable("PROGRAMDATA") ?? Root;
            RuntimeDir = Program.Opt("data") ?? Path.Combine(pd, "EnsariPOS", "Kiosk");
            Directory.CreateDirectory(RuntimeDir);
            Log.Init(Program.Opt("logs") ?? Path.Combine(RuntimeDir, "logs"));
            Cfg.PathFile = Path.Combine(Root, "config.json");
            var c = Cfg.Read();
            int p; Port = int.TryParse(Program.Opt("port"), out p) ? p : (int)J.NumOr(J.Get(c, "port"), 8790);
            if (J.S(c, "cloudServerUrl").Length > 0) CloudUrl = J.S(c, "cloudServerUrl").TrimEnd('/');
            // AYRI kiosk lisansi: urun "kiosk", anahtar kioskActivationKey (ana lisanstan bagimsiz).
            License.Init("kiosk", J.S(c, "kioskActivationKey"), CloudUrl, false);
            Updater.Root = Root; Updater.Channel = "kiosk-update"; Updater.DefaultVersion = Version; Updater.Never = new[] { "config.json" };
            if (!Program.Opts.ContainsKey("noupdate")) Updater.Start(Program.Opts.ContainsKey("updatenow"));
            Odeme.OnPaidOrder = PrintAsync;   // kioskta odenen siparislerde de musteri fisi basilir
            _host = new LocalHost(Port, Handle, Program.Opt("bind") ?? "127.0.0.1");
            _host.Start();
            Log.Write("EnsariPOS Kiosk " + Version + ": http://127.0.0.1:" + Port + "  veri: " + RuntimeDir + "  lisans: " + (License.IsLicensed ? "aktif" : "pasif"));
        }
        public static void Stop() { try { _host.Stop(); } catch { } Log.Write("Kiosk durduruldu."); }

        // ---------------------------------------------------------------- HTTP
        static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            {".html","text/html; charset=utf-8"},{".js","application/javascript; charset=utf-8"},{".css","text/css; charset=utf-8"},
            {".png","image/png"},{".jpg","image/jpeg"},{".jpeg","image/jpeg"},{".svg","image/svg+xml"},{".ico","image/x-icon"},{".webmanifest","application/manifest+json"},{".json","application/json"}
        };
        static readonly HashSet<string> PublicPaths = new HashSet<string> { "/", "/index.html", "/api/menu", "/api/order", "/admin", "/admin.html", "/api/admin/login", "/lisans.html", "/favicon.ico" };
        static Dictionary<string, object> Err(string m) { return J.Obj("error", m); }
        static readonly ConcurrentDictionary<string, long> _adminTok = new ConcurrentDictionary<string, long>();

        public static void Handle(Req req, Res res)
        {
            try { Route(req, res); }
            catch (Exception ex) { Log.Write("HATA " + req.Method + " " + req.Path + ": " + ex.Message); res.Headers.Clear(); res.Body.SetLength(0); res.Json(500, Err(ex.Message)); }
        }

        static bool AdminOk(Req req) { long exp; return _adminTok.TryGetValue(req.Cookie("kiosk_admin") ?? "", out exp) && exp > J.NowMs(); }

        static void Route(Req req, Res res)
        {
            string path = req.Path; bool POST = req.Method == "POST";

            // ---- kiosk ekrani verisi ----
            if (path == "/api/menu")
            {
                var c = Cfg.Read();
                var names = Samba.MenuNames();
                var cfgO = J.Obj("name", J.S(c, "name").Length > 0 ? J.S(c, "name") : "EnsariPOS Kiosk",
                    "logo", J.S(c, "logo"), "payMode", J.S(c, "payMode").Length > 0 ? J.S(c, "payMode") : "counter",
                    "orderMode", J.S(c, "orderMode").Length > 0 ? J.S(c, "orderMode") : "gelal",
                    "languages", J.S(c, "languages").Length > 0 ? J.S(c, "languages") : "tr",
                    "licensed", License.IsLicensed, "menus", names);
                cfgO["destinations"] = Destinations().Select(d => (object)J.Obj("label", J.S(d, "label"), "icon", J.S(d, "icon"), "iconImg", J.S(d, "iconImg"), "table", AskNumber(d))).ToList();
                cfgO["theme"] = J.S(c, "themeColor"); cfgO["themeMode"] = J.S(c, "themeMode");
                cfgO["slider"] = ParseArr(J.S(c, "sliderImages"));
                cfgO["payOptions"] = Odeme.Online ? Odeme.Methods() : PayOptions();   // online: kasada/QR/terminal (hazir olanlar)
                cfgO["printer"] = KioskPrinter.Enabled;                         // kiosk fis yazicisi var -> "fisinizi alin"
                cfgO["easyMode"] = J.S(c, "easyMode") == "true";                 // kolay mod: dokun-ekle, odeme/oneri ekrani yok
                cfgO["featured"] = ParseArr(J.S(c, "featuredProducts"));        // one cikan urun anahtarlari (productId)
                cfgO["upsell"] = ParseArr(J.S(c, "upsellProducts"));            // sepette onerilecek urun anahtarlari
                var outp = J.Obj("config", cfgO);
                if (!License.IsLicensed) { outp["categories"] = new List<object>(); res.Json(200, outp); return; }
                if (!Samba.HasConn()) { outp["categories"] = new List<object>(); outp["error"] = "SambaPOS bağlantısı ayarlanmamış."; res.Json(200, outp); return; }
                string sel = req.Q("menu");
                if (names.Count > 1 && string.IsNullOrEmpty(sel)) { outp["categories"] = new List<object>(); outp["chooseMenu"] = true; res.Json(200, outp); return; }
                try { var m = Samba.MenuWithPrices(sel); string lang = req.Q("lang"); if (!string.IsNullOrEmpty(lang) && lang != "tr") m = Ceviri.TranslateMenu(m, lang); outp["categories"] = J.Get(m, "categories"); outp["menu"] = string.IsNullOrEmpty(sel) ? names[0] : sel; }
                catch (Exception e) { outp["categories"] = new List<object>(); outp["error"] = e.Message; }
                res.Json(200, outp);
                return;
            }
            // Urun secenekleri (SambaPOS siparis etiketleri) - kiosk urun detayinda gosterir.
            if (path == "/api/product-options")
            {
                long pid; long.TryParse(req.Q("pid"), out pid);
                if (!License.IsLicensed || pid <= 0 || !Samba.HasConn()) { res.Json(200, J.Obj("groups", new List<object>())); return; }
                res.Json(200, J.Obj("groups", Samba.OrderTagGroups(pid)));
                return;
            }
            // Bir siparis yonunun (masa/varlik gerektiren) GERCEK varliklarini dondurur -> musteri listeden secer.
            if (path == "/api/entities")
            {
                if (!License.IsLicensed) { res.Json(200, J.Obj("entities", new List<object>())); return; }
                try
                {
                    var dests = Destinations(); int di; int.TryParse(req.Q("dest"), out di);
                    string et = (di >= 0 && di < dests.Count) ? J.S(dests[di], "entityType") : "";
                    var list = et.Length > 0 ? Samba.EntitiesOf(et) : null;
                    res.Json(200, J.Obj("entities", list ?? new List<object>()));
                }
                catch (Exception e) { res.Json(200, J.Obj("entities", new List<object>(), "error", e.Message)); }
                return;
            }
            if (path == "/api/order")
            {
                if (!POST) { res.Json(405, Err("POST")); return; }
                if (!License.IsLicensed) { res.Json(403, Err("Kiosk lisansı aktif değil.")); return; }
                var b = req.JsonBody();
                string dDept, dTt, dEt, dLabel; ResolveDest(b, out dDept, out dTt, out dEt, out dLabel);
                string table = J.S(b, "table");
                // Kioskta odeme acikken "kasada ode" kapatildiysa odemesiz siparis kabul edilmez.
                if (Odeme.Online && J.S(Cfg.Read(), "payAllowCounter") == "false") { res.Json(400, Err("Bu kioskta ödeme kioskta alınır.")); return; }
                try { var r = Samba.Order(J.LL(J.Get(b, "items")), table, dDept, dTt, dEt); PrintAsync(r, dLabel, table); res.Json(200, r); }
                catch (Exception e) { res.Json(400, Err(e.Message)); }
                return;
            }
            // ---- kioskta odeme: QR (iyzico) / kart terminali. Siparis odeme SUNUCUDA dogrulaninca acilir. ----
            if (path == "/api/pay/start")
            {
                if (!POST) { res.Json(405, Err("POST")); return; }
                if (!License.IsLicensed) { res.Json(403, Err("Kiosk lisansı aktif değil.")); return; }
                var b = req.JsonBody();
                string dDept, dTt, dEt, dLabel; ResolveDest(b, out dDept, out dTt, out dEt, out dLabel);
                try { res.Json(200, Odeme.Start(J.S(b, "method"), J.LL(J.Get(b, "items")), J.S(b, "table"), dDept, dTt, dEt, dLabel)); }
                catch (Exception e) { res.Json(400, Err(e.Message)); }
                return;
            }
            if (path == "/api/pay/status") { res.Json(200, Odeme.Status(req.Q("id"))); return; }
            if (path == "/api/pay/cancel") { res.Json(200, Odeme.Cancel(req.Q("id"))); return; }

            // ---- admin panel ----
            if (path == "/api/admin/login")
            {
                if (!POST) { res.Json(405, Err("POST")); return; }
                string pin = J.S(req.JsonBody(), "pin");
                string set = J.S(Cfg.Read(), "adminPin"); if (set.Length == 0) set = "1234";   // ilk kurulum varsayilani - panelden degistirilir
                if (pin.Length == 0 || pin != set) { res.Json(401, Err("PIN hatalı.")); return; }
                string t = Crypto.RandomHex(24); _adminTok[t] = J.NowMs() + 60L * 60 * 1000;
                res.SetHeader("Set-Cookie", "kiosk_admin=" + t + "; Path=/; HttpOnly; SameSite=Lax; Max-Age=3600");
                res.Json(200, J.Obj("ok", true));
                return;
            }
            if (path.StartsWith("/api/admin/"))
            {
                if (!AdminOk(req)) { res.Json(401, Err("Yönetici girişi gerekli.")); return; }
                if (path == "/api/admin/settings")
                {
                    if (POST) { SaveSettings(req.JsonBody()); res.Json(200, Settings()); return; }
                    res.Json(200, Settings()); return;
                }
                if (path == "/api/admin/test") { res.Json(200, Samba.TestConnection()); return; }
                if (path == "/api/admin/menu-preview") { try { res.Json(200, Samba.MenuPreview(req.Q("menu"))); } catch (Exception e) { res.Json(400, Err(e.Message)); } return; }
                if (path == "/api/admin/fiscal-test") { res.Json(200, YazarKasa.Test()); return; }
                if (path == "/api/admin/options") { try { res.Json(200, Samba.Options()); } catch (Exception e) { res.Json(400, Err(e.Message)); } return; }
                if (path == "/api/admin/catalog") { try { res.Json(200, Samba.Catalog(req.Q("menu"))); } catch (Exception e) { res.Json(400, Err(e.Message)); } return; }
                if (path == "/api/admin/pay-test") { res.Json(200, req.Q("m") == "terminal" ? Odeme.TestPos() : Odeme.TestQr()); return; }
                if (path == "/api/admin/printers") { res.Json(200, J.Obj("printers", KioskPrinter.List(), "current", KioskPrinter.Name)); return; }
                if (path == "/api/admin/printer-test") { try { res.Json(200, KioskPrinter.Test()); } catch (Exception e) { res.Json(200, J.Obj("ok", false, "error", e.Message)); } return; }
                if (path == "/api/admin/dest-test") { var b = req.JsonBody(); res.Json(200, Samba.TestTerminal(J.S(b, "department"), J.S(b, "ticketType"), J.S(b, "terminal"))); return; }
                if (path == "/api/admin/logo") { try { res.Json(200, SaveLogo(req.JsonBody())); } catch (Exception e) { res.Json(400, Err(e.Message)); } return; }
                if (path == "/api/admin/upload") { try { res.Json(200, SaveUpload(req.JsonBody())); } catch (Exception e) { res.Json(400, Err(e.Message)); } return; }
                if (path == "/api/admin/license") { License.Init("kiosk", J.S(Cfg.Read(), "kioskActivationKey"), CloudUrl, false); res.Json(200, J.Obj("licensed", License.IsLicensed, "error", License.Error)); return; }
                res.Json(404, Err("Bulunamadı")); return;
            }

            ServeStatic(res, path);
        }

        // Istekteki siparis yonu (dest index) -> departman/adisyon tipi/varlik tipi/etiket.
        static void ResolveDest(Dictionary<string, object> b, out string dept, out string tt, out string et, out string label)
        {
            dept = null; tt = null; et = null; label = "";
            var dests = Destinations(); if (dests.Count == 0) return;
            int di = (int)J.Num(b, "dest"); if (di < 0 || di >= dests.Count) di = 0;
            var d = dests[di]; dept = J.S(d, "department"); tt = J.S(d, "ticketType"); et = J.S(d, "entityType"); label = J.S(d, "label");
        }
        // Kiosk'un kendi fis yazicisi varsa musteri fisini arka planda bas (siparis cevabini bekletmez).
        static void PrintAsync(Dictionary<string, object> r, string label, string table)
        {
            if (!KioskPrinter.Enabled) return;
            ThreadPool.QueueUserWorkItem(_ => {
                try { var pr = KioskPrinter.PrintOrder(r, label, table); if (!J.IsTrue(pr, "ok")) Log.Write("[yazici] " + J.S(pr, "error")); }
                catch (Exception ex) { Log.Write("[yazici] " + ex.Message); }
            });
        }

        // Bu yonde musteri bir NUMARA (masa/sira no) girsin mi? Acik "ask" alani varsa onu kullan;
        // yoksa eski config icin: varlik tipi adinda "masa" geciyorsa sor (ör. Masalar), "Müşteriler"de sorma.
        static bool AskNumber(Dictionary<string, object> d)
        {
            if (J.Has(d, "ask")) return J.IsTrue(d, "ask") || J.S(d, "ask") == "true" || J.S(d, "ask") == "1";
            return J.S(d, "entityType").ToLowerInvariant().Contains("masa");
        }

        // Siparis yonleri (config "destinations" JSON) - sadece ENABLED olanlar, sirali.
        static List<Dictionary<string, object>> Destinations()
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                foreach (var o in J.LL(J.Parse(J.S(Cfg.Read(), "destinations"))))
                {
                    var d = J.DD(o); if (d == null) continue;
                    bool en = !J.Has(d, "enabled") || J.IsTrue(d, "enabled") || J.S(d, "enabled") == "true" || J.S(d, "enabled") == "1";
                    if (en && J.S(d, "label").Length > 0) list.Add(d);
                }
            }
            catch { }
            return list;
        }

        static List<object> ParseArr(string json) { try { var l = J.LL(J.Parse(json)); return l != null ? l : new List<object>(); } catch { return new List<object>(); } }
        // Odeme secenekleri (kioskta "nasil odemek istersiniz") - config "payOptions" JSON; bos ise varsayilan 3 secenek.
        static List<object> PayOptions()
        {
            var def = new List<object> {
                J.Obj("key","counter","label","Kasada Öde","icon",""),
                J.Obj("key","cash","label","Nakit","icon",""),
                J.Obj("key","card","label","Kredi Kartı","icon","")
            };
            try
            {
                var raw = J.LL(J.Parse(J.S(Cfg.Read(), "payOptions")));
                if (raw == null || raw.Count == 0) return def;
                var outp = new List<object>();
                foreach (var o in raw) { var d = J.DD(o); if (d == null) continue; bool en = !J.Has(d, "enabled") || J.IsTrue(d, "enabled") || J.S(d, "enabled") == "true"; if (en && J.S(d, "label").Length > 0) outp.Add(J.Obj("key", J.S(d, "key"), "label", J.S(d, "label"), "icon", J.S(d, "icon"))); }
                return outp.Count > 0 ? outp : def;
            }
            catch { return def; }
        }

        // ---- ayarlar (config.json) ----
        static readonly string[] Keys = { "name", "sambaHost", "sambaPort", "sambaClientId", "sambaUser", "menuName", "menus", "department", "ticketType", "terminal", "orderMode", "entityType", "kioskEntityType", "kioskEntityScreen", "kioskEntityName", "destinations", "languages", "payMode", "payOptions", "themeColor", "themeMode", "sliderImages", "printCommand", "hiddenProducts", "extraProducts", "easyMode", "featuredProducts", "upsellProducts", "kioskPrinter", "kioskPrinterChars", "kioskPrinterAscii", "kioskPrinterFooter",
            "payAllowCounter", "iyzicoEnabled", "iyzicoMode", "iyzicoApiKey", "iyzicoPayType", "iyzicoCallback", "iyzicoCity", "iyzicoEmail", "posBrand", "posConn", "posPayType", "exitPin", "kioskActivationKey", "cloudServerUrl", "fiscalEnabled", "fiscalType", "fiscalConn", "fiscalPort", "fiscalBaud", "fiscalTcpPort", "fiscalSerial", "fiscalGmp", "fiscalReceiptType", "fiscalMerchantNo", "fiscalNote", "adminPin" };
        static Dictionary<string, object> Settings()
        {
            var c = Cfg.Read(); var o = new Dictionary<string, object>();
            foreach (var k in Keys) if (k != "adminPin") o[k] = J.S(c, k);
            o["hasSambaPass"] = J.S(c, "sambaPass").Length > 0;   // sifre geri gonderilmez
            o["hasIyzicoSecret"] = J.S(c, "iyzicoSecret").Length > 0;   // iyzico gizli anahtari da geri gonderilmez
            o["logo"] = J.S(c, "logo");
            o["licensed"] = License.IsLicensed; o["licenseError"] = License.Error;
            o["version"] = Version;
            return o;
        }
        static void SaveSettings(Dictionary<string, object> b)
        {
            Cfg.Update(c => {
                foreach (var k in Keys) if (J.Has(b, k)) c[k] = J.S(b, k);
                // sifre: sadece dolu gelirse degistir (maskeli "********" ise dokunma)
                string pw = J.S(b, "sambaPass"); if (pw.Length > 0 && pw != "********") c["sambaPass"] = pw;
                string isec = J.S(b, "iyzicoSecret"); if (isec.Length > 0 && isec != "********") c["iyzicoSecret"] = isec.Trim();
            });
            if (J.Has(b, "kioskActivationKey")) License.Init("kiosk", J.S(Cfg.Read(), "kioskActivationKey"), CloudUrl, false);
        }
        static Dictionary<string, object> SaveLogo(Dictionary<string, object> b)
        {
            string data = J.S(b, "data");   // "data:image/png;base64,...."
            var m = Regex.Match(data, @"^data:image/(png|jpeg|jpg|svg\+xml);base64,(.+)$");
            if (!m.Success) throw new Exception("Geçersiz görsel.");
            string ext = m.Groups[1].Value == "svg+xml" ? "svg" : (m.Groups[1].Value == "jpeg" ? "jpg" : m.Groups[1].Value);
            byte[] bytes = Convert.FromBase64String(m.Groups[2].Value);
            if (bytes.Length > 3 * 1024 * 1024) throw new Exception("Görsel 3 MB'tan büyük olamaz.");
            string rel = "logo." + ext; File.WriteAllBytes(Path.Combine(Root, "public", rel), bytes);
            Cfg.Update(c => c["logo"] = "/" + rel + "?t=" + J.NowMs());
            return J.Obj("ok", true, "logo", J.S(Cfg.Read(), "logo"));
        }
        // Genel gorsel yukleme (slider, siparis/odeme ikonlari) -> public/uploads/, benzersiz ad, yol doner.
        static Dictionary<string, object> SaveUpload(Dictionary<string, object> b)
        {
            string data = J.S(b, "data");
            var m = Regex.Match(data, @"^data:image/(png|jpeg|jpg|svg\+xml|webp|gif);base64,(.+)$");
            if (!m.Success) throw new Exception("Geçersiz görsel.");
            string ext = m.Groups[1].Value == "svg+xml" ? "svg" : (m.Groups[1].Value == "jpeg" ? "jpg" : m.Groups[1].Value);
            byte[] bytes = Convert.FromBase64String(m.Groups[2].Value);
            if (bytes.Length > 4 * 1024 * 1024) throw new Exception("Görsel 4 MB'tan büyük olamaz.");
            string dir = Path.Combine(Root, "public", "uploads"); Directory.CreateDirectory(dir);
            string name = "u" + J.NowMs() + Crypto.RandomHex(3) + "." + ext;
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            return J.Obj("ok", true, "path", "/uploads/" + name);
        }

        static void ServeStatic(Res res, string pathname)
        {
            // Ilk acilis: lisans yoksa lisans sayfasi; lisansli ama SambaPOS baglantisi ayarlanmamissa
            // KURULUM REHBERI (adim adim); tam ayarliysa kiosk ekrani. Kurulum exe'si '/' acar -> rehber gelir.
            string rel;
            if (pathname == "/" || pathname == "") rel = !License.IsLicensed ? "/lisans.html" : (!Samba.HasConn() ? "/kurulum.html" : "/index.html");
            else if (pathname == "/admin") rel = "/admin.html";
            else if (pathname == "/kurulum") rel = "/kurulum.html";
            else rel = pathname;
            string pub = Path.Combine(Root, "public");
            string file = Path.GetFullPath(Path.Combine(pub, rel.TrimStart('/')));
            if (!file.StartsWith(pub + "\\", StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) { res.Json(404, Err("Bulunamadı")); return; }
            string ext = Path.GetExtension(file); string t; if (!Types.TryGetValue(ext, out t)) t = "application/octet-stream";
            res.Send(200, t, File.ReadAllBytes(file), "no-store");
        }
    }
}
