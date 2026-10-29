# Roomora Backend

Roomora'nin backend servisi; kimlik dogrulama, ev/gezi gruplari, harcamalar, duzenli giderler, odemeler ve borc-alacak hesaplarini tek bir API altinda toplar.

Yapi, is kurallarini ve veri erisimini ayri katmanlarda tutan bir Clean Architecture duzeni uzerine kuruludur. Ama amac yalnizca teknik olarak duzgun olmak degil; urun tarafinda hizli ilerlerken bakimi da kolay tutmaktir.

## Sorumluluklar

- Auth ve JWT tabanli oturum yonetimi
- Ev olusturma, uye daveti ve grup akislari
- Harcama olusturma, guncelleme ve silme
- Duzensiz gider, duzenli gider ve taksitli plan senaryolari
- Ledger tabanli borc-alacak hesaplamalari
- Odeme ve onay mekanizmalari
- Fis, belge ve destekleyici servis akislari
- Secili uyelerle paylasilan gezi giderleri ve kullaniciya ozel hizli harcama sablonlari

## Teknoloji

- .NET 8
- ASP.NET Core Web API
- Entity Framework Core
- SQL Server
- MediatR
- FluentValidation

## Yerel Calistirma

```bash
dotnet restore
dotnet build
dotnet run --project EvArkadasim.API
```

Gercek cihaz testi icin API gelistirme ortaminda `0.0.0.0:5118` uzerinden dinler. Yerel baglanti, JWT, SMTP ve OCR degerlerini Git'e eklenmeyen `EvArkadasim.API/appsettings.Local.json` dosyasinda tanimlayin.

Tekrar calistirilabilir temel API kontrolu:

```powershell
.\scripts\smoke-test.ps1 -Email <test-email> -Password <test-password> -UserId <id> -HouseId <id>
```

Tum bagimliliklari Docker ile calistirmak:

```powershell
docker compose -f compose.local.yml up --build
```

Blue-green production yayini, rollback ve yedekleme akisi icin
[`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) belgesine bakin.

## Uretim

- Tum `CHANGE_ME` degerlerini ortam degiskenleriyle saglayin.
- API'yi TLS sonlandiran bir reverse proxy arkasinda yayinlayin.
- `privacy.html` ve `account-deletion.html` adreslerinin herkese acik oldugunu dogrulayin.
- SMTP parolasini ve OCR anahtarini yalnizca sunucu secret yonetiminde saklayin.

## Katmanlar

- `EvArkadasim.API`: controller, middleware ve uygulama girisi
- `Application`: command-query akislari ve is kurallari
- `Persistence`: veritabani, EF Core ve repository katmani
- `Domain`: entity ve enum tanimlari
- `Core`: ortak yardimci yapilar ve guvenlik bilesenleri

[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) istek, veri ve deployment akislarini gosterir. `Persistence/Migrations` gercek veritabani gecmisidir; uygulama yayindayken adlari veya sirasiyla oynanmaz.

## Yaklasim

Bu repo, urun ihtiyaclarina gore buyuyen ama dagilmamaya calisan bir backend yapisi sunar. Kodun amaci yalnizca endpoint acmak degil; kullanicinin gercekte yasadigi senaryolari guvenilir sekilde tasimaktir.
