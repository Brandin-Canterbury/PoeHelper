# PoeHelper

A small Windows overlay for **Path of Exile 1** (league: Covens, 3.30). Hover an item in game and press a hotkey:

| Hotkey | Feature | Status |
|---|---|---|
| **Ctrl+D** | **Price check**: finds similar listings, then gives a recommended price with reasons | ✅ working |
| **Alt+D** | **Gear compare**: what changes if this item replaced yours (life, resists vs cap, attributes vs gem requirements, your selected skill's damage, energy shield) | ✅ working |
| button on compare | **Find upgrade**: Instant Buyout search for a better item within your budget, keeping resists capped and gem requirements met | ✅ working |

An always-on **☰ button** (right edge of the screen) opens the menu: Settings, Reload character, Refresh trade data, Open log, Reset button position, Quit. **Alt+drag** moves it.

## How it stays within GGG's rules

Like Awakened PoE Trade, each keypress does one thing:
1. Sends a single Ctrl+C (or Ctrl+Alt+C) to the game.
2. Reads the clipboard, then restores what was there before.
3. Makes one trade search plus up to two fetches of 10 listings. Find upgrade does the same, once per button click.

It never reads game memory and never sends any other input. Requests follow the trade site's `X-Rate-Limit-*` headers, and the User-Agent names this tool.

## Setup

### Option A: build from source
1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Build and publish:
   ```powershell
   git clone https://github.com/Brandin-Canterbury/PoeHelper.git
   cd PoeHelper
   ./publish.ps1          # writes publish\PoeHelper.exe (self-contained, no runtime needed)
   ```
3. Run `publish\PoeHelper.exe`. The ☰ button appears on the right edge of the screen, and a gold **P** appears in the tray.

### Option B: a friend's copy
Copy the `publish` folder (it only needs `PoeHelper.exe`) and run it. Each player enters their own account in Settings; nothing in it is tied to one person.

### In game
- Set PoE to **Windowed** or **Windowed Fullscreen**. An overlay can't draw over exclusive fullscreen.
- Hover an item and press **Ctrl+D**. The panel opens beside the item. **Esc**, the ✕ button, or pressing Ctrl+D over empty space closes it.
- Hotkeys only fire while PoE is the active window, so Ctrl+D still works normally in other apps.

### Settings (☰ button → Settings, or the tray icon)
| Setting | Notes |
|---|---|
| Account name | Include the discriminator, e.g. `Name#1234` (shown on your pathofexile.com profile). Needed for compare and upgrade. |
| Character name | The character to compare against. |
| League | Defaults to `Covens`. |
| Listings | **Instant Buyout only** (default), Instant Buyout + in person, or any online seller. |
| Skill to upgrade | A dropdown of your character's active gems; whose damage counts as "damage". **Auto** picks the gem with the most linked supports. |
| Upgrade tracker | Tick the stats Find upgrade looks for after resists: skill damage, life, energy shield, mana, regen, movement speed, attributes, attack/cast speed, crit, spell damage, DoT multiplier, minion damage, gem levels. Default: life and skill damage. |
| Advanced copy | Uses Ctrl+Alt+C so mod tiers are known. Rares then search on their best (T1–T2) mods, not every line. |
| Contact | Optional email or forum name added to the User-Agent, as GGG asks of tool authors. |

To use compare, your profile's **Characters** tab must be public: pathofexile.com → My Account → Privacy Settings.

## Gear compare (Alt+D)

Hover any weapon, armour piece, ring, amulet or belt and press **Alt+D**. The panel shows your stats now and with this item:
- **Life**: from your level, gear, tree (including anoints and masteries), jewels and Strength.
- **Resists**: after the −60 act penalty and aura gems like Purity of Elements, and including the tree's "fire resistance also applies to cold and lightning" conversion. They're compared against your real caps, with any over-cap shown as a buffer.
- **Attributes**: compared with the highest requirement among your socketed gems and gear.
- **Energy shield**: armour pieces' ES plus global flat ES, × global increases and Intelligence.
- **Skill damage**: a relative estimate for the selected skill, not a PoB DPS number (see below).

### Main skill and damage

Pick the skill in **Settings → Skill to upgrade**. On **Auto** it's the active gem with the most supports linked to it, skipping auras, heralds, curses, marks, warcries, guards, golems, banners and blink/travel skills. The compare panel always says which skill it scored and how.

Damage comes from **Path of Building's gem data** (bundled; see Credits), worked out the way Path of Building does it:

| | How it's counted |
|---|---|
| **Hits** | per damage type: (the gem's base damage at its level, or for attacks your weapon × the gem's damage multiplier) + flat added damage × the gem's effectiveness, × increased × more, × casts or attacks per second (the gem's cast time, the weapon's speed and the gem's attack-rate bonus) × crit |
| **Damage over time** | the gem's base damage per second × increased × more × (1 + DoT multiplier) |
| **"More"** | from the gem itself and every support linked to it (e.g. Elemental Focus 34% more elemental damage at level 20), with each modifier's flags respected |
| **Gem level** | your gem's level plus socketed and global +levels, looked up in the level table |
| **Quality** | gem quality bonuses from the data |
| **Minions** | the gem sets the minion's level (Raise Zombie 20 → level 70 zombies) and how many there are. Each minion hits from Path of Building's ally damage table × its own damage multiplier, with its own attack or spell. Your linked supports apply to it, and so does every "Minions …" stat (gear, tree, Minion Damage Support). The result is × the number of minions. |

Path of Building's rules come through too. For example, a spell's own damage over time isn't spell damage: "increased Spell Damage" and Controlled Destruction don't boost Righteous Fire's burn. Skills missing from the data use a simpler tag-based model, as do spectres (which spectre you raised isn't known). For gems that summon several kinds of minion (Summon Skeletons), the first kind is scored, as Path of Building does by default.

**Skill-specific formulas.** A few skills have their own code in Path of Building. They're ported one by one, and each switches on only when that skill is the one being scored (picked in Settings, or Auto's pick). Settings says when one is active:

| Skill | Formula |
|---|---|
| **Righteous Fire** | base burn = 70% of life + 70% of energy shield per second. RF's burn on you (reduced by fire res) is compared with your regen: a "Righteous Fire net regen /s" row, and **Don't swap** if an item tips you into net degen. The variants (Arcane Devotion, Vaal) aren't ported yet. |

When "skill damage" is tracked, Find upgrade's damage weights come from the same model, so a minion build is shown minion stats, a bow build attack stats, and so on.

The verdict is one line: Upgrade, Downgrade, Sidegrade or **Don't swap**. "Don't swap" means the item breaks something:
- your main skill's links, or gem sockets;
- an elemental resist falling under cap;
- an attribute requirement no longer met;
- a skill-specific check failing (e.g. Righteous Fire out-burning your regen).

Rings show a tab for each ring slot.

What's **not** modelled: conditional mods ("while on Low Life", flasks, charges, "against bleeding enemies"), damage conversion, ailments from hits, enemy resistances, minions' damage conversion (e.g. Raging Spirits' fire), "+1 to maximum number of …" minion gear, and Path of Building's per-skill code beyond the formulas above. The damage number is for comparing gear, not a full Path of Building DPS. Gear is fetched from your public profile when the app starts. After changing gear, click **Reload character**.

## Find upgrade (button on the compare panel)

Set a budget (div or c), tick **Include chaos res** if chaos resistance should count, then click **Find upgrade**:
1. **Required.** It works out what the slot must keep providing: the resists that would fall under cap without it (chaos too, when ticked and capped), the attributes your gems need, and the same socket count and links if gems sit in it.
2. **Searched.** It searches **Instant Buyout only** within your budget for your tracked stats (Settings → Upgrade tracker), sorted best-first.
3. **Checked.** Every result is re-checked against your full build. Anything that breaks resists, sockets or requirements is dropped, and so is anything that gains nothing you track.
4. **Ranked.** Resists come first: the fewer points under cap, the higher the item. Then come the tracked stats, each counted in "good rolls" (about 70 life, 10% skill damage, 25% movement speed…) so different stats add up fairly. An item that fixes a resist may cost at most one good roll of your tracked stats.

Resists that stay capped aren't shown. A resist row appears only when the item would leave it under cap, and chaos only when **Include chaos res** is ticked. Click a result to see every change it makes and every mod you'd give up.

**Open on trade site** opens the search so you can buy.

**Logged in** (see below): the search uses one weighted stat group made of your tracked stats (chaos res first when it counts and isn't capped). It's sorted by weighted value, with your current item's value as the floor.
**Anonymous:** GGG refuses weighted groups ("Query is too complex"), so the search sorts on one stat, keeps the stat your current item gives most at least as high, and ranks locally. If a weighted search is refused, for example because the session expired, it falls back to this automatically.

## Logging in with your session cookie (optional)

Settings → **Log in with your session cookie**: paste your `POESESSID`, press **Test**, then **Save**. **How do I get this?** (also in the ☰ menu) walks through copying it from your browser (F12 → Application/Storage → Cookies → pathofexile.com → `POESESSID`).
- **Storage:** encrypted with Windows DPAPI for your Windows user, so it can't be read on another PC or by another user. It's never written to the log.
- **Where it goes:** only ever sent to pathofexile.com.
- **Treat it like a password:** anyone with it is logged in to the website as you. Logging out of pathofexile.com ends the session, which revokes it.

## Reading the price check

- **Headline**: what to list at. When cheap listings are much cheaper than the typical one, it also gives a "sells fast" price.
- **Confidence**: based on how many similar items are listed (High ≥ 20, Medium 5–19, Low < 5).
- **Notes** explain the market:
  - **Ignored bait:** for in-person and bulk listings, prices far below the rest are ignored as price-fixing bait. Instant Buyout listings are never treated as bait, because anyone can buy them.
  - **Unsold cheapest listings:** they've sat for days, so buyers won't pay that much. Undercut them.
  - **Wide spread:** prices vary a lot, so your rolls matter.
  - **Stack value:** for currency and other bulk items.
- **Prices** are shown in the currency sellers use: a 4-divine item reads "4 div", not "880c".
- **Search stats**: untick (or tick) stats and press **Search again**. Each press is one more search.
- **Open on trade site** opens the exact search in your browser.

## Troubleshooting

| Problem | What to do |
|---|---|
| Nothing happens on Ctrl+D | Make sure PoE is focused and windowed, and that another tool (APT, Exiled Exchange) isn't also bound to Ctrl+D. |
| "Rate limit: try again in N s" | The trade site limits searches per IP (about 5 per 10 s). Wait it out; the app never retries on its own. |
| "Character data is private" | Make the Characters tab public (see above). |
| Test says the session wasn't accepted | It expired, or you logged out of the website. Copy a fresh `POESESSID`. |
| Compare shows old gear | ☰ → Reload character, or the button on the compare panel. |
| Wrong or odd search | Tray → **Open log**. Every checked item's text is logged; attach it to an issue. |
| Stale stats after a patch | Tray → **Refresh trade data**. |

## Releases

GitHub Actions (`.github/workflows/build.yml`) builds and tests every push to `main`. To release, push a version tag, or run the workflow from the Actions tab with a version:

```
git tag v1.2.0
git push origin v1.2.0
```

It builds the self-contained `PoeHelper.exe` and creates a GitHub Release with the exe, a zip with the README, and SHA-256 checksums. Versions with a suffix (`v1.2.0-beta.1`) are marked pre-release.

## Development

```
src/PoeHelper.Core     item parser, trade client + rate limiter, price analysis, character model, compare, upgrade finder (no UI)
src/PoeHelper.App      WPF overlay, keyboard hook, clipboard copy, tray, settings
tests/...              unit tests with saved item texts and trade-data fixtures
tools/PoeHelper.Cli    price-check item text files against the live league from a terminal
```

```powershell
dotnet test
dotnet run --project tools/PoeHelper.Cli -- price tests/PoeHelper.Core.Tests/Fixtures/items/gem_rf.txt
dotnet run --project tools/PoeHelper.Cli -- build "Name#1234" CharacterName
dotnet run --project tools/PoeHelper.Cli -- compare "Name#1234" CharacterName <item.txt>
dotnet run --project tools/PoeHelper.Cli -- upgrade "Name#1234" CharacterName Ring 2 divine [Life|Damage]
src\PoeHelper.App\bin\Debug\net10.0-windows\PoeHelper.exe --check <item.txt> <out.png>     # render price check to PNG
src\PoeHelper.App\bin\Debug\net10.0-windows\PoeHelper.exe --compare <item.txt> <out.png>   # render compare (uses Settings' character)
src\PoeHelper.App\bin\Debug\net10.0-windows\PoeHelper.exe --upgrade <item.txt> <out.png>   # compare + find upgrade
```

Character data comes from the public character API (`/character-window/get-items`, `get-passive-skills`), and node stats from GGG's [skilltree-export](https://github.com/grindinggear/skilltree-export), cached for a week. Other data comes from the official trade API (`/api/trade/search`, `/fetch`, `/exchange`, `/data/*`). The static data is cached in `%LOCALAPPDATA%\PoeHelper\cache` for a day. Settings and the log are in `%APPDATA%\PoeHelper`.

## Credits

Gem data (base damage, levels, support multipliers) is imported from [Path of Building Community](https://github.com/PathOfBuildingCommunity/PathOfBuilding), MIT licence, © 2016 David Gowor and contributors; stat data © Grinding Gear Games. The licence text is in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The bundled copy is `src/PoeHelper.Core/Data/skills.json`. To refresh it for a new league, clone Path of Building and run:

```
python -I tools/pob-import/import_pob.py <PathOfBuilding checkout> src/PoeHelper.Core/Data/skills.json
```

## Licence

PoeHelper is MIT licensed: see [LICENSE](LICENSE). Bundled third-party data keeps its own licence: see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
