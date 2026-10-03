// Kiosk cok dilli - urun/kategori adlarini otomatik cevirir (TR -> secilen dil), sonucu onbellege alir.
// Kaynak: Google ceviri (anahtarsiz gtx ucu), her metin dil basina BIR KEZ cevrilir ve translations.json'a yazilir.
// Not: makine cevirisidir; yanlis/eksik olani panelden elle duzeltmek icin translations.json elle de duzenlenebilir
// (anahtar "dil|Türkçe metin" -> ceviri). Ceviri alinamazsa orijinal (Türkçe) ad gosterilir.
using System;
using System.Collections.Generic;
using System.Text;
using Alfa;

namespace Kiosk
{
    public static class Ceviri
    {
        static readonly object _lk = new object();
        static Dictionary<string, object> _cache;
        static string _file;

        static void Load()
        {
            if (_cache != null) return;
            _file = System.IO.Path.Combine(App.RuntimeDir, "translations.json");
            try { _cache = System.IO.File.Exists(_file) ? J.DD(J.Parse(System.IO.File.ReadAllText(_file))) : new Dictionary<string, object>(); }
            catch { _cache = new Dictionary<string, object>(); }
        }
        static void Save() { try { System.IO.File.WriteAllText(_file, J.Pretty(_cache)); } catch { } }

        // Onbellekten ceviri (yoksa orijinal). Tek tek AG CAGRISI YAPMAZ - once Prime ile toplu doldurulur.
        public static string T(string text, string to)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrEmpty(to) || to == "tr") return text;
            Load();
            lock (_lk) { object v; if (_cache.TryGetValue(to + "|" + text, out v)) return J.S(v); }
            return text;
        }

        // Verilen metinlerin onbellekte OLMAYANLARINI TOPLU cevirir (satir-birlestirme, parca parca). Hizli + tek birkac cagri.
        public static void Prime(List<string> texts, string to)
        {
            if (string.IsNullOrEmpty(to) || to == "tr" || texts == null) return;
            Load();
            var need = new List<string>();
            lock (_lk) foreach (var t in texts) { if (!string.IsNullOrWhiteSpace(t) && !_cache.ContainsKey(to + "|" + t) && !need.Contains(t)) need.Add(t); }
            if (need.Count == 0) return;
            bool changed = false;
            int i = 0;
            while (i < need.Count)
            {
                var chunk = new List<string>(); int len = 0;
                while (i < need.Count && chunk.Count < 40 && len < 1200) { chunk.Add(need[i]); len += need[i].Length + 1; i++; }
                try
                {
                    string joined = string.Join("\n", chunk);
                    string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=tr&tl=" + Uri.EscapeDataString(to) + "&dt=t&q=" + Uri.EscapeDataString(joined);
                    var r = Web.Get(url, 7000);
                    if (r.Ok)
                    {
                        var outer = J.L(J.Parse(r.Text));
                        var sb = new StringBuilder();
                        if (outer != null && outer.Count > 0) { var segs = J.L(outer[0]); if (segs != null) foreach (var s in segs) { var a = J.L(s); if (a != null && a.Count > 0) sb.Append(J.S(a[0])); } }
                        var lines = sb.ToString().Replace("\r", "").Split('\n');
                        // Satir sayisi tutuyorsa birebir esle; tutmuyorsa o parcayi atla (orijinal kalir)
                        if (lines.Length == chunk.Count)
                            lock (_lk) { for (int k = 0; k < chunk.Count; k++) { _cache[to + "|" + chunk[k]] = lines[k].Trim().Length > 0 ? lines[k].Trim() : chunk[k]; changed = true; } }
                    }
                }
                catch { }
            }
            if (changed) Save();
        }

        // Menudeki kategori + urun adlarini yerinde cevirir (once toplu Prime, sonra onbellekten uygula).
        public static Dictionary<string, object> TranslateMenu(Dictionary<string, object> menu, string to)
        {
            if (string.IsNullOrEmpty(to) || to == "tr") return menu;
            var names = new List<string>();
            foreach (var c in J.LL(J.Get(menu, "categories")))
            {
                var cd = J.DD(c); names.Add(J.S(cd, "name"));
                foreach (var it in J.LL(J.Get(cd, "items"))) names.Add(J.S(J.DD(it), "name"));
            }
            Prime(names, to);
            foreach (var c in J.LL(J.Get(menu, "categories")))
            {
                var cd = J.DD(c); cd["tr"] = J.S(cd, "name"); cd["name"] = T(J.S(cd, "name"), to);   // tr: orijinal ad (kiosk ikon eslesmesi icin)
                foreach (var it in J.LL(J.Get(cd, "items"))) { var id = J.DD(it); id["tr"] = J.S(id, "name"); id["name"] = T(J.S(id, "name"), to); }
            }
            return menu;
        }
    }
}
