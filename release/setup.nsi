; The one-file Windows installer for Smart Study Distraction Blocker.
; Built by build-setup.ps1, which passes VERSION, STAGE (the folder to bundle),
; OUTFILE and ICON. Everything it installs, including the .NET runtime the
; programs need, is inside this one file.
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"

!define PRODUCT "Smart Study Distraction Blocker"
Name "${PRODUCT}"
BrandingText "${PRODUCT} ${VERSION}"
OutFile "${OUTFILE}"
Icon "${ICON}"
RequestExecutionLevel admin
SetCompressor /SOLID lzma
InstallDir "$TEMP\SmartStudyBlocker-setup"
ShowInstDetails show
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "${PRODUCT}"
VIAddVersionKey "FileDescription" "${PRODUCT} setup"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"

!define MUI_ICON "${ICON}"
!define MUI_WELCOMEPAGE_TITLE "Welcome to ${PRODUCT}"
!define MUI_WELCOMEPAGE_TEXT "This sets up ${PRODUCT} on your PC. It blocks the distracting sites you choose and keeps YouTube focused on studying.$\r$\n$\r$\nWindows may ask for permission once. Click Next, then wait a minute or two."
!define MUI_FINISHPAGE_TITLE "You're all set"
!define MUI_FINISHPAGE_TEXT "${PRODUCT} is installed and running. It opens from the Start menu or the desktop icon.$\r$\n$\r$\nOne last step: close and reopen your browsers (Chrome, Edge or Brave). The extension appears by itself after that."
!define MUI_FINISHPAGE_NOAUTOCLOSE

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "English"

; Everything it installs is 64-bit.
Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "${PRODUCT} needs 64-bit Windows."
    Abort
  ${EndIf}
FunctionEnd

Section "Install"
  SetOutPath "$INSTDIR\payload"
  File /r "${STAGE}\*.*"
  DetailPrint "Installing ${PRODUCT} (this takes a minute)..."
  ; This setup program is 32-bit, and Windows gives a 32-bit program the 32-bit
  ; PowerShell, which takes Program Files (x86) for Program Files and writes to
  ; the 32-bit half of the registry: the service then never finds the folder it
  ; leaves LockIn's pairing offer in. Sysnative is the way to the 64-bit one.
  nsExec::ExecToLog '"$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "$INSTDIR\payload\installer\install.ps1" -PrebuiltDir "$INSTDIR\payload" -Quiet'
  Pop $0
  SetOutPath "$TEMP"
  RMDir /r "$INSTDIR"
  StrCmp $0 "0" done
    MessageBox MB_ICONSTOP "The installation did not finish (code $0). Nothing was left half-installed that you need to clean up.$\r$\n$\r$\nThe details are in $TEMP\SmartStudyBlocker-install.log"
    Abort
  done:
SectionEnd
