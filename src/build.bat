@echo off
rem Rebuilds FISHING.exe from source (needs the game installed in the default Steam folder).
rem The payload DLLs get a per-release assembly name (e.g. FishingClient_v20): Mono reuses an already loaded
rem assembly with the same name, so a new name is what lets a newer FISHING.exe replace an older copy in a running game.
set V=v20
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set M=C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2\Risk of Rain 2_Data\Managed
set REFS=-r:"%M%\mscorlib.dll" -r:"%M%\System.dll" -r:"%M%\System.Core.dll" -r:"%M%\netstandard.dll" -r:"%M%\UnityEngine.CoreModule.dll" -r:"%M%\UnityEngine.IMGUIModule.dll" -r:"%M%\UnityEngine.TextRenderingModule.dll"
cd /d "%~dp0"
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -r:"%M%\UnityEngine.ParticleSystemModule.dll" -out:FishingClient_%V%.dll FishingClient.cs WaterLook.cs HotspotLook.cs NativeWaterLook.cs MercSwingLook.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:ReqPanelClient_%V%.dll ReqPanelClient.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library -define:FISHFX_CLIENT %REFS% -out:FishFxClient_%V%.dll FishFx.cs FishArt.cs FishSchool.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:NoWeaponsClient_%V%.dll NoWeapons.cs NoWeaponsIconPixels.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:FishItemsClient_%V%.dll FishItems.cs FishArt.cs || exit /b 1
"%CSC%" -nologo -platform:x64 -target:exe -resource:FishingClient_%V%.dll,FishingClient.dll -resource:ReqPanelClient_%V%.dll,ReqPanelClient.dll -resource:FishFxClient_%V%.dll,FishFxClient.dll -resource:NoWeaponsClient_%V%.dll,NoWeaponsClient.dll -resource:FishItemsClient_%V%.dll,FishItemsClient.dll -out:FISHING.exe FishingLauncher.cs || exit /b 1
del FishingClient_%V%.dll ReqPanelClient_%V%.dll FishFxClient_%V%.dll NoWeaponsClient_%V%.dll FishItemsClient_%V%.dll
echo Built FISHING.exe
