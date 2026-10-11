@echo off
rem Rebuilds FISHING.exe from source (needs the game installed in the default Steam folder).
rem The payload DLLs get a per-release assembly name (e.g. FishingClient_v20): Mono reuses an already loaded
rem assembly with the same name, so a new name is what lets a newer FISHING.exe replace an older copy in a running game.
set V=v30
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set M=C:\Program Files (x86)\Steam\steamapps\common\Risk of Rain 2\Risk of Rain 2_Data\Managed
set REFS=-r:"%M%\mscorlib.dll" -r:"%M%\System.dll" -r:"%M%\System.Core.dll" -r:"%M%\netstandard.dll" -r:"%M%\UnityEngine.CoreModule.dll" -r:"%M%\UnityEngine.IMGUIModule.dll" -r:"%M%\UnityEngine.TextRenderingModule.dll"
cd /d "%~dp0"
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -r:"%M%\UnityEngine.ParticleSystemModule.dll" -r:"%M%\UnityEngine.InputLegacyModule.dll" -r:"%M%\RoR2.dll" -r:"%M%\com.unity.multiplayer-hlapi.Runtime.dll" -r:"%M%\KinematicCharacterController.dll" -r:"%M%\HGCSharpUtils.dll" -r:"%M%\HGUnityUtils.dll" -out:FishingClient_%V%.dll FishingClient.cs WaterLook.cs HotspotLook.cs NativeWaterLook.cs MercSwingLook.cs RodSkinLook.cs RodHandAttach.cs UnderwaterLook.cs CastAnimLook.cs FishingSwimLook.cs GlowBobberLook.cs RodTypeLook.cs GameplayClient.cs GameplayFx.cs GoldLook.cs DayNightLook.cs FishingFlashlight.cs FishingPetLines.cs FishingZoneCues.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:ReqPanelClient_%V%.dll ReqPanelClient.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library -define:FISHFX_CLIENT %REFS% -out:FishFxClient_%V%.dll FishFx.cs Fish3D.cs FishArt.cs FishSchool.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:NoWeaponsClient_%V%.dll NoWeapons.cs NoWeaponsIconPixels.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:FishItemsClient_%V%.dll FishItems.cs FishArt.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -r:"%M%\UnityEngine.PhysicsModule.dll" -r:"%M%\UnityEngine.UIModule.dll" -out:CompassClient_%V%.dll CompassClient.cs CompassLook.cs GadgetLook.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:VoteClient_%V%.dll VoteClient.cs FishingKeys.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:InventoryClient_%V%.dll InventoryClient.cs InvLook.cs FishingKeys.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -r:"%M%\UnityEngine.PhysicsModule.dll" -r:"%M%\UnityEngine.ImageConversionModule.dll" -out:ShopStandClient_%V%.dll ShopStandClient.cs RodStandLook.cs StandSkinLook.cs GlowBobberLook.cs RodTypeLook.cs TimeHudLook.cs GuideBookLook.cs GuideData.cs GuideSketch.cs FishingKeys.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library %REFS% -out:MerchantClient_%V%.dll MerchantClient.cs CaseData.cs CaseSkinLook.cs MerchantLook.cs MerchantUI.cs FishingKeys.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library -define:ISLAND_CLIENT %REFS% -r:"%M%\UnityEngine.PhysicsModule.dll" -out:IslandClient_%V%.dll Island.cs IslandLook.cs || exit /b 1
"%CSC%" -nologo -noconfig -nostdlib -target:library -define:AQUA_CLIENT -define:HAVE_FISH3D %REFS% -r:"%M%\UnityEngine.PhysicsModule.dll" -out:AquariumClient_%V%.dll Aquarium.cs Fish3D.cs FishArt.cs || exit /b 1
"%CSC%" -nologo -platform:x64 -target:exe -resource:FishingClient_%V%.dll,FishingClient.dll -resource:ReqPanelClient_%V%.dll,ReqPanelClient.dll -resource:FishFxClient_%V%.dll,FishFxClient.dll -resource:NoWeaponsClient_%V%.dll,NoWeaponsClient.dll -resource:FishItemsClient_%V%.dll,FishItemsClient.dll -resource:CompassClient_%V%.dll,CompassClient.dll -resource:VoteClient_%V%.dll,VoteClient.dll -resource:InventoryClient_%V%.dll,InventoryClient.dll -resource:ShopStandClient_%V%.dll,ShopStandClient.dll -resource:MerchantClient_%V%.dll,MerchantClient.dll -resource:IslandClient_%V%.dll,IslandClient.dll -resource:AquariumClient_%V%.dll,AquariumClient.dll -out:FISHING.exe FishingLauncher.cs || exit /b 1
del FishingClient_%V%.dll ReqPanelClient_%V%.dll FishFxClient_%V%.dll NoWeaponsClient_%V%.dll FishItemsClient_%V%.dll CompassClient_%V%.dll VoteClient_%V%.dll InventoryClient_%V%.dll ShopStandClient_%V%.dll MerchantClient_%V%.dll IslandClient_%V%.dll AquariumClient_%V%.dll
echo Built FISHING.exe
