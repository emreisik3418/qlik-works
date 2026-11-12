# Python sürümü için prompt

Bu prompt, C# `QlikSheetCleaner` aracının Python ile yeniden yazılması içindir. Bir LLM'e olduğu gibi verilebilir.
C# sürümünü geliştirirken netleşen kararlar (SheetId eşleştirme, DoSave oturumunda qNoData yasağı, app bazında paralellik, iptal davranışı) prompt'a eklenmiştir.

Temel fark: Python `ssl` modülü `.pfx` dosyasını doğrudan okuyamaz; sertifikalar QMC'den PEM formatında export edilir.

---

````text
Rol: Kıdemli bir Python geliştiricisi ve Qlik Sense Enterprise on Windows (QSEoW) API uzmanı olarak davran.

Amaç: Bir Excel dosyasında listelenen AppId + SheetId çiftlerini okuyup bu sheet'leri silen bir Python komut satırı uygulaması yaz. Duplicate tespiti önceden yapıldı. Uygulama sadece listedekileri silecek. Listede hem yayınlanmamış (Work) app'ler hem de yayınlanmış app'lerdeki kişisel sheet'ler olabilir.

## Ortam
- Python 3.11+, Windows üzerinde çalışacak (Linux'ta da çalışabilmeli)
- Qlik Sense Enterprise on Windows (sürüm: [SÜRÜMÜ YAZ])
- Kimlik doğrulama: Qlik sertifikaları, UserDirectory=INTERNAL; UserId=sa_api
- Sertifikalar QMC'den "Platform independent PEM-format" ile export edilecek: client.pem, client_key.pem, root.pem
  (Python ssl modülü .pfx'i doğrudan okuyamaz. İsteğe bağlı olarak cryptography ile .pfx'ten PEM'e dönüştürme desteği ekle.)
- QRS API (port 4242), Engine API (port 4747, WebSocket JSON-RPC 2.0)
- Qlik SDK kullanma. QRS için httpx, Engine için websockets ile ham JSON-RPC yaz.

## Kütüphaneler
- httpx (async, ssl.SSLContext ile istemci sertifikası)
- websockets (Engine; sertifika + X-Qlik-User header'ı)
- openpyxl (Excel okuma/yazma)
- tenacity (retry)
- pydantic + pydantic-settings (config; JSON/YAML dosyası + ortam değişkeni ile ezme, örn. QSC_QLIK__CLIENT_KEY_PASSWORD)
- typer veya argparse (komut satırı)
- loguru veya standart logging (konsol + günlük dönen dosya logu)
- pytest (testler)
- Bağımlılıklar pyproject.toml'da sürümleri sabitlenmiş olarak tanımlansın.

## Girdi: Excel dosyası
- openpyxl ile oku. Sayfa ve kolon adları config'ten gelsin (varsayılan: "Sheet1", "AppId", "SheetId").
- Başlık satırını ilk N satırda kolon adına göre bul (büyük/küçük harf ve boşluk duyarsız).
- Boş satırları atla. Geçersiz GUID'leri, tekrar eden satırları ve eksik değerleri raporla, işleme alma.
  - Geçersiz GUID / eksik değer → Status=Error; tekrar eden satır → Status=Skipped (gerekçede ilk geçtiği satır).
  - GUID'ler D, N veya {…} formatında kabul edilsin, küçük harfe normalize edilsin.

## Ön kontrol (her satır için, QRS)
1. App var mı? (GET /qrs/app/{id}) → app.published ve app.owner değerini al. 404 → Error "App bulunamadı".
2. Sheet bu app'e ait ve objectType = 'sheet' mi? (GET /qrs/app/object/full?filter=app.id eq {appId} and objectType eq 'sheet'; app başına bir kez çağır, önbellekle)
   - Excel'deki SheetId engineObjectId (hub URL'indeki id) ya da QRS object id olabilir. Config: SheetIdMatch = Auto | EngineObjectId | QrsId. Auto'da önce engineObjectId, sonra QRS id ile eşleştir. Birden fazla eşleşme varsa Error.
   - Bulunamazsa nedenini teşhis et: başka türde bir nesne mi (örn. masterobject), başka bir app'e mi ait, hiç yok mu.
3. Sheet'in owner, published, approved ve name bilgilerini al. Engine çağrılarında engineObjectId, QRS çağrılarında QRS id kullan.
4. Her satırı aşağıdaki senaryolardan birine ata (SheetClassifier, yan etkisiz saf fonksiyon):

   Senaryo A – Yayınlanmamış app (app.published = false):
   → Engine API ile silinecek.

   Senaryo B – Yayınlanmış app, kişisel sheet (sheet.published = false, approved = false):
   → QRS API ile silinecek.

   Senaryo C – Yayınlanmış app, community sheet (sheet.published = true, approved = false):
   → Varsayılan olarak ATLA (Reason: "Community sheet").
   → Config'te allow_community_sheet_delete = true ise: önce PUT /qrs/app/object/{id}/unpublish, ardından Senaryo B gibi sil.

   Senaryo D – Yayınlanmış app, base sheet (approved = true):
   → HER ZAMAN ATLA (Reason: "Base sheet, sadece app yeniden publish edilerek kaldırılabilir"). Bu kural config ile değiştirilemesin; silme katmanında da ikinci bir koruma olsun.

## Silme
Senaryo A (Engine API):
- App sahibi adına bağlan (config: scenario_a_engine_identity = app_owner | service_account, varsayılan app_owner).
- App'i OpenDoc ile bir kez aç (qNoData=false; DoSave yapılacak oturum ASLA verisiz açılmasın).
- Her SheetId için önce GetObject ile qGenericType == "sheet" olduğunu doğrula, sonra DestroyObject çağır (qSuccess kontrolü).
- En sonda bir kez DoSave yap. DoSave başarısızsa ilgili satırlar Error ("DoSave başarısız; silme kalıcı olmayabilir").
- Master item'lara dokunma.

Senaryo B/C (QRS API):
- DELETE /qrs/app/object/{sheetId}
- Bu app'ler için DoSave ÇAĞIRMA ve app'i Engine üzerinden değiştirme (Engine yalnızca yedek için, salt okuma).
- Silmeden hemen önce sheet'in hâlâ approved = false olduğunu ve published değerinin beklendiği gibi olduğunu tekrar kontrol et (GET). Değiştiyse atla. C için unpublish'ten sonra da tekrar kontrol et.

Genel:
- Bir sheet'te hata olursa logla ve devam et. Bir app'e bağlanılamazsa o app'i atla, diğerlerine devam et.
- B ve C aynı stratejide işlensin; owner başına tek bir Engine oturumu açılsın.

## Engine bağlantısı (ayrıntı)
- URL: wss://{host}:4747/app/{appId}/identity/{rastgele-guid} (kullanıcının açık oturumuna bağlanmamak için izole oturum; config ile kapatılabilsin)
- Header: X-Qlik-User: UserDirectory={dir}; UserId={id}
- İlk gelen OnConnected bildirimini bekle. OnAuthenticationInformation(mustAuthenticate=true), OnLicenseAccessDenied, OnSessionClosed vb. bildirimleri hata olarak ele al.
- id → Future eşlemeli tek bir okuma döngüsü; istek bazında zaman aşımı; "error" alanını özel bir istisnaya çevir.
- Kullanılan metotlar: OpenDoc (handle -1), GetObject, GetFullPropertyTree, DestroyObject, DoSave.

## Yedekleme (--execute modunda zorunlu)
- Senaryo A: silmeden önce app'in .qvf yedeğini QRS export ile al (POST /qrs/app/{id}/export/{token}?skipData=…, dönen downloadPath'i aynı QRS bağlantısıyla stream ederek indir, iş bitince DELETE /qrs/app/{id}/export/{token} ile temizle). Yol: {yedek_klasörü}/{AppId}/{AppId}_{zaman}.qvf. Boş dosya hata sayılsın.
- Senaryo B/C: .qvf export'u kişisel sheet'leri İÇERMEZ. Bunun yerine Engine API'ye sheet sahibi adına bağlan (sertifika + X-Qlik-User header'ı ile owner'ı taklit et; salt okuma oturumu qNoData=true ile açılabilir), GetObject(sheetId) → GetFullPropertyTree çıktısını JSON olarak kaydet: {yedek_klasörü}/{AppId}/{SheetId}.json
  - JSON'da propertyTree (qPropEntry olduğu gibi) + geri yükleme için meta: appId, appName, engineObjectId, qrsObjectId, title, owner, published, approved, scenario, createdAt, backupFormatVersion.
  - Önce geçici dosyaya yaz, sonra taşı. Önceki bir yedek varsa ezme, zaman damgasıyla yeniden adlandır.
- Yedek alınamazsa o satırda/app'te silme YAPMA, Status = Error.

## Güvenlik
- Varsayılan mod DRY-RUN: ön kontrolleri yap, rapor üret, hiçbir şey silme. Gerçek silme için --execute gereksin.
- Çalıştırmadan önce senaryo bazında özet göster (A/B/C/D sayıları, silinecek ve atlanacak toplam) ve "EVET" yazılmadan devam etme (--yes ile atlanabilsin).
- Paralellik config'ten ayarlansın (varsayılan 1). Paralellik APP bazında olsun: aynı app'in sheet'leri her zaman tek worker'da sırayla işlensin (asyncio.Semaphore).
- Ctrl+C: nazikçe dur. Senaryo A'da yeni DestroyObject başlatma, ama bellekte zaten silinmiş sheet'ler için DoSave'i yine de çalıştır. İşlenmeyen satırları "İşlenmedi (iptal)" olarak raporla, rapor her durumda yazılsın.
- Sunucu sertifikasını root.pem ile doğrula; hostname uyuşmazlığına yalnızca config izin verirse göz yum. Doğrulamayı kapatma (verify=False) seçeneği olmasın.

## Çıktı raporu
- Girdi Excel'inin kopyasını oluştur, her satıra şu kolonları ekle: Scenario (A/B/C/D), Status (WouldDelete / Deleted / Skipped / Error), Reason, AppName, AppPublished, SheetTitle, SheetOwner, SheetPublished, SheetApproved, BackupPath, ProcessedAt.
- Varsayılan çıktı adı: {girdi}_result_{dryrun|execute}_{zaman}.xlsx. Senaryo × durum tablosunu içeren bir "CleanupSummary" sayfası ekle.
- Orijinal dosyanın üzerine yazma (--output girdiyle aynıysa çalışmayı reddet).
- Konsolda senaryo ve durum bazında özet, en sık atlama/hata nedenleri.
- Konsol + dosya loglama (günlük dönen dosya, Debug seviyesinde her QRS/Engine çağrısı).
- Çıkış kodları: 0 başarılı, 1 bazı satırlar Error, 2 kritik hata, 3 iptal / onay verilmedi.

## Teknik gereksinimler
- config.json veya config.yaml: sunucu, sertifika yolları (client.pem, client_key.pem, root.pem, isteğe bağlı key parolası), kullanıcı, Excel sayfa/kolon adları, yedek klasörü, paralellik, allow_community_sheet_delete (varsayılan false), zaman aşımları, retry sayısı. Parolalar ortam değişkeniyle verilebilsin.
- Komut satırı: --input, --output, --execute, --yes, --config
- Modüller: excel_reader, qrs_client, engine_client (engine_session), sheet_classifier, preflight, backup_service, sheet_deleter (engine_strategy ve qrs_strategy ayrı sınıflar), report_writer, summary, cli
- Tüm QRS çağrılarında X-Qlik-Xrfkey header'ı + xrfkey query parametresi kullanılsın (her istekte rastgele 16 karakter, aynı değer).
- asyncio / async-await baştan sona; zaman aşımları asyncio.timeout ile.
- tenacity ile retry SADECE okuma çağrılarında olsun: QRS GET, export dosyasının indirilmesi, Engine bağlantısını açma + OpenDoc. Silme, unpublish, export başlatma (POST), DestroyObject ve DoSave çağrılarında retry olmasın. Retry yalnızca geçici hatalarda (bağlantı hatası, zaman aşımı, 408/429/5xx) yapılsın.
- Tip ipuçları (type hints), dataclass veya pydantic modelleri; ruff/mypy ile temiz olsun.
- pytest testleri: sınıflandırıcı (özellikle D'nin config'ten bağımsız olarak her zaman atlandığı), Excel okuma doğrulamaları, raporun kopya olması ve orijinalin değişmemesi.

## Beklenen çıktı
1. Proje klasör yapısı
2. Tüm .py dosyalarının eksiksiz kodu
3. config dosyası ve örnek girdi Excel'i yapısı
4. pyproject.toml (veya requirements.txt) ve paket sürümleri
5. Kurulum ve çalıştırma adımları (QMC'den PEM sertifika export'u, sanal ortam kurulumu, örnek dry-run ve execute komutları)
6. Bilinen kısıtlar ve riskler (açık kullanıcı oturumları, sa_api'nin owner taklidi için gereken yetkiler — sertifikanın kendisinin tam yetki olduğu, silinen sheet'e referans veren bookmark/story'ler, JSON yedekten geri yükleme yöntemi: owner adına bağlan → CreateObject(qInfo: aynı qId, qType sheet) → SetFullPropertyTree; yayınlanmış app'te DoSave yok, C sheet'iyse Publish)

Kodu yazmadan önce belirsiz gördüğün noktaları kısaca listele ve varsayımlarını belirt.
````
