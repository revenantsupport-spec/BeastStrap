# BeastStrap Release Notes

## v420.80.0

### Changed

- Upgraded the runtime from .NET 6 to .NET 8 LTS for better performance and security.
- Replaced SixLabors.ImageSharp with SkiaSharp (MIT-licensed) for image decoding, fixing known vulnerabilities and removing a paid-license gate on Release builds.
- Added a test project (`BeastStrap.Tests`) and a root `.editorconfig` for consistent style.
- Cleaned up nullable-reference warnings in window scaling code.

### Fixed

- Window manipulation and scaling code no longer dereferences possibly-null screen references.

## Heads-up: it's Halloween season

Update or install in October and you get the new Halloween theme straight away, pumpkin icon and all. Your own theme comes back by itself on Nov 2. Not for you? Open Appearance and press Turn it off for this year, or turn off "Switch to the Halloween theme every October" to skip it every year. Then hit Save.

## New: themes for the whole app

Appearance can turn BeastStrap into a creator's own edition. You get their colours across the app, their avatar or logo on the windows, taskbar, tray and launcher, and their banner on the Home page.

Pick from YouTubers ZeroDetections, vaehz and 7GrandDad, or script sites Roblox Scripts, Sir Meme Hub, V3rmillion, ScriptBlox, HaxHell and Rscripts.

Seasonal themes switch on by themselves for 4/20 on April 20th, Halloween from Oct 1 to Nov 1 and Christmas from Dec 24 to 26, then hand back to your own theme.

Each seasonal theme has a yearly switch in Appearance. While one is on, a notice there lets you turn it off for this year, and picking any theme or changing a colour does the same.

Done with a creator theme? Back to the standard BeastStrap look is right under the themes.

A "New: themes" note on the Home page takes you straight there with Open Appearance.

## Removed

The Obfuscator and Deobfuscator tabs have been removed. The LeakD/LuaObfuscator tooling they depended on is no longer shipped.

## Changed

- The Scripts tab opens on the whole catalogue. It used to filter to your last game every time. That game is a one-click button now.
- Three new sites in the rotating banner: farmsync.cloud, zeropoint.to and V3rmillion, which is back at v3rm.net.

## Fixed

- Your custom app icon sticks now. It went back to the normal icon every time you reopened BeastStrap.
- Your theme colours reach the bits they missed. The filter chips, the version and game pickers, the script details popup and the launcher's background glow stuck to fixed colours whatever you picked.
- Closing settings asks about unsaved changes when it should. If you saved and then changed something back, the window closed without asking and your change was lost.
EOF