// Kiosk - SambaPOS motoru. Menu ve siparis SambaPOS mesaj sunucusundan (:9000 GraphQL) alinir;
// kiosk ekrani HERKESE ACIK oldugu icin SambaPOS kullanici/sifresi burada (sunucuda) kalir, ekrana gitmez.
// Siparis "gel-al" (paket/takeaway) adisyon tipi/departmanina terminal yolundan girer (Garson ile ayni API).
using System;
using System.Collections.Generic;
using System.Linq;
using Alfa;

namespace Kiosk
{
    public static class Samba
    {
        static readonly object _lock = new object();
        static string _token; static DateTime _tokenExp;
        static string _terminalId; static DateTime _termAt; static string _termKey;

        static string Cv(string k, string def) { string v = J.S(Cfg.Read(), k); return v.Length > 0 ? v : def; }
        static string Host { get { return Cv("sambaHost", "localhost"); } }
        static int Port { get { int p; return int.TryParse(Cv("sambaPort", "9000"), out p) ? p : 9000; } }
        static string ClientId { get { return Cv("sambaClientId", "EnsariGarson"); } }
        public static string Base()
        {
            string h = Host;
            if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return h.TrimEnd('/');
            return "http://" + h + ":" + Port;
        }
        public static bool HasConn() { var c = Cfg.Read(); return J.S(c, "sambaUser").Length > 0 && J.S(c, "sambaPass").Length > 0; }
        static string GqlStr(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        // ---- token ----
        static string Token(bool force)
        {
            lock (_lock) { if (!force && _token != null && _tokenExp > DateTime.UtcNow) return _token; }
            var c = Cfg.Read(); string user = J.S(c, "sambaUser"), pass = J.S(c, "sambaPass");
            if (user.Length == 0 || pass.Length == 0) throw new Exception("SambaPOS bağlantı kullanıcısı ayarlanmamış (Kiosk yönetici panelinden girin).");
            string body = "grant_type=password&client_id=" + Uri.EscapeDataString(ClientId) + "&username=" + Uri.EscapeDataString(user) + "&password=" + Uri.EscapeDataString(pass);
            var r = Web.Request("POST", Base() + "/Token", body, new Dictionary<string, string> { { "Content-Type", "application/x-www-form-urlencoded" } }, 12000);
            if (r.Status == 0) throw new Exception("SambaPOS mesaj sunucusuna ulaşılamadı (" + Base() + ").");
            var j = J.DD(J.Parse(r.Text)); string acc = J.S(j, "access_token");
            if (r.Ok && acc.Length > 0) { double e = J.Num(j, "expires_in"); if (!(e > 60)) e = 3600; lock (_lock) { _token = acc; _tokenExp = DateTime.UtcNow.AddSeconds(e - 120); } return acc; }
            string err = J.S(j, "error");
            if (err == "invalid_client") throw new Exception("SambaPOS'ta \"" + ClientId + "\" uygulama kaydı yok (hazırlık aracını çalıştırın).");
            if (err == "invalid_grant") throw new Exception("SambaPOS bağlantı kullanıcısı adı/şifresi hatalı (Şifre alanı, PIN değil).");
            throw new Exception("SambaPOS girişi reddetti: " + (J.S(j, "error_description").Length > 0 ? J.S(j, "error_description") : ("HTTP " + r.Status)));
        }
        static Dictionary<string, object> Gql(string query)
        {
            string tok = Token(false);
            var r = Web.PostJson(Base() + "/api/graphql", J.Obj("query", query), 25000, new Dictionary<string, string> { { "Authorization", "Bearer " + tok } });
            if (r.Status == 401) { tok = Token(true); r = Web.PostJson(Base() + "/api/graphql", J.Obj("query", query), 25000, new Dictionary<string, string> { { "Authorization", "Bearer " + tok } }); }
            if (r.Status == 0) throw new Exception("SambaPOS mesaj sunucusuna ulaşılamadı.");
            var j = J.DD(J.Parse(r.Text));
            var errs = J.L(J.Get(j, "errors"));
            if (J.Get(j, "data") == null && errs != null && errs.Count > 0)
            {
                string m = J.S(J.DD(errs[0]), "message");
                if (m.IndexOf("authorization is required", StringComparison.OrdinalIgnoreCase) >= 0) m = "Bağlantı kullanıcısının yetkisi yetersiz. SambaPOS'ta bu kullanıcıyı Yönetici (Admin) rolüne alın.";
                throw new Exception(m.Length > 0 ? m : "SambaPOS hatası.");
            }
            return J.DD(J.Get(j, "data"));
        }

        // ---- menu ----
        // Isletmenin birden fazla menusu olabilir (ör. Kahvalti / Ana Menu). "menus" virgulle ayrilmis
        // menu adlari; bos ise tek "menuName" kullanilir.
        public static List<string> MenuNames()
        {
            var list = Cv("menus", "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (list.Count == 0) { string m = Cv("menuName", ""); if (m.Length > 0) list.Add(m); }
            if (list.Count == 0) list.Add("Menu");
            return list;
        }
        public static Dictionary<string, object> Menu(string menuName)
        {
            if (string.IsNullOrEmpty(menuName)) { var ns = MenuNames(); menuName = ns[0]; }
            var data = Gql("{menu:getMenu(name:" + GqlStr(menuName) + "){categories{id,name,color,menuItems{id,name,color,caption,productId,image}}}}");
            var menu = J.DD(J.Get(data, "menu"));
            var cats = new List<object>();
            foreach (var c in J.LL(J.Get(menu, "categories")))
            {
                var cd = J.DD(c);
                var items = new List<object>();
                foreach (var it in J.LL(J.Get(cd, "menuItems")))
                {
                    var id = J.DD(it);
                    // Fiyat getMenu'de gelmez; urun fiyati icin ayri sorgu pahali - caption'da fiyat olabilir, yoksa 0.
                    items.Add(J.Obj("id", J.Get(id, "id"), "productId", J.Get(id, "productId"), "name", J.S(id, "name"),
                        "price", J.Num(id, "price"), "image", J.S(id, "image")));
                }
                cats.Add(J.Obj("name", J.S(cd, "name"), "icon", "🍽️", "color", J.S(cd, "color"), "items", items));   // color: SambaPOS kategori butonu rengi
            }
            return J.Obj("categories", cats);
        }

        // Urun fiyatlari (getProducts) - menude fiyat gelmedigi icin productId->price haritasi.
        // MenuRaw = SambaPOS menusu + fiyat (gizleme/ekleme UYGULANMADAN); admin "ürünler" listesi bunu kullanir.
        public static Dictionary<string, object> MenuRaw(string menuName)
        {
            var m = Menu(menuName);
            try
            {
                var pd = Gql("{products:getProducts{id,name,price,portions{name,price}}}");
                var price = new Dictionary<string, double>();
                foreach (var p in J.LL(J.Get(pd, "products")))
                {
                    var d = J.DD(p); double pr = J.Num(d, "price");
                    if (!(pr > 0)) { var ports = J.LL(J.Get(d, "portions")); if (ports.Count > 0) pr = J.Num(J.DD(ports[0]), "price"); }   // SambaPOS'ta fiyat genelde porsiyonda
                    price[J.S(d, "id")] = pr;
                }
                foreach (var c in J.LL(J.Get(m, "categories")))
                    foreach (var it in J.LL(J.Get(J.DD(c), "items")))
                    { var d = J.DD(it); double pr; if (!(J.Num(d, "price") > 0) && price.TryGetValue(J.S(d, "productId"), out pr)) d["price"] = J.NumVal(pr); }
            }
            catch { /* fiyat alinamazsa menu yine gosterilir */ }
            return m;
        }

        // Kiosk ekranina giden menu: MenuRaw + GIZLI urunler cikarilir + EKLENEN (kiosk'a ozel) urunler eklenir.
        public static Dictionary<string, object> MenuWithPrices(string menuName)
        {
            var m = MenuRaw(menuName);
            var hidden = HiddenSet();
            var cats = J.LL(J.Get(m, "categories"));
            if (hidden.Count > 0)
            {
                var keep = new List<object>();
                foreach (var c in cats)
                {
                    var cd = J.DD(c); var items = J.LL(J.Get(cd, "items"));
                    var ki = items.Where(it => !hidden.Contains(ItemKey(J.DD(it)))).ToList();
                    if (ki.Count > 0) { cd["items"] = ki; keep.Add(cd); }
                }
                cats = keep; m["categories"] = cats;
            }
            // Eklenen urunler (admin "ürün ekle") - kategori adina gore yerlestirilir, yoksa yeni kategori.
            foreach (var ex in Extras())
            {
                var e = J.DD(ex); if (e == null) continue;
                string cat = J.S(e, "category"); if (cat.Length == 0) cat = "Ekstra";
                var item = J.Obj("id", J.Get(e, "productId"), "productId", J.Get(e, "productId"),
                    "name", J.S(e, "name"), "price", J.NumVal(J.Num(e, "price")), "image", J.S(e, "image"));
                Dictionary<string, object> target = null;
                foreach (var c in cats) { var cd = J.DD(c); if (string.Equals(J.S(cd, "name"), cat, StringComparison.OrdinalIgnoreCase)) { target = cd; break; } }
                if (target == null) { target = J.Obj("name", cat, "icon", J.S(e, "icon").Length > 0 ? J.S(e, "icon") : "➕", "items", new List<object>()); cats.Add(target); }
                J.LL(J.Get(target, "items")).Add(item);
            }
            return m;
        }

        // Gizli urun anahtar kumesi (config "hiddenProducts" = productId dizisi, string).
        static HashSet<string> HiddenSet()
        {
            var h = new HashSet<string>();
            try { foreach (var o in J.LL(J.Parse(Cv("hiddenProducts", "")))) { string s = J.S(o); if (s.Length > 0) h.Add(s); } } catch { }
            return h;
        }
        static List<object> Extras() { try { return J.LL(J.Parse(Cv("extraProducts", ""))); } catch { return new List<object>(); } }
        // Urun anahtari: productId varsa onu, yoksa ad.
        static string ItemKey(Dictionary<string, object> d) { double pid = J.Num(d, "productId"); return pid > 0 ? ((long)pid).ToString() : ("n:" + J.S(d, "name")); }

        // Admin paneli KATALOGU: tum urunler/kategoriler (gizli isaretli) + eklenenler + SambaPOS listeleri + varliklar.
        public static Dictionary<string, object> Catalog(string menuName)
        {
            var res = new Dictionary<string, object>();
            var hidden = HiddenSet();
            try
            {
                var m = MenuRaw(menuName);
                foreach (var c in J.LL(J.Get(m, "categories")))
                    foreach (var it in J.LL(J.Get(J.DD(c), "items")))
                    { var d = J.DD(it); d["hidden"] = hidden.Contains(ItemKey(d)); d["key"] = ItemKey(d); }
                res["categories"] = J.Get(m, "categories");
                res["menuName"] = string.IsNullOrEmpty(menuName) ? MenuNames()[0] : menuName;
            }
            catch (Exception e) { res["menuError"] = e.Message; res["categories"] = new List<object>(); }
            res["menus"] = MenuNames();
            res["extraProducts"] = Extras();
            // SambaPOS listeleri + varliklar (Options hepsini GraphQL ile ceker).
            try { var opt = Options(); res["options"] = opt; if (opt.ContainsKey("entities")) res["entities"] = opt["entities"]; }
            catch (Exception e) { res["optionsError"] = e.Message; }
            res["ok"] = true;
            return res;
        }

        // getEntities(type:X) ile bir varlik tipinin varliklarini (ad) ceker. Tip YOKSA null doner (getEntities null).
        public static List<object> EntitiesOf(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            var d = Gql("{e:getEntities(type:" + GqlStr(type) + "){name}}");
            var raw = J.Get(d, "e");
            if (raw == null) return null;   // "Entity Type not found" -> tip yok
            var names = new List<object>();
            foreach (var x in J.LL(raw)) { string n = J.S(J.DD(x), "name"); if (n.Length > 0) names.Add(n); if (names.Count >= 1000) break; }
            return names;
        }
        // Urun secenekleri = SambaPOS siparis etiket gruplari (getOrderTagGroups(productId)). 5 dk onbellek.
        // Donus: [{name, min, max, tags:[{name, price}]}] - gizli gruplar atlanir. Hata/etiket yoksa bos liste.
        static readonly Dictionary<long, Tuple<DateTime, List<object>>> _tagCache = new Dictionary<long, Tuple<DateTime, List<object>>>();
        public static List<object> OrderTagGroups(long productId)
        {
            lock (_lock) { Tuple<DateTime, List<object>> c; if (_tagCache.TryGetValue(productId, out c) && (DateTime.UtcNow - c.Item1).TotalMinutes < 5) return c.Item2; }
            var list = new List<object>();
            try
            {
                var d = Gql("{g:getOrderTagGroups(productId:" + productId + "){name min max hidden tags{name price}}}");
                foreach (var g in J.LL(J.Get(d, "g")))
                {
                    var gd = J.DD(g); if (gd == null || J.IsTrue(gd, "hidden")) continue;
                    var tags = new List<object>();
                    foreach (var t in J.LL(J.Get(gd, "tags"))) { var td = J.DD(t); string n = J.S(td, "name"); if (n.Length > 0) tags.Add(J.Obj("name", n, "price", J.NumVal(J.Num(td, "price")))); }
                    if (tags.Count > 0) list.Add(J.Obj("name", J.S(gd, "name"), "min", J.NumVal(J.Num(gd, "min")), "max", J.NumVal(J.Num(gd, "max")), "tags", tags));
                }
            }
            catch { }
            lock (_lock) { _tagCache[productId] = Tuple.Create(DateTime.UtcNow, list); }
            return list;
        }

        // Denenecek varlik tipi adaylari: config'te gecenler + yaygin SambaPOS varsayilanlari.
        static List<string> EntityTypeCandidates()
        {
            var set = new List<string>();
            Action<string> add = s => { s = (s ?? "").Trim(); if (s.Length > 0 && !set.Contains(s)) set.Add(s); };
            try { foreach (var o in J.LL(J.Parse(Cv("destinations", "")))) add(J.S(J.DD(o), "entityType")); } catch { }
            add(Cv("kioskEntityType", ""));
            foreach (var d in new[] { "Masalar", "Masa", "Müşteriler", "Müşteri", "Kuryeler", "Kurye", "Garsonlar", "Garson", "Paket", "Masa No", "Müşteri Hesapları" }) add(d);
            return set;
        }

        // ---- siparis ----
        // Siparis YONU (gel-al/masa/servis) admin'de tanimli; her yonun kendi departman/adisyon tipi/varlik tipi olur.
        static string RegisterTerminal(string dept, string ticketType, bool force)
        {
            var c = Cfg.Read();
            if (string.IsNullOrEmpty(dept)) dept = Cv("department", "Gel-Al");
            if (string.IsNullOrEmpty(ticketType)) ticketType = Cv("ticketType", "Adisyon");
            lock (_lock) { if (!force && _terminalId != null && _termKey == dept + "|" + ticketType && (DateTime.UtcNow - _termAt).TotalMinutes < 10) return _terminalId; }
            string q = "mutation m{t:registerTerminal(terminal:" + GqlStr(Cv("terminal", "Kiosk")) + ",department:" + GqlStr(dept) +
                       ",user:" + GqlStr(J.S(c, "sambaUser")) + ",ticketType:" + GqlStr(ticketType) + ")}";
            var d = Gql(q); string tid = J.S(d, "t");
            if (tid.Length == 0) throw new Exception("SambaPOS terminal kaydı alınamadı (departman/adisyon tipi adı doğru mu?).");
            lock (_lock) { _terminalId = tid; _termAt = DateTime.UtcNow; _termKey = dept + "|" + ticketType; }
            return tid;
        }
        // items: [{productId, quantity}]. table dolu ise entityType ile o masaya baglanir. dept/ticketType/entityType: siparis yonunden.
        // Urun fiyat haritasi (productId -> fiyat; porsiyonda ise ilk porsiyon) - kiosk odemesinde TUTARI SUNUCU hesaplar. 60 sn onbellek.
        static Dictionary<long, double> _priceCache; static DateTime _priceAt;
        public static Dictionary<long, double> ProductPrices()
        {
            lock (_lock) { if (_priceCache != null && (DateTime.UtcNow - _priceAt).TotalSeconds < 60) return _priceCache; }
            var map = new Dictionary<long, double>();
            var pd = Gql("{products:getProducts{id,price,portions{name,price}}}");
            foreach (var p in J.LL(J.Get(pd, "products")))
            {
                var d = J.DD(p); double pr = J.Num(d, "price");
                if (!(pr > 0)) { var ports = J.LL(J.Get(d, "portions")); if (ports.Count > 0) pr = J.Num(J.DD(ports[0]), "price"); }
                map[(long)J.Num(d, "id")] = pr;
            }
            lock (_lock) { _priceCache = map; _priceAt = DateTime.UtcNow; }
            return map;
        }

        // payment: null = odemesiz (kasada ode); {type: SambaPOS odeme tipi adi, amount, desc} = kioskta odendi -> payTerminalTicket.
        public static Dictionary<string, object> Order(List<object> items, string table, string dept, string ticketType, string entityType, Dictionary<string, object> payment = null)
        {
            if (items == null || items.Count == 0) throw new Exception("Sepet boş.");
            string tid = RegisterTerminal(dept, ticketType, false);
            Action create = () => Gql("mutation m{t:createTerminalTicket(terminalId:" + GqlStr(tid) + "){uid}}");
            try { create(); }
            catch { tid = RegisterTerminal(dept, ticketType, true); create(); }   // terminal kaydi eskidiyse yenile
            // Kiosk kimligi: her adisyona secilen varlik tipinde bir "Kiosk" varligi baglanir ->
            // SambaPOS'ta siparisin kiosktan geldigi bellidir (varlik ekraninda gorunur, fiste "Kiosk" yazar).
            string ket = Cv("kioskEntityType", ""); string ken = Cv("kioskEntityName", "Kiosk");
            if (ket.Length > 0 && ken.Length > 0)
                try { Gql("mutation m{t:changeEntityOfTerminalTicket(terminalId:" + GqlStr(tid) + ",type:" + GqlStr(ket) + ",name:" + GqlStr(ken) + "){uid}}"); }
                catch { /* kiosk varligi yoksa/baglanamazsa siparis yine gecerli */ }
            // Masaya dusur: musterinin girdigi masa, varlik tipi ile adisyona baglanir (kiosk varligindan farkli tip).
            if (!string.IsNullOrWhiteSpace(table))
            {
                string et = !string.IsNullOrEmpty(entityType) ? entityType : Cv("entityType", "Masalar");
                try { Gql("mutation m{t:changeEntityOfTerminalTicket(terminalId:" + GqlStr(tid) + ",type:" + GqlStr(et) + ",name:" + GqlStr(table.Trim()) + "){uid}}"); }
                catch (Exception e) { throw new Exception("Masa (" + table + ") adisyona bağlanamadı: " + e.Message); }
            }
            var notes = new List<string>();
            foreach (var it in items)
            {
                var d = J.DD(it); double pid = J.Num(d, "productId"); double qty = J.Num(d, "quantity"); if (qty <= 0) qty = 1;
                if (pid <= 0) continue;
                var added = Gql("mutation m{t:addOrderToTerminalTicket(terminalId:" + GqlStr(tid) + ",productId:" + ((long)pid) + ",orderTags:\"\",quantity:" + qty.ToString(J.Inv) + "){orders{uid name}}}");
                var ords = J.LL(J.Get(J.DD(J.Get(added, "t")), "orders"));
                var last = ords.Count > 0 ? J.DD(ords[ords.Count - 1]) : null;
                string ouid = last != null ? J.S(last, "uid") : "", pname = last != null ? J.S(last, "name") : "";
                // Secilen urun secenekleri (SambaPOS siparis etiketleri): yalnizca SambaPOS'ta TANIMLI olanlar,
                // fiyat SambaPOS'tan (kiosk ekrani herkese acik - gonderilen fiyata guvenilmez). Ayni etiket tekrar
                // gonderilirse SambaPOS onu "kaldir" sayar -> tekillestirilir.
                var sel = J.LL(J.Get(d, "tags"));
                if (sel.Count > 0)
                {
                    var groups = OrderTagGroups((long)pid);
                    var valid = new List<string>(); var names = new List<string>(); var seen = new HashSet<string>();
                    foreach (var s in sel)
                    {
                        var sd = J.DD(s); string gname = J.S(sd, "group"), tname = J.S(sd, "tag");
                        if (!seen.Add(gname + "|" + tname)) continue;
                        double price = -1;
                        foreach (var g in groups) { var gd = J.DD(g); if (J.S(gd, "name") != gname) continue; foreach (var tt in J.LL(J.Get(gd, "tags"))) { var td = J.DD(tt); if (J.S(td, "name") == tname) price = J.Num(td, "price"); } }
                        if (price < 0) continue;
                        valid.Add("{tagName:" + GqlStr(gname) + ",tag:" + GqlStr(tname) + ",price:" + price.ToString(J.Inv) + ",quantity:1}");
                        names.Add(tname);
                    }
                    if (valid.Count > 0)
                    {
                        bool ok = false;
                        if (ouid.Length > 0)
                            try { Gql("mutation m{t:updateOrderOfTerminalTicket(terminalId:" + GqlStr(tid) + ",orderUid:" + GqlStr(ouid) + ",orderTags:[" + string.Join(",", valid) + "]){uid}}"); ok = true; } catch { }
                        if (!ok) notes.Add((pname.Length > 0 ? pname : "Ürün") + ": " + string.Join(", ", names));   // etiket yazilamazsa adisyon notuna
                    }
                }
                string inote = System.Text.RegularExpressions.Regex.Replace(J.S(d, "note"), @"[\r\n\t]+", " ").Trim();
                if (inote.Length > 200) inote = inote.Substring(0, 200);
                if (inote.Length > 0) notes.Add((pname.Length > 0 ? pname : "Ürün") + ": " + inote);
            }
            // Adisyon notu: "Kiosk" + urun notlari -> SambaPOS'ta kiosk siparisi belli olur, fiste/mutfakta gorunur.
            string paidTxt = payment != null ? ("ÖDENDİ: " + J.S(payment, "type") + " " + J.Num(payment, "amount").ToString("0.00", J.Inv) + " TL") : "";
            try { string tnote = "Kiosk" + (paidTxt.Length > 0 ? " — " + paidTxt : "") + (notes.Count > 0 ? " — " + string.Join(" | ", notes) : ""); Gql("mutation m{t:updateTerminalTicket(terminalId:" + GqlStr(tid) + ",note:" + GqlStr(tnote) + "){uid}}"); }
            catch { /* not yazilamazsa siparis yine gecerli */ }
            // Kioskta alinan odeme -> SambaPOS'a odeme kaydi (adisyon "odendi" kapanir). Islemciler CALISTIRILMAZ:
            // para zaten kioskta alindi, SambaPOS'un kendi POS cihazi tekrar tetiklenmesin.
            if (payment != null)
            {
                try
                {
                    var pr = Gql("mutation m{t:payTerminalTicket(terminalId:" + GqlStr(tid) + ",paymentTypeName:" + GqlStr(J.S(payment, "type")) + ",description:" + GqlStr(J.S(payment, "desc")) +
                                 ",executePaymentProcessors:false,amount:" + J.Num(payment, "amount").ToString(J.Inv) + "){amount remainingAmount errorMessage}}");
                    string pe = J.S(J.DD(J.Get(pr, "t")), "errorMessage");
                    if (pe.Length > 0) Log.Write("[odeme] SambaPOS ödeme kaydı hatası: " + pe + " (" + paidTxt + ")");
                }
                catch (Exception e) { Log.Write("[odeme] SambaPOS ödeme kaydı yapılamadı: " + e.Message + " (" + paidTxt + ") - adisyon notunda ÖDENDİ yazıyor"); }
            }
            // Yazdirma: ayarlanan SambaPOS otomasyon komutu adisyon uzerinde calistirilir (or. "Fiş Yazdır" / "Mutfağa Gönder").
            string printCmd = Cv("printCommand", "");
            if (printCmd.Length > 0)
                try { Gql("mutation m{t:executeAutomationCommandForTerminalTicket(terminalId:" + GqlStr(tid) + ",orderUid:\"\",name:" + GqlStr(printCmd) + ",value:\"\"){uid}}"); }
                catch { /* yazdirma basarisizsa siparis yine gecerli */ }
            string no = ""; double total = 0; var lines = new List<object>();
            try { var g = Gql("{t:getTerminalTicket(terminalId:" + GqlStr(tid) + "){number totalAmount}}"); var gt = J.DD(J.Get(g, "t")); no = J.S(gt, "number"); total = J.Num(gt, "totalAmount"); } catch { }
            // Kiosk fis yazicisi icin adisyon satirlari (ad/adet/birim fiyat + etiketler) - SambaPOS'un kendi verisi.
            try
            {
                var g = Gql("{t:getTerminalTicket(terminalId:" + GqlStr(tid) + "){orders{name quantity price tags{tag price quantity}}}}");
                foreach (var o in J.LL(J.Get(J.DD(J.Get(g, "t")), "orders")))
                {
                    var od = J.DD(o); double up = J.Num(od, "price"); var tl = new List<object>();
                    foreach (var t in J.LL(J.Get(od, "tags"))) { var td = J.DD(t); double tq = J.Num(td, "quantity"); up += J.Num(td, "price") * (tq > 0 ? tq : 1); tl.Add(J.Obj("tag", J.S(td, "tag"))); }
                    lines.Add(J.Obj("name", J.S(od, "name"), "quantity", J.NumVal(J.Num(od, "quantity")), "price", J.NumVal(up), "tags", tl));
                }
            }
            catch { }
            Gql("mutation m{e:closeTerminalTicket(terminalId:" + GqlStr(tid) + ")}");
            lock (_lock) { _terminalId = null; }   // her siparis yeni adisyon -> terminali tazele
            // Yazar kasa (ÖKC) mali fiş kancası - su an no-op (entegrasyon sonra), siparisi etkilemez.
            try { YazarKasa.PrintReceipt(J.Obj("orderNo", no, "table", table ?? "", "items", items)); } catch { }
            return J.Obj("ok", true, "orderNo", no, "total", J.NumVal(total), "lines", lines);
        }

        // Menu onizleme (admin panel "ürünler geliyor mu" testi) - kategori/urun sayisi
        public static Dictionary<string, object> MenuPreview(string menuName)
        {
            var all = MenuNames(); var res = new List<object>(); int totItems = 0;
            foreach (var mn in all)
            {
                if (!string.IsNullOrEmpty(menuName) && mn != menuName) continue;
                try { var m = MenuWithPrices(mn); var cats = J.LL(J.Get(m, "categories")); int items = 0; foreach (var c in cats) items += J.LL(J.Get(J.DD(c), "items")).Count; totItems += items; res.Add(J.Obj("menu", mn, "categories", cats.Count, "items", items)); }
                catch (Exception e) { res.Add(J.Obj("menu", mn, "error", e.Message)); }
            }
            return J.Obj("ok", true, "menus", res, "items", totItems);
        }

        static List<object> Names(Dictionary<string, object> d, string key)
        { var r = new List<object>(); foreach (var x in J.LL(J.Get(d, key))) { string n = J.S(J.DD(x), "name"); if (n.Length > 0) r.Add(n); } return r; }

        // Ayarlari SambaPOS'tan OTOMATIK cek - SADECE GraphQL (REPORT SQL mesaj sunucusunu cokertiyor).
        // GraphQL'de departman/adisyon tipi/terminal LISTESI yok; menu/odeme tipi/varlik ekrani/siparis etiketi
        // ve VARLIKLAR (tip bazinda getEntities) cekilebilir. Varlik tipleri aday listesi denenerek bulunur.
        public static Dictionary<string, object> Options()
        {
            var res = new Dictionary<string, object>();
            try { res["menus"] = MenuNames().Cast<object>().ToList(); } catch { }
            try { var p = Gql("{p:getPaymentTypes(userRoleId:1){name}}"); var pn = Names(p, "p"); if (pn.Count > 0) res["paymentTypes"] = pn; } catch { }
            try { var s = Gql("{s:getEntityScreens(roleId:1){name}}"); var sn = Names(s, "s"); if (sn.Count > 0) res["entityScreens"] = sn; } catch { }
            try { var o = Gql("{o:getOrderTagGroups{name}}"); var on = Names(o, "o"); if (on.Count > 0) res["orderTagGroups"] = on; } catch { }
            // Varlik tipleri + varliklar: aday tipleri getEntities ile dene; gelen = gecerli tip.
            var types = new List<object>(); var entities = new Dictionary<string, object>();
            foreach (var t in EntityTypeCandidates())
            {
                try { var list = EntitiesOf(t); if (list != null) { types.Add(t); if (list.Count > 0) entities[t] = list; } } catch { }
            }
            if (types.Count > 0) res["entityTypes"] = types;
            if (entities.Count > 0) res["entities"] = entities;
            res["ok"] = true;
            return res;
        }

        // Bir siparis yonunu (departman/adisyon tipi/terminal) GERCEKTEN dene - registerTerminal ile dogrula (adisyon ACMAZ).
        public static Dictionary<string, object> TestTerminal(string dept, string ticketType, string terminal)
        {
            try
            {
                var c = Cfg.Read();
                if (string.IsNullOrEmpty(dept)) dept = Cv("department", "Gel-Al");
                if (string.IsNullOrEmpty(ticketType)) ticketType = Cv("ticketType", "Adisyon");
                if (string.IsNullOrEmpty(terminal)) terminal = Cv("terminal", "Kiosk");
                string q = "mutation m{t:registerTerminal(terminal:" + GqlStr(terminal) + ",department:" + GqlStr(dept) +
                           ",user:" + GqlStr(J.S(c, "sambaUser")) + ",ticketType:" + GqlStr(ticketType) + ")}";
                var d = Gql(q); string tid = J.S(d, "t");
                if (tid.Length == 0) return J.Obj("ok", false, "error", "Terminal kaydı boş döndü — departman / adisyon tipi / terminal adı SambaPOS'taki ile birebir aynı değil.");
                return J.Obj("ok", true, "info", "Geçerli: " + dept + " / " + ticketType + " / " + terminal);
            }
            catch (Exception e) { return J.Obj("ok", false, "error", e.Message); }
        }

        public static Dictionary<string, object> TestConnection()
        {
            try { var d = Gql("{r:getUserRoles{id}}"); return J.Obj("ok", true); }
            catch (Exception e) { return J.Obj("ok", false, "error", e.Message); }
        }
    }
}
