# รายงานการรวม Source AzaelX-Tools 1.0.0 (Fresh Repository Baseline)

## แหล่งที่ใช้
- **ฐานระบบ:** Source ชุดแรก (`AzaelX.7z`) — AuthService, source metadata/catalogue, models และ download/install pipeline
- **ฐาน GUI/แอป:** Source GUI AzaelX-Tools 1.0.5 — neon theme, Settings/Downloads/Fixes UI, Device Authorization window, update dialog และ updater ของ AzaelX
- **OnlineFix/auth UX:** รวมการแจ้งเตือนบัญชีชั่วคราว `@bot.lua.tools`, sign-in/redeem code และข้อความ 401 ที่อธิบายวิธีแก้

## ขอบเขตและความเข้ากันได้
- `CreateManifestJob` และ `DownloadViewModel` มาจากสาย Source ที่มี signature `appId, gameName, sourceName, needsKey` ตรงกัน
- `AppConfig` รักษาค่าที่จำเป็นต่อ Service ชุดแรก แต่กำหนด app identity/data path/authorization ให้เป็น AzaelX และ self-update ได้จาก `AzaelX-Dev/AzaelX-Shop-Tool` เท่านั้น
- เพิ่ม GUI resources ของ 1.0.5 และรวม key ของ English/Thai เข้ากับ key เดิมของ Source แรก เพื่อไม่ให้ลบข้อความที่ Service/ViewModel เดิมยังอ้างถึง
- GUI หลักยังไม่แสดง Plugin/CloudRedirect pages และไม่เพิ่ม FastFetch UI/flow; `PluginAddService` เก็บช่อง `FastFetch` ใน response schema เพื่อความเข้ากันได้ แต่กำหนดเป็น `false` เพื่อไม่เลือกแหล่งดาวน์โหลดอัตโนมัติ
- โค้ด legacy ของ localhost update endpoint ใช้ delegate ไปยัง update dialog แบบยืนยันก่อนของ 1.0.5 และไม่เรียก `CheckAndStageAsync` หรือ auto-update plugin API รุ่นเก่าที่ไม่มีใน updater ปัจจุบัน
- เวอร์ชันตั้งเป็น `1.0.0` ใน project file เท่านั้น นี่ไม่ใช่การสร้าง Git tag หรือ Release

## สถานะการตรวจสอบ
- ตรวจโครงสร้างไฟล์และ XML ของ XAML ทั้ง 17 ไฟล์, project และ resource files
- ตรวจ event handlers ที่ประกาศใน XAML เทียบกับ code-behind: ไม่พบสมาชิกที่ขาดจากการตรวจแบบ static
- ตรวจ resource keys: English/Thai มี 595 keys เท่ากัน; references ที่ตรวจจาก C#/XAML มี accessor ครบ
- รัน `python scripts/check-i18n.py`: ผ่าน (XML, key parity และ placeholders)
- ตรวจ `CreateManifestJob`: caller และ factory ใช้ signature ที่รองรับ `sourceName`/`needsKey` ตรงกัน
- แก้ static API mismatch ที่รายงานจาก Build รอบแรก: การอ้าง `SettingsService.FastFetch`, `App.RunUpdateFlow` และ `UpdateService.CheckAndStageAsync`; updater endpoint ใช้ flow แบบ consent-first ของ GUI 1.0.5 แทน
- เพิ่ม `#nullable enable` ให้ resource accessor เพื่อลด warning CS8669
- ผลตรวจจากเครื่อง Windows (ผู้ใช้รันภายหลังการรวม): `dotnet build` ผ่าน; `dotnet test` ผ่าน 232/281 และล้มเหลว 49 รายการทั้งหมดจาก `LanguageDetectionTests` ที่ยังคาดหวังภาษาเกินขอบเขต GUI ไทย/อังกฤษ
- แก้ `LanguageDetectionTests.cs` ให้สอดคล้องกับข้อกำหนดของแอปปัจจุบัน: Thai → `th`; English → `en`; ภาษาอื่น/Invariant → fallback `en`. ไม่ได้เพิ่มภาษาหรือเปลี่ยน runtime behavior. ต้องรัน Build/Test ซ้ำบน Windows เพื่อยืนยันหลังแก้ tests

```powershell
dotnet build .\LuaToolsGui.sln -c Release -m:1
dotnet test .\LuaToolsGui.sln -c Release -m:1
```

- แก้ป้ายบนการ์ด Home จาก “Plugin Status” เป็น “Steam Status” ให้ตรงกับข้อมูล Steam ที่แสดงจริง ทั้งอังกฤษและไทย


## Fresh repository baseline
- Root `README.md`, `CONTRIBUTING.md`, and `LICENSE` files are intentionally omitted. The same filenames are ignored by `.gitignore`.
- Top Sellers Expanded behavior is retained; the release version is reset to `1.0.0` for the new repository baseline.
