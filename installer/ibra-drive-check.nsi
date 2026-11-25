; IBRA DRIVE CHECK — مثبت ويندوز (NSIS)
; makensis /DVERSION=1.0.0 /DSRC=path\to\IbraDriveCheck.exe /DOUT=path\to\Setup.exe installer\ibra-drive-check.nsi
Unicode true
ManifestDPIAware true
SetCompressor /SOLID lzma

!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef SRC
  !define SRC "..\out\portable\IbraDriveCheck.exe"
!endif
!ifndef OUT
  !define OUT "..\out\IBRA-DRIVE-CHECK-Setup-${VERSION}.exe"
!endif

!define APPNAME "IBRA DRIVE CHECK"
!define EXENAME "IBRA DRIVE CHECK.exe"
!define PUBLISHER "Ibrahim Al-Khazraji (IBRAPROGRAM)"
!define WEBSITE "https://ibraprogram.netlify.app/"
!define UNINSTKEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\IbraDriveCheck"

Name "${APPNAME}"
OutFile "${OUT}"
InstallDir "$PROGRAMFILES64\${APPNAME}"
InstallDirRegKey HKLM "Software\IbraDriveCheck" "InstallDir"
RequestExecutionLevel admin
BrandingText "${APPNAME} ${VERSION} — ibraprogram.netlify.app"

VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${APPNAME}"
VIAddVersionKey "FileDescription" "${APPNAME} Setup"
VIAddVersionKey "CompanyName" "IBRAPROGRAM"
VIAddVersionKey "LegalCopyright" "Ibrahim Al-Khazraji"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"

!include "MUI2.nsh"
!include "x64.nsh"

!define MUI_ICON "..\assets\icon.ico"
!define MUI_UNICON "..\assets\icon.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\${EXENAME}"
!define MUI_FINISHPAGE_LINK "ibraprogram.netlify.app"
!define MUI_FINISHPAGE_LINK_LOCATION "${WEBSITE}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Arabic"
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "IBRA DRIVE CHECK requires 64-bit Windows 10/11."
    Abort
  ${EndIf}
  SetRegView 64
  !insertmacro MUI_LANGDLL_DISPLAY
FunctionEnd

Section "Install"
  SetOutPath "$INSTDIR"
  ; نغلق أي نسخة شغالة قبل الاستبدال
  nsExec::Exec 'taskkill /IM "${EXENAME}" /F'
  File "/oname=${EXENAME}" "${SRC}"
  File "/oname=icon.ico" "..\assets\icon.ico"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APPNAME}"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\icon.ico"
  CreateShortcut "$SMPROGRAMS\${APPNAME}\Uninstall.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APPNAME}.lnk" "$INSTDIR\${EXENAME}" "" "$INSTDIR\icon.ico"

  WriteRegStr HKLM "Software\IbraDriveCheck" "InstallDir" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayName" "${APPNAME}"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKLM "${UNINSTKEY}" "Publisher" "${PUBLISHER}"
  WriteRegStr HKLM "${UNINSTKEY}" "URLInfoAbout" "${WEBSITE}"
  WriteRegStr HKLM "${UNINSTKEY}" "DisplayIcon" "$INSTDIR\icon.ico"
  WriteRegStr HKLM "${UNINSTKEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTKEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTKEY}" "NoRepair" 1
SectionEnd

Section "Uninstall"
  SetRegView 64
  nsExec::Exec 'taskkill /IM "${EXENAME}" /F'
  Delete "$INSTDIR\${EXENAME}"
  Delete "$INSTDIR\icon.ico"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\${APPNAME}\${APPNAME}.lnk"
  Delete "$SMPROGRAMS\${APPNAME}\Uninstall.lnk"
  RMDir "$SMPROGRAMS\${APPNAME}"
  Delete "$DESKTOP\${APPNAME}.lnk"
  DeleteRegKey HKLM "${UNINSTKEY}"
  DeleteRegKey HKLM "Software\IbraDriveCheck"
  ; سجلات الفحص في %LOCALAPPDATA%\IBRA DRIVE CHECK تبقى للمستخدم ولا تُحذف تلقائياً
SectionEnd
