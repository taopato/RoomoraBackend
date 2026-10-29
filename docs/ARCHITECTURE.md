# Backend mimarisi

```mermaid
flowchart LR
    Mobile[Roomora Mobile] --> API[ASP.NET Core API]
    Web[Roomora Web] --> API
    API --> App[Application / use case]
    App --> Domain[Domain / is kurallari]
    App --> Ports[Repository arayuzleri]
    Ports --> Persistence[Persistence / EF Core]
    Persistence --> DB[(SQL Server)]
    API --> OCR[Roomora OCR servisi]
```

API katmani kimlik dogrulama, endpoint ve hata sozlesmesinden; Application katmani harcama/odeme akisi ve dogrulamadan; Domain modellerden; Persistence veri erisimi ve EF migration'larindan sorumludur. Core ortak altyapiyi tasir. Ev ve gezi gruplari ayni ledger'i kullanir; ev odakli planli faturalar geziye zorlanmaz.

```mermaid
sequenceDiagram
    participant M as Mobil istemci
    participant A as API
    participant U as Application
    participant D as SQL Server
    M->>A: POST harcama + odeyen + katilimcilar
    A->>U: Komut ve kimlik bilgisi
    U->>U: Yetki, tutar ve paylari dogrula
    U->>D: Harcama ve ledger kayitlari
    D-->>U: Kalici kimlikler
    U-->>A: Sonuc
    A-->>M: Kayit ve guncel durum
```

Silme gerektiren finansal akislarda soft delete / iptal semantigi korunur. Yayinlanmis `Persistence/Migrations` dosyalari elle yeniden adlandirilmaz veya topluca silinmez. Dagitim ve geri alma ayrintilari `docs/DEPLOYMENT.md` icindedir.
