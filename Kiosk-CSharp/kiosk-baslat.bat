@echo off
rem EnsariPOS Kiosk - tam ekran baslatici. Edge'i kiosk (tam ekran, cikis yok) modunda acar.
rem Otomatik baslatma: bu .bat'in kisayolunu Baslangic klasorune (shell:startup) ya da Gorev
rem Zamanlayici'ya "oturum acildiginda" koyun. Kiosk servisi (EnsariKiosk) zaten arka planda calisir.
rem Edge yoksa Chrome kullanilir. Port config.json'daki "port" (varsayilan 8790) ile ayni olmali.
set URL=http://localhost:8790/
start "" msedge.exe --kiosk "%URL%" --edge-kiosk-type=fullscreen --no-first-run --disable-pinch --overscroll-history-navigation=0 2>nul
if errorlevel 1 start "" chrome.exe --kiosk "%URL%" --no-first-run 2>nul
