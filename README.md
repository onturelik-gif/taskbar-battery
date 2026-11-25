# TaskbarBattery

Windows 11 görev çubuğunda gizli simgeler (`^`) okunun hemen solunda kablosuz **mouse** ve **kulaklık** pil yüzdesini gösteren hafif (~20 KB) yerel C#/.NET uygulaması.

## Desteklenen Cihazlar
- **Razer DeathAdder V3 Pro** (2.4 GHz USB alıcı `VID_1532 / PID_00B7` & kablolu `PID_00B6`) — Razer Synapse çalışırken çakışmadan okur.
- **Corsair Void Elite Wireless** (2.4 GHz USB alıcı `VID_1B1C / PID_0A51`) — Corsair iCUE çalışırken çakışmadan okur.

## Özellikler
- Şeffaf, kenarlıksız (per-pixel alpha) alt alta kompakt görünüm.
- `Shell_TrayWnd` görev çubuğuna bağlı Z-order (tıklamalarda kapanıp açılma / göz kırpma yapmaz).
- Tam ekran oyun ve videolarda otomatik gizlenir.
- Renk kodları: normalde beyaz, şarjda yeşil + şimşek ikonu, `%25` ve altı sarı, `%15` ve altı kırmızı.
- Sağ tık menüsü: **Şimdi Yenile**, **Sola/Sağa Kaydır**, **Konumu Sıfırla**, **Windows ile Başlat**, **Çıkış**.

## Derleme
Windows'ta yerleşik gelen .NET Framework 4.x derleyicisi (`csc.exe`) ile harici hiçbir bağımlılık olmadan derlenir:

```bat
build.bat
```
