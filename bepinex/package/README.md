# RoR2 Fishing

> **PRE-RELEASE 3.0.0 - UNTESTED.** This version has not been played in a real game yet (only offline checks).
> Expect bugs. Everyone in the lobby must run exactly 3.0.0; 2.x players cannot join a 3.0.0 lobby.
> Do not run FISHING.exe in the same game as this mod.

Turns a Risk of Rain 2 run into a relaxed co-op **fishing game**. Everybody holds a fishing rod instead of a
weapon, a lake covers the low ground of every stage, and the players compete in a fishing tournament.

**Everyone in the lobby needs this mod (same version).** The game hides lobbies whose mod list doesn't match.
The host runs the game rules; every player's own game draws the rods, fish, water and menus.

## How to play
| Action | Control |
|---|---|
| Cast / reel in when it bites | **Left click** (or **F**) |
| Rod Whip: 1 damage to the player in front of you and snaps their line back | **Right click** |
| Inventory (equip rods, bobbers, gadgets) | **I** or `!inv` |
| Compass / Range Finder gadgets (hotspots are invisible, find them with these) | hold **Shift** / hold **R** |
| Flashlight | **L** |
| Swim up / dive | **Jump** / **X** (watch the breath bar) |
| Guide book | **G**, `!guide`, or **E** at the rod shop |
| Rod shop and fish merchant (sell fish, open cases) | walk up to the stall / merchant, number keys pick |
| Feature vote | `!vote 1`, `!vote 2`, `!vote 3` (or the number keys while the card is up) |
| Drone pet that fishes with you | `!pet` |
| Your aquarium | `!house`, `!visit <name>`, `!back` |
| Leaderboard / collection / time left | `!scores`, `!collection`, `!tournamentstatus` |

Host-only: `!endtournament`, `!island` / `!island leave` (tropical island trip), `!time <hour>`, `!daylength <min>`,
`!poll`, `!sandbox` (test sandbox).

- Aim higher to cast further. Only water counts; a cast onto the ground bounces back.
- Fishing zones, weather, a day/night clock, golden hotspots and small pools change what bites.
- Fish go in your item bar and give small movement perks. Sell them to the merchant for money.
- Fall in and you swim; run out of breath and you drown (you come back on dry land).
- A ring of Lunar Seer podiums appears on every stage. Use one to pick the next map.

## What the host's game changes
This is a game mode, not a small tweak. While a run is hosted with this mod:
- **No enemies** (peaceful mode).
- **No weapons**: M1 is the fishing rod, M2 the Rod Whip, the other skills are off.
- **No normal items**: chests, shops, printers, shrines and item drops are removed; fish are the only items.
- **Tournament**: starts about 5 s into each run and lasts 10 minutes (configurable).
  By default the winner takes **all the other connected players' lunar coins** (real, saved coins).
  The host can turn this off in the config (`WinnerTakesLunarCoins = false`).

## Settings and files
`BepInEx\config\com.jv94.fishing.cfg` (created on first launch; r2modman: *Config editor*). Only the host's
settings matter for the rules: peaceful mode, map picker, drown respawn, cast range, Rod Whip, tournament,
feature polls and two optional pranks. Saved progress (rods owned, case items, inventory) is kept in
`BepInEx\config\JV94.Fishing\` on the host's PC.

## Notes
- Logs: the BepInEx log, plus `%TEMP%\ror2_fishing.log` (host) and `%TEMP%\ror2_fishing_client.log` (guests).
- If you host and then join someone else's game in the same session, restart the game first.
- Built for Risk of Rain 2 with Seekers of the Storm. Mods that add enemies, items or skills will clash with
  the fishing rules.
