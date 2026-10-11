# Fishing mod: install (r2modman)

> **3.0.0 is an UNTESTED pre-release.** It has not been played in a real game yet.

Everybody in the lobby needs the mod, **the same version (3.0.0)**. You no longer need FISHING.exe.

> **Never use FISHING.exe and this mod in the same game.** If you started FISHING.exe earlier, restart the game
> through r2modman before joining. (The mod notices injected FISHING.exe copies and switches itself off.)

## 1. Install r2modman (one time)
1. Download r2modman from https://thunderstore.io/package/ebkr/r2modman/ and install it.
2. Open it, pick **Risk of Rain 2**, and create a new profile called **Fishing**. A separate profile keeps your
   other mods out of the fishing lobby.

## 2. Add the fishing mod
**Until it's on Thunderstore (zip from JV94):**
1. In the Fishing profile, open the **Online** tab, search **BepInExPack** (by bbepis) and click Download.
2. Go to **Settings > Profile > Import local mod**, pick `JV94-RoR2Fishing-3.0.0.zip`. If it asks for an
   author, type `JV94`.
3. Had 2.0.0 or 2.1.0 before? Import the 3.0.0 zip the same way (it replaces the old one), or uninstall the old one first.

**Once it's on Thunderstore:** Online tab, search **RoR2Fishing**, Download. BepInExPack comes with it.

## 3. Play
1. Click **Start modded** (not the normal Steam Play button).
2. Join JV94's lobby. If you can't see it, your version doesn't match: update the mod and try again.

| Action | Control |
|---|---|
| Cast, then reel in when it bites | Left click (or F) |
| Rod Whip (1 damage + snaps their line) | Right click |
| Inventory | I (or `!inv`) |
| Compass / Range Finder (find the invisible hotspots) | hold Shift / hold R |
| Flashlight | L |
| Swim up / dive | Jump / X |
| Guide book | G (or E at the rod shop) |
| Rod shop / fish merchant | walk up to it, number keys pick |
| Vote for the next feature | `!vote 1` / `2` / `3` |
| Drone pet, your aquarium | `!pet`, `!house` / `!visit <name>` / `!back` |
| Leaderboard / your fish / time left | `!scores` / `!collection` / `!tournamentstatus` |

Fall in the lake and you swim; run out of breath and you drown, then come back on dry land.

## Something wrong?
- Send JV94 these files: `%TEMP%\ror2_fishing_client.log` and, in r2modman, *Settings > Debugging > Copy
  LogOutput contents*.
- Lag? Type `!prof` in chat: it measures for 8 s and writes `%TEMP%\ror2_prof.log`. Send that too.
- If you hosted a game and then joined someone else's, restart the game.
