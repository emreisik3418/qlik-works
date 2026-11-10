# QlikSheetCleaner

Excel dosyasında listelenen **AppId + SheetId** çiftlerini Qlik Sense Enterprise on Windows (QSEoW) üzerinden silen .NET 8 console uygulaması. Varsayılan mod **DRY-RUN**'dır; hiçbir şey silinmez.

| Senaryo | Koşul | İşlem |
|---|---|---|
| **A** | `app.published = false` (Work app) | `.qvf` yedeği → Engine `OpenDoc` → her sheet için `DestroyObject` → tek `DoSave` |
| **B** | Yayınlanmış app, `sheet.published = false`, `approved = false` | Owner adına JSON yedek → `approved=false` tekrar kontrolü → `DELETE /qrs/app/object/{id}` |
| **C** | Yayınlanmış app, `published = true`, `approved = false` | Varsayılan **ATLA** (`Community sheet`). `AllowCommunitySheetDelete=true` ise: JSON yedek → `PUT …/unpublish` → tekrar kontrol → `DELETE` |
| **D** | Yayınlanmış app, `approved = true` | **HER ZAMAN ATLA**. Bu kural koda gömülüdür, config ayarı yoktur |

---

## 1. Belirsiz noktalar ve varsayımlar

| # | Belirsizlik | Varsayım / çözüm |
|---|---|---|
| 1 | Qlik sürümü belirtilmemiş | **May 2023 veya sonrası** varsayıldı. Kullanılan endpoint'ler (`/qrs/app/object/full`, `export/{token}`, `unpublish`) Feb 2018 ve sonrasında mevcut. |
| 2 | Excel'deki `SheetId`, **engineObjectId** (hub URL'indeki `…/sheet/{id}`) mi yoksa **QRS object id** mi? | `SheetIdMatch = Auto`: önce `engineObjectId`, bulunamazsa QRS `id` ile eşleşir. Engine çağrılarında her zaman QRS kaydındaki `engineObjectId`, QRS çağrılarında QRS `id` kullanılır. Birden fazla eşleşme bulunursa satır işlenmez. |
| 3 | Senaryo A'da Engine oturumu hangi kimlikle açılacak? | `ScenarioAEngineIdentity = AppOwner` (varsayılan): X-Qlik-User ile app sahibi olarak bağlanılır. Work app'te sheet'ler sahibine ait olduğu için bu oturum hepsini görür. `ServiceAccount` seçilirse sa_api ile bağlanılır. |
| 4 | "Geçersiz GUID" kontrolü | AppId ve SheetId'nin ikisi de GUID olmalı (`D`, `N` veya `{…}` formatı). Qlik client'ın oluşturduğu sheet'lerin id'leri GUID'dir. |
| 5 | Geçersiz/eksik/tekrar eden satırların rapordaki durumu | Geçersiz GUID veya eksik değer → `Error`. Tekrar eden satır → `Skipped` (gerekçede ilk geçtiği satır yazar). Tamamen boş satırlar raporlanmaz. |
| 6 | Yedek dosya adları | Senaryo A: `{BackupFolder}/{AppId}/{AppId}_{yyyyMMdd_HHmmss}.qvf`. B/C: `{BackupFolder}/{AppId}/{SheetId}.json`. Önceki bir çalıştırmadan kalan JSON dosyası ezilmez; zaman damgasıyla yeniden adlandırılır. |
| 7 | JSON içeriği | `GetFullPropertyTree` çıktısı (`qPropEntry`) `propertyTree` alanında olduğu gibi saklanır. Etrafına geri yükleme için meta bilgi eklenir (owner, app, QRS id, bayraklar). |
| 8 | Yedek oturumunda veri yükleme | B/C yedek oturumları salt okumadır ve `qNoData=true` ile açılır (`BackupOpenWithoutData`). Senaryo A oturumunda `DoSave` yapıldığı için bu oturum **asla noData ile açılmaz**; aksi halde app verisiz kaydedilebilirdi. |
| 9 | Retry kapsamı | Polly retry: QRS GET çağrıları, `.qvf` indirme ve Engine bağlantısını açma (`OpenDoc`). **Retry yok**: `DELETE`, `unpublish`, export başlatma (POST), `DestroyObject`, `DoSave`. |
| 10 | Paralellik | İşler **app bazında** yürür: farklı app'ler paralel çalışır, aynı app'in sheet'leri ise her zaman tek worker'da sırayla işlenir. Bu, aynı app'te çakışan `DoSave` çağrılarını önler. |
| 11 | Senaryo A sırasında Ctrl+C | Yeni `DestroyObject` çağrısı yapılmaz. Bellekte zaten yok edilmiş sheet'ler yine de **`DoSave` ile kalıcı hale getirilir**; aksi halde başka bir kullanıcının autosave'i bunları zaten kalıcı yapabilirdi. |
| 12 | Onay sorusu | Yalnızca `--execute` modunda ve silinecek en az bir sheet varsa sorulur. DRY-RUN'da hiç sorulmaz. |
| 13 | Oturum izolasyonu | Her Engine bağlantısı `/app/{id}/identity/{guid}` ile kendi oturumunu açar; böylece owner'ın açık tarayıcı oturumuna bağlanmaz. |
| 14 | Qlik ile iletişim | SDK kullanılmaz. Engine API, `ClientWebSocket` üzerinden ham JSON-RPC ile, QRS ise `HttpClient` ile çağrılır. Yalnızca sertifika + `X-Qlik-User` kullanılır; virtual proxy veya lisans devreye girmez. |

---

## 2. Klasör yapısı

```
qlik-works/
├── QlikSheetCleaner.sln
├── README.md
├── samples/
│   └── sample-input.xlsx
├── src/QlikSheetCleaner/
│   ├── QlikSheetCleaner.csproj
│   ├── appsettings.json
│   ├── Program.cs                         # System.CommandLine, Serilog, bağlantılar
│   ├── Configuration/AppSettings.cs        # config modeli + doğrulama
│   ├── Models/
│   │   ├── SheetRow.cs                     # satır + sonuç, Scenario/RowStatus/DeleteAction enum'ları
│   │   └── QrsModels.cs                    # QRS DTO'ları
│   ├── Excel/
│   │   ├── ExcelReader.cs                  # ClosedXML: başlığı bul, doğrula
│   │   └── ReportWriter.cs                 # kopya + sonuç kolonları + CleanupSummary sayfası
│   ├── Qlik/
│   │   ├── CertificateProvider.cs          # client.pfx / root.cer, sunucu sertifikası doğrulama
│   │   ├── QlikIdentity.cs                 # X-Qlik-User
│   │   ├── ReadRetryPolicy.cs              # Polly (yalnızca okuma çağrıları)
│   │   ├── QrsClient.cs                    # QRS + Xrfkey
│   │   ├── EngineClient.cs                 # wss://host:4747 bağlantısı, OpenDoc
│   │   └── EngineSession.cs                # JSON-RPC: GetObject, GetFullPropertyTree, DestroyObject, DoSave
│   └── Processing/
│       ├── AppWorkItem.cs
│       ├── SheetClassifier.cs              # A/B/C/D ataması (saf mantık)
│       ├── PreflightService.cs             # QRS ön kontrolleri
│       ├── BackupService.cs                # .qvf export + JSON property tree
│       ├── SummaryPrinter.cs               # konsol özet tabloları
│       ├── CleanupRunner.cs                # ana akış, onay, paralellik
│       └── Deletion/
│           ├── ISheetDeletionStrategy.cs
│           ├── EngineSheetDeletionStrategy.cs   # Senaryo A
│           ├── QrsSheetDeletionStrategy.cs      # Senaryo B/C
│           └── SheetDeleter.cs                  # strateji dağıtımı, son savunma hattı D kontrolü
└── tests/QlikSheetCleaner.Tests/          # xUnit: classifier, Excel okuma, rapor
```

## 3. NuGet paketleri

| Paket | Sürüm | Amaç |
|---|---|---|
| ClosedXML | 0.105.1 | Excel okuma ve rapor yazma |
| System.CommandLine | 2.0.14 | `--input --output --execute --yes --config` |
| Polly.Core | 8.9.1 | Okuma çağrılarında retry |
| Serilog, Serilog.Sinks.Console, Serilog.Sinks.File | 4.4.0 / 6.1.1 / 7.0.0 | Konsol + günlük dönen dosya logu |
| Microsoft.Extensions.Configuration.Json / Binder / EnvironmentVariables | 8.0.x | appsettings.json + `QSC_` ortam değişkenleri |

## 4. Yapılandırma (`appsettings.json`)

```jsonc
{
  "Qlik": {
    "Host": "qlik-central.sirket.local",     // sunucu sertifikasındaki ad ile aynı olmalı
    "QrsPort": 4242, "EnginePort": 4747,
    "ClientCertificatePath": "C:\\QlikCerts\\client.pfx",
    "ClientCertificatePassword": "",          // tercihen ortam değişkeni: QSC_Qlik__ClientCertificatePassword
    "RootCertificatePath": "C:\\QlikCerts\\root.cer",
    "AllowCertificateNameMismatch": false,    // true: ad uyuşmazlığına izin verilir, zincir yine root.cer ile doğrulanır
    "UserDirectory": "INTERNAL", "UserId": "sa_api",
    "RequestTimeoutSeconds": 120, "ExportTimeoutSeconds": 3600, "EngineRequestTimeoutSeconds": 600,
    "ReadRetryCount": 3, "ReadRetryBaseDelaySeconds": 2,
    "ScenarioAEngineIdentity": "AppOwner",    // AppOwner | ServiceAccount
    "BackupOpenWithoutData": true,            // yalnızca B/C yedek oturumları
    "UseIsolatedEngineSession": true
  },
  "Excel":      { "SheetName": "Sheet1", "AppIdColumn": "AppId", "SheetIdColumn": "SheetId", "HeaderSearchRows": 20 },
  "Processing": {
    "BackupFolder": "D:\\QlikSheetCleanup\\backups",
    "MaxDegreeOfParallelism": 1,
    "AllowCommunitySheetDelete": false,
    "ExportSkipData": false,                  // true: .qvf'i verisiz yedekle (daha hızlı, daha küçük)
    "SheetIdMatch": "Auto"                    // Auto | EngineObjectId | QrsId
  },
  "Logging": { "LogFolder": "logs", "MinimumLevel": "Information" }   // Debug: her QRS/Engine çağrısını gösterir
}
```

Her ayar, `QSC_` önekli ve `__` ayraçlı bir ortam değişkeniyle ezilebilir; örneğin `QSC_Processing__AllowCommunitySheetDelete=true`.

## 5. Girdi Excel'i (`samples/sample-input.xlsx`)

| AppId | SheetId | (diğer kolonlar yok sayılır) |
|---|---|---|
| 3f2504e0-4f89-11d3-9a0c-0305e82c3301 | a5b2c7d8-1e2f-4a3b-9c8d-7e6f5a4b3c21 | … |

- Sayfa adı ve kolon adları config'ten gelir. Başlık satırı ilk `HeaderSearchRows` satır içinde **kolon adına göre** bulunur; eşleştirmede büyük/küçük harf ve baştaki/sondaki boşluklar önemsenmez.
- Tamamen boş satırlar atlanır. Eksik değer, geçersiz GUID veya tekrar eden çift içeren satırlar raporlanır ama işlenmez.

### Çıktı raporu
Girdi dosyası **kopyalanır** (varsayılan ad: `{girdi}_result_{dryrun|execute}_{zaman}.xlsx`) ve her satıra şu kolonlar eklenir:
`Scenario, Status (WouldDelete/Deleted/Skipped/Error), Reason, AppName, AppPublished, SheetTitle, SheetOwner, SheetPublished, SheetApproved, BackupPath, ProcessedAt`.
Ayrıca senaryo × durum tablosunu içeren bir `CleanupSummary` sayfası eklenir. `--output` girdi dosyasıyla aynıysa uygulama çalışmayı reddeder.

## 6. Kurulum

### 6.1 Sertifikaların export'u (QMC)
1. QMC → **Certificates** → *Add machine name*: aracı çalıştıracak makinenin adı.
2. *Certificate password*: güçlü bir parola belirleyin. **Include secret key**: ✔. *Export file format*: **Windows format (.pfx)**.
3. Sunucuda `C:\ProgramData\Qlik\Sense\Repository\Exported Certificates\<makine adı>\` klasöründe `client.pfx`, `root.cer` ve `server.pfx` oluşur.
4. Aracı çalıştıracak makineye yalnızca `client.pfx` ve `root.cer` dosyalarını kopyalayın, örneğin `C:\QlikCerts\`. NTFS izinlerini aracı çalıştıran servis hesabıyla sınırlayın. **client.pfx sunucuda tam yetki verir** (bkz. Riskler).
5. Bu makineden central node'un 4242 ve 4747 portlarına erişilebilmeli; firewall'u kontrol edin.

### 6.2 Servis hesabı
- `INTERNAL\sa_api` yerleşik bir hesaptır ve varsayılan olarak RootAdmin'dir. Başka bir hesap kullanırsanız bu hesabın **RootAdmin** rolüne ya da en azından `App`, `App.Object` ve `ExportAppData` üzerinde Read/Update/Delete yetkisine sahip olması gerekir.

### 6.3 Derleme
```powershell
dotnet --version          # 8.0.x
cd qlik-works
dotnet test               # birim testleri
dotnet publish src/QlikSheetCleaner -c Release -r win-x64 --self-contained false -o C:\Tools\QlikSheetCleaner
```
`C:\Tools\QlikSheetCleaner\appsettings.json` dosyasını düzenleyin, ardından:
```powershell
setx QSC_Qlik__ClientCertificatePassword "pfx-parolası"   # ya da sadece oturum için: $env:QSC_Qlik__ClientCertificatePassword="..."
```

## 7. Çalıştırma

```powershell
cd C:\Tools\QlikSheetCleaner

# 1) DRY-RUN (varsayılan): ön kontrol + rapor, hiçbir şey silinmez
.\QlikSheetCleaner.exe --input D:\work\sheets_to_delete.xlsx

# 2) Rapor yolunu açıkça vererek DRY-RUN
.\QlikSheetCleaner.exe -i D:\work\sheets_to_delete.xlsx -o D:\work\report_dryrun.xlsx

# 3) EXECUTE: özeti gösterir, ardından "EVET" yazmanızı ister
.\QlikSheetCleaner.exe -i D:\work\sheets_to_delete.xlsx --execute

# 4) Onay sormadan EXECUTE (zamanlanmış görev / otomasyon)
.\QlikSheetCleaner.exe -i D:\work\sheets_to_delete.xlsx --execute --yes

# 5) Community sheet'leri de sil (yalnızca bu çalıştırma için)
$env:QSC_Processing__AllowCommunitySheetDelete="true"
.\QlikSheetCleaner.exe -i D:\work\sheets_to_delete.xlsx --execute

# 6) Farklı bir config dosyası
.\QlikSheetCleaner.exe -i list.xlsx -c D:\conf\prod.appsettings.json
```

**Çıkış kodları:** `0` hepsi başarılı · `1` tamamlandı ama bazı satırlar Error · `2` kritik hata (config, sertifika, dosya) · `3` iptal (onay verilmedi veya Ctrl+C).

**Önerilen sıra:** DRY-RUN → raporu inceleyin → test ortamında tek satırla `--execute` deneyin → bir sheet'i JSON yedeğinden geri yüklemeyi test edin (§9) → tam listeyle çalıştırın.

## 8. Bilinen kısıtlar ve riskler

### 8.1 Açık kullanıcı oturumları
- **Senaryo A:** Engine app'i bellekte tutar. Owner aynı anda app'i tarayıcıda açık tutuyorsa, client'ı silinen sheet'i sayfa yenilenene kadar kendi yerel durumunda tutar. Düzenlemeye devam ederse kullanıcının **autosave'i (DoSave)** bizim kaydımızla çakışabilir. Engine tarafında nesne silinmiş olduğu için pratikte genellikle geri gelmez, ancak kullanıcı hata görebilir. **Öneri:** mesai dışında çalıştırın ve Work app sahiplerini önceden uyarın.
- **Senaryo B/C:** QRS `DELETE` nesneyi repository'den kaldırır. Engine'e senkronizasyonla bildirilir, ancak app'i açık tutan kullanıcı sayfayı yenileyene kadar sheet'i görmeye devam edebilir. Çok node'lu kurulumlarda QRS → Engine senkronizasyonu gecikebilir.
- Araç kendi izole oturumunu (`/identity/{guid}`) açtığı için kullanıcı oturumuna bağlanmaz; ancak Engine app'i oturumlar arasında paylaşır.

### 8.2 Owner taklidi için gereken yetkiler
- Sertifika güveni, **client.pfx'e sahip olan herkesin** `X-Qlik-User` ile istediği kullanıcı olabilmesi demektir. Bu, sa_api'ye özel bir "taklit yetkisi" değildir: **yetkinin kendisi sertifikadır**. client.pfx'i ve parolasını gizli bilgi gibi saklayın: kısıtlı NTFS izinleri, ortam değişkeni veya vault, mümkünse ayrı bir jump host.
- Taklit edilen oturumda **owner'ın kendi security rule'ları** geçerlidir. Owner'ın app'in stream'ine *read* erişimi olmalıdır. Kullanıcı silinmişse, pasif yapılmışsa veya stream erişimini kaybetmişse `OpenDoc` başarısız olur; o satır "yedek alınamadı" gerekçesiyle `Error` olur ve **silinmez**. Bu durumda QMC'den owner'ı değiştirip yeniden çalıştırabilir ya da sheet'i elle yedekleyebilirsiniz.
- 4747 portuna doğrudan Engine bağlantıları proxy kullanmadığı için **lisans tüketmez**. Yine de Engine loglarında o kullanıcının oturumu olarak görünür; denetim ekibinizi bilgilendirin.
- QRS çağrıları (DELETE/unpublish/export) `sa_api` olarak çalışır; bu hesabın RootAdmin veya eşdeğer yetkilere sahip olması gerekir.

### 8.3 Silinen sheet'e referans veren bookmark ve story'ler
- **Bookmark**'lar silinmez. O sheet'te oluşturulmuş bir bookmark (`qBookmark.sheetId`) seçimlerini uygulamaya devam eder, ancak "sheet'e git" artık çalışmaz veya ilk sheet'e düşer.
- **Story**'lerde, o sheet'in görselleştirmelerinden alınmış snapshot'ları içeren slaytlar çalışmaya devam eder, çünkü snapshot statik bir kopyadır. "Kaynağa git" bağlantıları ise kırılır.
- Sheet'i işaret eden **hub linkleri / NPrinting / mashup URL'leri** (`/sheet/{id}`) 404 döner.
- **Master item**'lara dokunulmaz. Yalnızca sheet ve altındaki nesneler silinir (`DestroyObject`, sheet'in kendi alt nesnelerini de siler).
- Gerekirse, silmeden önce sheet'e referans veren bookmark'ları `GET /qrs/app/object/full?filter=app.id eq {appId} and objectType eq 'bookmark'` ile listeleyip içeriklerini Engine'de kontrol ederek bulabilirsiniz.

### 8.4 Yedekleme kısıtları
- **`.qvf` export (A):** Work app'te export, app'in içerdiği tüm nesneleri kapsar. **Sheet'lerin yedekte olduğunu bir testte doğrulayın** (import edip bakın). `ExportSkipData=false` ile büyük app'ler büyük ve uzun süren dosyalar üretir; `ExportTimeoutSeconds` değerini ve disk alanını buna göre ayarlayın. Export'un `downloadPath`'i (`/tempcontent/...`) 4242 portundan QRS üzerinden indirilir.
- **JSON (B/C):** `GetFullPropertyTree`, sheet'i ve tüm alt görselleştirmelerini içerir. Referans verilen master item'ları, bookmark'ları ve `qEmbeddedSnapshotRef` görsellerini **içermez**. Master item'lar silinmediği için geri yüklemede zaten mevcutturlar.
- Bir C sheet'i unpublish edilip silindiğinde, community/published durumu otomatik geri gelmez. Gerekirse geri yüklemeden sonra yeniden publish edin.

### 8.5 Diğer
- **Yarış koşulları:** ön kontrol ile silme arasında bir app publish edilebilir veya bir sheet approve edilebilir. B/C satırlarında silmeden hemen önce `approved=false` ve `published` tekrar kontrol edilir; değişmişse satır atlanır. Senaryo A'da Engine'de `qGenericType=sheet` kontrol edilir.
- **Senaryo A'da `DoSave` hatası:** nesneler bellekte yok edilmiş ama kalıcı hale gelmemiştir. Satır "DoSave başarısız" gerekçesiyle `Error` olarak işaretlenir. App yeniden yüklenirse sheet'ler geri gelebilir, ya da başka bir oturumun kaydı onları kalıcı hale getirebilir.
- **Çok node'lu kurulum:** central node'u (QRS master'ın çalıştığı node) kullanın. Engine bağlantılarında app, bağlandığınız node'da açılır.
- **Paralellik > 1:** Engine RAM'i ve QRS üzerindeki yükü artırır. Büyük listelerde bile 2–4 arası değerler önerilir.

## 9. JSON yedekten geri yükleme (Senaryo B/C)

Örnek yedek dosyası yapısı:
```json
{
  "backupFormatVersion": 1,
  "appId": "…", "engineObjectId": "…", "qrsObjectId": "…",
  "title": "…", "owner": { "userDirectory": "CORP", "userId": "user2" },
  "published": false, "approved": false, "scenario": "B",
  "propertyTree": { "qProperty": { "qInfo": { "qId": "…", "qType": "sheet" }, … }, "qChildren": [ … ] }
}
```

Adımlar (Engine JSON-RPC; aracın kullandığı bağlantı yöntemiyle aynı: sertifika + yedekteki `owner` ile `X-Qlik-User`):
1. `wss://{host}:4747/app/{appId}` adresine `X-Qlik-User: UserDirectory={owner.userDirectory}; UserId={owner.userId}` header'ıyla bağlanın.
2. `OpenDoc { qDocName: appId }` → `appHandle`.
3. Aynı id ile boş bir sheet oluşturun:
   `CreateObject (handle=appHandle) { qProp: { qInfo: { qId: "<engineObjectId>", qType: "sheet" } } }` → `sheetHandle`
4. Alt görselleştirmeler dahil tam ağacı yükleyin:
   `SetFullPropertyTree (handle=sheetHandle) { qPropEntry: <propertyTree> }`
5. **Yayınlanmış app:** kullanıcının oluşturduğu nesne otomatik olarak o kullanıcının *kişisel* sheet'i olur ve repository'ye yazılır. **DoSave çağırmayın** (yayınlanmış app kaydedilemez).
   C sheet'iyse: `Publish (handle=sheetHandle)` ile yeniden community yapın.
6. Hub'da owner olarak oturum açıp sheet'in "My sheets" altında olduğunu kontrol edin.

> Senaryo A (Work app) sheet'i `.qvf`'ten geri yüklenir: QMC → Apps → Import ya da `POST /qrs/app/upload`. Import **yeni bir app id** oluşturur. Ardından ya sheet'i Engine ile kopyalayın (`GetFullPropertyTree` → orijinal app'te `CreateObject` + `SetFullPropertyTree` + `DoSave`) ya da app'i `PUT /qrs/app/{newId}/replace?app={origId}` ile değiştirin.
