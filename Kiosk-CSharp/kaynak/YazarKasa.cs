// Yazar Kasa (ÖKC / Mali Fiş) entegrasyon KATMANI - 03.10.2026.
// ŞU AN: sadece AYARLAR + stub. Port/marka girilince mali fiş BASILMAZ; gerçek entegrasyon
// (marka marka: Ingenico/Hugin/Beko/Profilo/Verifone... GMP3 protokolü / üretici SDK'sı + mali onay/TSM)
// ileride PrintReceipt içine eklenecek. Burada tüm GİRİŞ ALANLARI ve bağlanacak YER hazır.
using System;
using System.Collections.Generic;
using Alfa;

namespace Kiosk
{
    public static class YazarKasa
    {
        static string V(string k) { return J.S(Cfg.Read(), k); }
        public static bool Enabled { get { string e = V("fiscalEnabled"); return e == "true" || e == "1"; } }

        // Panelden okunup yazilan tum alanlar (entegrasyonun ihtiyac duyacagi bilgiler onceden toplanir).
        public static Dictionary<string, object> Settings()
        {
            return J.Obj(
                "fiscalEnabled", Enabled,
                "fiscalType", V("fiscalType"),            // marka: ingenico/hugin/beko/profilo/verifone/token...
                "fiscalConn", V("fiscalConn"),            // baglanti: serial / tcp / usb
                "fiscalPort", V("fiscalPort"),            // COM portu (seri) veya IP adresi (tcp)
                "fiscalBaud", V("fiscalBaud"),            // seri hiz (9600/19200/115200)
                "fiscalTcpPort", V("fiscalTcpPort"),      // tcp port
                "fiscalSerial", V("fiscalSerial"),        // ECR/kasa seri no
                "fiscalGmp", V("fiscalGmp"),              // GMP3 protokol surumu
                "fiscalReceiptType", V("fiscalReceiptType"), // fis / fatura
                "fiscalMerchantNo", V("fiscalMerchantNo"),   // uye isyeri no (varsa)
                "fiscalNote", V("fiscalNote")
            );
        }

        /* Siparis sonrasi mali fis bas. order: {orderNo, table, total, items:[{name,quantity,price}]}.
           ŞU AN NO-OP: ayar yoksa/kapaliysa atlar; acik olsa bile henuz gercek cihaz surucusu yok,
           sadece loglar ve "pending" doner. Gercek entegrasyon:
             switch(fiscalType){ case "ingenico": ... GMP3 ... break; case "hugin": ... }  <-- buraya. */
        public static Dictionary<string, object> PrintReceipt(Dictionary<string, object> order)
        {
            if (!Enabled) return J.Obj("ok", true, "skipped", "ÖKC kapalı");
            try
            {
                Log.Write("[okc] Mali fiş istendi (entegrasyon henüz yok) - marka=" + V("fiscalType") +
                          " baglanti=" + V("fiscalConn") + " port=" + V("fiscalPort") + " fis=" + V("fiscalReceiptType"));
                // TODO: secili markaya gore cihazla haberlesip mali fis bas. Simdilik beklemede.
                return J.Obj("ok", false, "pending", "ÖKC entegrasyonu henüz eklenmedi.");
            }
            catch (Exception e) { Log.Write("[okc] hata: " + e.Message); return J.Obj("ok", false, "error", e.Message); }
        }

        // Panelden "cihazı test et" - su an sadece ayarlarin dolu olup olmadigini soyler.
        public static Dictionary<string, object> Test()
        {
            if (!Enabled) return J.Obj("ok", false, "error", "ÖKC kapalı (önce 'Aktif' yapın).");
            if (V("fiscalType").Length == 0) return J.Obj("ok", false, "error", "Marka seçilmedi.");
            if (V("fiscalPort").Length == 0) return J.Obj("ok", false, "error", "Port/adres girilmedi.");
            return J.Obj("ok", true, "info", "Ayarlar tam. Cihaz entegrasyonu eklendiğinde bağlantı burada denenecek.");
        }
    }
}
