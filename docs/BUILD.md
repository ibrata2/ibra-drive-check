# البناء والتطوير — IBRA DRIVE CHECK

## المتطلبات

- Windows 10/11 x64
- .NET SDK 10 (`dotnet --version` ≥ 10.0)
- NSIS 3 (لملف التثبيت فقط): `choco install nsis` أو من nsis.sourceforge.io

لا توجد أي حزم NuGet خارجية — المشروع يعتمد فقط على مكتبات .NET و Windows المدمجة
(WMI عبر COM، الوصول المباشر عبر `DeviceIoControl`، الإشعارات عبر `NotifyIcon`).

## البناء والتشغيل

```powershell
dotnet build IbraDriveCheck.sln -c Release
dotnet run --project src/IbraDriveCheck.App
```

الفحص السريع/العميق/المدمر وفحص القراءة تحتاج صلاحيات المسؤول (زر "إعادة التشغيل كمسؤول" داخل البرنامج).
اختبار السرعة وفحص المساحة الفارغة والصحة تعمل بدونها.

## الاختبارات

```powershell
# محاكاة: وحدات مزيفة (التفاف، مناطق وهمية، رفض كتابة)، أخطاء قراءة/كتابة، فصل الوحدة، إلغاء، إيقاف مؤقت، بطء، تقرير الشراء
dotnet run --project tests/IbraDriveCheck.Tests -c Release

# اختبارات ويندوز حقيقية على قرص افتراضي (تحتاج مسؤول + قرص VHD بحرف T:)
$env:IBRA_WIN_INTEGRATION = "1"; $env:IBRA_TEST_VOLUME = "T:"
dotnet run --project tests/IbraDriveCheck.Tests -c Release -- T_Win_
```

لإنشاء قرص افتراضي للاختبار (diskpart كمسؤول):

```
create vdisk file="C:\temp\ibra-test.vhdx" maximum=2048 type=expandable
select vdisk file="C:\temp\ibra-test.vhdx"
attach vdisk
create partition primary
format fs=exfat quick label=IBRATEST
assign letter=T
```

اختبار تشغيل الواجهة آلياً (كل الصفحات باللغتين والمظهرين):

```powershell
IbraDriveCheck.exe --smoke-test   # رمز خروج 0 = نجاح، السجل في %TEMP%\ibra-drive-check-smoke.log
```

## النشر

```powershell
# النسخة المحمولة: ملف واحد مستقل (لا يحتاج تثبيت .NET) — بدون UPX أو تمويه
dotnet publish src/IbraDriveCheck.App -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -o out/portable

# ملف التثبيت
makensis /DVERSION=1.0.0 /DSRC=out\portable\IbraDriveCheck.exe /DOUT=out\IBRA-DRIVE-CHECK-Setup-1.0.0.exe installer\ibra-drive-check.nsi
```

### البناء الآلي

`.github/workflows/build.yml` يعمل مع كل رفع على `main`:
بناء ← اختبارات المحاكاة ← إنشاء قرص افتراضي واختبارات ويندوز الحقيقية ← نشر النسخة المحمولة ← اختبار تشغيل الواجهة ←
ملف التثبيت ← فحص Windows Defender ← SHA-256 ← نشر الإصدار تلقائياً إذا كان رقم الإصدار (`<Version>` في
`src/IbraDriveCheck.App/IbraDriveCheck.App.csproj`) غير منشور سابقاً.

لإصدار جديد: غيّر `<Version>` (وأضف `releases/notes/<version>.md` اختيارياً) وارفع.

### التوقيع الرقمي

عند توفر شهادة Authenticode:

```powershell
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f cert.pfx /p <password> out\*.exe
```

## مكان البيانات

`%LOCALAPPDATA%\IBRA DRIVE CHECK\`
- `history\` سجل الفحوصات (JSON لكل فحص)
- `restore\` سجلات الاستعادة المؤقتة (تُحذف تلقائياً بعد نجاح إعادة البيانات)
- `settings.json` الإعدادات

السجلات بصيغة JSON بدل SQLite حتى يبقى البرنامج بدون أي اعتماد خارجي (أقل حجماً وأقل إنذارات من برامج الحماية).
