# PoeHelper

A small Windows overlay for **Path of Exile 1** (league: Covens, 3.30). Hover an item in game and press a hotkey:

| Hotkey | Feature | Status |
|---|---|---|
| **Ctrl+D** | **Price check**: finds similar listings, then gives a recommended price with reasons | ✅ working |
| **Alt+D** | **Gear compare**: what changes if this item replaced yours (life, resists vs cap, attributes vs gem requirements, damage stats) | 🚧 next |
| button on compare | **Find upgrade**: Instant Buyout search for a better item within your budget, keeping resists capped and gem requirements met | 🚧 after compare |

## How it stays within GGG's rules

Like Awakened PoE Trade, each keypress does one thing:
1. Sends a single Ctrl+C (or Ctrl+Alt+C) to the game.
2. Reads the clipboard, then restores what was there before.
3. Makes one trade search plus up to two fetches of 10 listings.

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
3. Run `publish\PoeHelper.exe`. A gold **P** appears in the system tray.

### Option B: a friend's copy
Copy the `publish` folder (it only needs `PoeHelper.exe`) and run it. Each player enters their own account in Settings; nothing in it is tied to one person.

### In game
- Set PoE to **Windowed** or **Windowed Fullscreen**. An overlay can't draw over exclusive fullscreen.
- Hover an item and press **Ctrl+D**. The panel opens beside the item. **Esc**, the ✕ button, or pressing Ctrl+D over empty space closes it.
- Hotkeys only fire while PoE is the active window, so Ctrl+D still works normally in other apps.

### Settings (right-click the tray icon, or double-click it)
| Setting | Notes |
|---|---|
| Account name | Include the discriminator, e.g. `Name#1234` (shown on your pathofexile.com profile). Needed for compare and upgrade. |
| Character name | The character to compare against. |
| League | Defaults to `Covens`. |
| Listings | **Instant Buyout only** (default), Instant Buyout + in person, or any online seller. |
| Advanced copy | Uses Ctrl+Alt+C so mod tiers are known. Rares then search on their best (T1–T2) mods, not every line. |
| Contact | Optional email or forum name added to the User-Agent, as GGG asks of tool authors. |

To use compare, your profile's **Characters** tab must be public: pathofexile.com → My Account → Privacy Settings.

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
| Wrong or odd search | Tray → **Open log**. Every checked item's text is logged; attach it to an issue. |
| Stale stats after a patch | Tray → **Refresh trade data**. |

## Development

```
src/PoeHelper.Core     item parser, trade client + rate limiter, query builder, price analysis (no UI)
src/PoeHelper.App      WPF overlay, keyboard hook, clipboard copy, tray, settings
tests/...              unit tests with saved item texts and trade-data fixtures
tools/PoeHelper.Cli    price-check item text files against the live league from a terminal
```

```powershell
dotnet test
dotnet run --project tools/PoeHelper.Cli -- price tests/PoeHelper.Core.Tests/Fixtures/items/gem_rf.txt
src\PoeHelper.App\bin\Debug\net10.0-windows\PoeHelper.exe --check <item.txt> <out.png>   # render the overlay to a PNG
```

Data comes from the official trade API (`/api/trade/search`, `/fetch`, `/exchange`, `/data/*`). The static data is cached in `%LOCALAPPDATA%\PoeHelper\cache` for a day. Settings and the log are in `%APPDATA%\PoeHelper`.
