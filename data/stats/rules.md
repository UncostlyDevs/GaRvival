# Stat rules

General rules behind the numbers in this folder. `client:` means the decompiled game client
(Assembly-CSharp / JunkWars), which is the 2017 build.

## Files

- `parts.csv`: every part template in the catalog (190). `subtype` is `key=value;...`
  (chassis `kind`/`terrain`, body `arms`/`batteries`, mechanism `barrels`/`mod`,
  barrel `role`/`feed`/`projectiles`, battery `quality`/`bias`, engine `tradeoff`).
- `observations.csv`: one known number per row. `partId` is a template id or a class
  selector (`class:Body/Heavy/4arm`); blank when the source names no mappable part
  (`target` keeps the source's wording). `value`..`valueMax` is a range when both are set.
  `mechanism` is the firing mechanism a barrel number was measured on.
  `reliability`: official (developer, client, patch notes), guide, player.
- `patches.csv`: dated balance changes. `kind`: multiplier, additive, set, direction
  (no figure given), rule, unspecified.
- `economy.csv`: prices, rewards, odds and limits.

## Tiers and upgrades

- Parts upgrade from tier 0 to 10; new purchases start at tier 0.
  https://www.moddb.com/games/guns-and-robots/news/welcome-to-the-new-update-information (2014-06-09);
  `client:PartTemplate.maxTier = 10`.
- Upgrade levels are restricted by player level (dev diary, 2014-01-23):
  https://web.archive.org/web/20140927233536id_/http://forum.play-gar.com:80/index.php?/topic/929-devs-talk-about-item-upgrades/
- The store sells only tier 0; tiers do not move between parts:
  https://steamcommunity.com/app/293540/discussions/0/34093781853729242/
- Upgrades raise durability of cases and chassis; barrel upgrades raise damage and durability;
  weapon-case upgrades are durability only (players, 2014):
  https://steamcommunity.com/app/293540/discussions/0/34095684687596607/
- Devices top out at level 3; level 2 to 3 takes 3 days (2017 guide):
  https://steamcommunity.com/sharedfiles/filedetails/?id=1105439577
- Upgrading costs credits plus components (Actuators chassis, Armor Plates body, Armaments weapon,
  CPU Cores head, Onboard Integration Modules device): https://store.steampowered.com/api/appdetails?appids=331890

## Garage display (client:GameplayDataExtactTools)

- Durability is shown rounded up (`CeilToInt`).
- Rate of fire is shown as `1 / delay` rounded to 0.1 shots/s; accuracy as a percent of a value
  rounded to 0.01; weight limit rounded to 0.1.
- Speed is stored in m/s and shown as km/h (x3.6, rounded to 0.1).
- Mechanism rate bonus is shown as `floor((1/fireDelay - 1) * 100)%`; damage modifier rounded to 0.01.
- Oxidian (Acidcaster) splash is shown as 2x the per-tick value, per second.
- Plumber and Sandblaster show damage as `D x 5`, Hailfire as `D x 4`.
- Each stat bar has a fixed maximum per part type (chassis durability 1200, body 1122, barrel damage 96,
  rate of fire 5/s...); they are in observations.csv as `*BarMax` rows.

## Combat formulas (client)

- Barrel damage = barrel damage x ammo modifier x mechanism damage modifier x multi-barrel coefficient
  (`client:WeaponBarrel.GetDamage`). Splash uses the mechanism splash modifier, which also scales splash range.
- Multi-barrel coefficient: 2 barrels 0.625 each, 3 barrels 0.5 each (`client:CommonConstants`);
  descriptions say 63% / 50%. Dev: Triceratops does 50% more than one barrel:
  https://steamcommunity.com/app/293540/discussions/0/34095051776343907/?ctp=2
- Accuracy = barrel accuracy x mechanism accuracy modifier + guild buff, capped at 1.
- Fire delay = barrel delay x mechanism fireDelay; for bots also x max(5 / robotRank, 1)
  (`client:WeaponBarrel.GetRateOfFire`), so bots below rank 5 fire slower.
- Weight over the chassis limit costs 0.05 km/h per unit; speed never drops below 7 km/h
  (`client:GameplayDataExtactTools.SpeedToSpeed`, `GlobalConstants.minSpeed`). Weight under the limit
  has no effect. Sprint costs 35 energy and doubles speed; the flag carrier loses 3.333 km/h.
- Railgun shots pass walls; limited to some surfaces from 2014-08-28, damage halved per obstacle from
  2015-03-26: https://steamcommunity.com/app/293540/discussions/0/611701999520303086/

## Builds

- A weapon is a firing mechanism plus barrel(s); light, medium and heavy parts fit only their class
  (official guide, 2013): https://web.archive.org/web/20140810051006id_/http://forum.play-gar.com:80/index.php?/topic/74-get-started/
- Battery capacity adds up across batteries; regeneration is per second; higher capacity means lower regen.
  2-arm bodies take 1 battery, 4-arm bodies 2; every body has 2 device slots: https://gar.fandom.com/wiki/Battery
- Four-weapon bodies must fill all four slots to battle (2015-04-09):
  https://steamcommunity.com/app/293540/discussions/0/611702631239414711/
- Energy weapons use no ammo and have no resupply cost; ammo weapons pay resupply:
  https://web.archive.org/web/20160821195212id_/http://forum.play-gar.com/index.php?/topic/4011-incentives-for-leveling-up/
- All heads have the same stats (review, 2015-05-11): https://steamcommunity.com/app/293540/reviews/
- Only body destruction kills; a destroyed chassis immobilises, a destroyed head scrambles controls:
  https://steamcommunity.com/app/293540/discussions/0/611702631212905784/

## Economy and rank

- All batteries and engines are free from 2014-10-02:
  https://web.archive.org/web/20160821182852id_/http://forum.play-gar.com/index.php?/topic/1364-game-update-notes-02-october-2014/
- Credits are earned per point of damage dealt: https://gar.fandom.com/wiki/Credits
- Robot rank is the sum of the parts' tier points; weapons and devices count more from 2015-03-26;
  rank 1 robots only meet rank 1: https://steamcommunity.com/app/293540/discussions/0/610574394236393051/
- Ranks 1-3 and 4-10 were separate matchmaking groups in 2013:
  https://web.archive.org/web/20130306042838id_/http://forum.play-gar.com:80/index.php?/topic/105-robot-ranks-and-what-they-depend-on/

## Derived from this dataset (not stated by any source)

- Garage durability at tier 0 looks like the 2017 guide value x2. Player tier-0 values from 2014, raised by
  the 2015-03-26 +10% body buff, match exactly: Mantis 595 x 1.1 = 654.5 = 327.25 x 2;
  Panzersturm 261 x 1.1 = 287.1 = 143.5 x 2. `RealStats.cs` stores guide / 0.35 (x2.857) instead.
- Tier 10 looks like tier 0 x 12/7 (+1/14 of tier 0 per tier): Mantis 595 -> 1020 (player T10 heavy 2-arm);
  Warhawk 350 x 2 x 12/7 = 1200 and Mantis 654.5 x 12/7 = 1122 (the bar maxima);
  Rocket Launcher splash 15.68 x 12/7 = 26.88 (barrel splash bar maximum). Mid tiers fit:
  Panzersturm T6 375, Ghast T7 238. Behemoth T6 726 is 3% above the fit.
- Cannon over Ballistic is x1.15 damage: 28.1 / 24.5 (Rocket Launcher) and 28.27 / 24.5 (Fragunov)
  in the 2014 DPS figures.
