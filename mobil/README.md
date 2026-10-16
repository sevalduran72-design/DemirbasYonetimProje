# Demirbaş Yönetim Sistemi - Mobil

Flutter istemcisi, ASP.NET Core API'sindeki cookie tabanlı oturumu kullanır.
Oturum çerezi `flutter_secure_storage` ile saklanır; uygulama açılışında
doğrulanır ve çıkışta hem sunucudan hem cihazdan silinir.

## Geliştirme

API'yi `http://localhost:5181` adresinde çalıştırıp Android emülatöründe:

```powershell
flutter run
```

Android emülatöründeki `10.0.2.2`, geliştirme makinesinin `localhost`
adresine yönlenir. Gerçek cihaz veya farklı bir API adresi için:

```powershell
flutter run --dart-define=API_BASE_URL=http://192.168.1.20:5181/api
```

USB ile bağlı gerçek Android cihazda API'yi LAN'a açmadan kullanmak için USB
hata ayıklamayı etkinleştirip cihazdaki izin istemini onaylayın. Ardından:

```powershell
adb reverse tcp:5181 tcp:5181
flutter run --dart-define=API_BASE_URL=http://127.0.0.1:5181/api
```

Wi-Fi üzerinden gerçek cihaz kullanırken API'nin bilgisayarın LAN arayüzünde
dinlemesi ve güvenlik duvarından erişilebilir olması gerekir. Mevcut `http`
başlatma profili yalnızca bilgisayardaki yerel erişim içindir. HTTP trafiğine
yalnızca Android debug derlemesinde izin verilir; release derlemesi HTTPS
gerektirir.

Üretimde HTTPS kullanın ve geçerli bir TLS sertifikası yapılandırın; uygulama
sertifika doğrulamasını devre dışı bırakmaz.

## Mobil özellikler

- Yönetici ve personel hesabıyla giriş, oturumu yenileme ve güvenli çıkış
- Toplam, boşta ve arızalı cihaz istatistikleri
- Envanter arama, durum/departman filtreleri, sayfalama ve cihaz detayları
- Üretilen QR/barkod kodunun görseli ve kamera ile QR/barkod tarayıp cihaz
  detaylarına erişme (alım tarihi, garanti bitişi, zimmetli personel)
- Yöneticiler için demirbaş ekleme, düzenleme ve silme
- Zimmet ve iade geçmişini görüntüleme; yöneticiler için zimmetleme ve zimmetten düşme
- Okutulan cihazı personele teslim etme, geri kabul etme ve arıza bildirimi
- Personel hesabı ile personel kaydı eşleştirildikten sonra kişisel aktif zimmetler
- Yönetici kullanıcı ekranında kullanıcı ekleme, arama, düzenleme, rol/parola
  yönetimi ve silme
