@echo off
rem Builds the BepInEx plugin RoR2Fishing.dll (needs Risk of Rain 2 in the default Steam folder).
rem   1. FishingHost.dll  = src\host\*.cs  + src\shared\*.cs   (every LOAD-FISHING.ps1 add-on, one class each)
rem   2. FishingGuest.dll = src\guest\*.cs + src\shared\*.cs   (every FISHING.exe 2.1 payload, one class each)
rem   3. RoR2Fishing.dll  = src\plugin\*.cs with 1 and 2 embedded as resources (one file to install)
rem Then refreshes package\plugins\RoR2Fishing.dll and the r2modman zip in dist\.
rem The sources in src\host and src\guest come from tools\sync-sources.ps1 (run it first after the originals change).
rem Offline check afterwards: test\run-harness.bat
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set M=C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2\Risk of Rain 2_Data\Managed
cd /d "%~dp0"
if not exist obj mkdir obj
set OPTS=-nologo -noconfig -nostdlib -target:library -optimize+ -codepage:65001 -nowarn:0162,0168,0169,0219,0414,0649,0618,0429
set REFS=-r:"%M%\mscorlib.dll" -r:"%M%\System.dll" -r:"%M%\System.Core.dll" -r:"%M%\netstandard.dll" -r:"%M%\UnityEngine.dll" -r:"%M%\UnityEngine.CoreModule.dll" -r:"%M%\UnityEngine.IMGUIModule.dll" -r:"%M%\UnityEngine.TextRenderingModule.dll" -r:"%M%\UnityEngine.ParticleSystemModule.dll" -r:"%M%\UnityEngine.PhysicsModule.dll" -r:"%M%\UnityEngine.ImageConversionModule.dll" -r:"%M%\UnityEngine.AnimationModule.dll" -r:"%M%\UnityEngine.AudioModule.dll" -r:"%M%\UnityEngine.UIModule.dll" -r:"%M%\UnityEngine.UI.dll" -r:"%M%\UnityEngine.InputLegacyModule.dll" -r:"%M%\UnityEngine.ScreenCaptureModule.dll" -r:"%M%\RoR2.dll" -r:"%M%\LegacyResourcesAPI.dll" -r:"%M%\com.unity.multiplayer-hlapi.Runtime.dll" -r:"%M%\Unity.Addressables.dll" -r:"%M%\Unity.ResourceManager.dll" -r:"%M%\HGCSharpUtils.dll" -r:"%M%\KinematicCharacterController.dll" -r:"%M%\Facepunch.Steamworks.dll" -r:"%M%\Zio.dll" -r:"%M%\Rewired_Core.dll" -r:"%M%\HGUnityUtils.dll" -r:"%M%\AK.Wwise.Unity.API.dll" -r:"%M%\AsyncManagement.dll"

"%CSC%" %OPTS% %REFS% -out:obj\FishingHost.dll src\host\*.cs src\shared\*.cs || goto :fail
"%CSC%" %OPTS% %REFS% -out:obj\FishingGuest.dll src\guest\*.cs src\shared\*.cs || goto :fail
"%CSC%" %OPTS% %REFS% -r:lib\BepInEx.dll -resource:obj\FishingHost.dll,FishingHost.dll -resource:obj\FishingGuest.dll,FishingGuest.dll -out:RoR2Fishing.dll src\plugin\*.cs || goto :fail

if not exist package\plugins mkdir package\plugins
copy /y RoR2Fishing.dll package\plugins\RoR2Fishing.dll >nul
powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-zip.ps1 || goto :fail
echo Built RoR2Fishing.dll (+ package\plugins\RoR2Fishing.dll and the zip in dist\)
exit /b 0
:fail
echo BUILD FAILED
exit /b 1
