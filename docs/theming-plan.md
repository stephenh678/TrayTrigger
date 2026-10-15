# Theming (1.6.2-beta.2)

Built-in themes and user themes, one format, one loader. This file is the contract: the color names
below are what a theme file is written against, so renaming one breaks every theme that uses it.

## What a theme is

A JSON file, `<name>.theme.json`. Built-in themes are embedded in the app; user themes live in
`%AppData%\TrayTrigger\Themes\`. The same loader reads both.

```json
{
  "schema": 1,
  "name": "Forest",
  "author": "someone",
  "base": "dark",
  "colors": {
    "Accent": "#2EA043",
    "Background": "#101412"
  }
}
```

- A theme of the user's is known by its **file name** (`Forest.theme.json` is `Forest`), not by `name`; `name` is only
  what the picker shows. So a copy of a file is a theme of its own, and changing `name` never changes which theme is
  selected.
- `base` is `dark` or `light`. It names the built-in theme that supplies every color the file does not
  mention, and it tells Windows whether the title bar is in dark mode.
- `colors` lists only what the theme changes. Names are the palette names in `Resources/Colors.xaml`
  without the `Brush` prefix (`Accent`, `TextPrimary`, ...). Values are `#RRGGBB` or `#AARRGGBB`.
- An unknown name is ignored; a bad value is ignored; both are logged and listed under Settings > Appearance. A file that is not JSON, or
  has `schema` newer than the app knows, is not applied; the app stays on Dark and says so.

## Background picture

A theme may have a picture behind the pages: `"background": { "image": "forest.jpg", "strength": 0.6 }`. The
picture sits under everything in the main window, covered by a layer of the theme's own `Background` color
(`1 - strength` thick), so text on the page keeps the contrast the theme was designed with. The cards and the
sidebar are solid and are not touched; menus and dialogs have no picture.

- A built-in theme's picture is embedded (`Resources/Themes/Backgrounds/*.jpg`, made by
  `tools/Make-ThemeBackgrounds.py`; replace a file to change it). A user theme's picture is a file beside the
  theme file, in the same folder and no larger than 20 MB, decoded once at no more than 2560 x 1440 pixels' worth, whatever its shape (a photo taken on its side is turned upright).
- No `background` block: the theme takes its base theme's picture. `"image": "none"`: no picture.
- Settings > Appearance > **Show the theme's background picture** turns pictures off for every theme.
- High Contrast has none.

## Derived colors

Most themes should name a handful of colors. A color that is not written is worked out, in this order:

1. The value in `colors`, if there is one.
2. If the color's *anchor* is in `colors`, the base theme's value moved by the same relationship: in
   OKLCH, the same change in lightness, the same ratio of chroma and the same hue offset that the base
   theme has between the anchor and this color. Changing nothing gives back the base value exactly.
3. The base theme's value.

| Anchor | Derives |
|---|---|
| `Accent` | AccentHover, AccentText, AccentPressed, AccentWash, BorderAccent, Brand, InfoText |
| `Background` | Surface, SurfaceHover, DialogCard, Border, BorderStrong |
| `TextPrimary` | TextSecondary, TextMuted, TextSoft, TextDisabled |
| `Danger` | DangerWash, DangerBorder, DangerSolid, DangerSolidHover |
| `Warning` | WarningWash, WarningBorder, Highlight |
| `SuccessText` | SuccessWash, SuccessSolid |

Everything else (Gold, BadgePurple and its wash, Scrim, OverlayLine) is only ever set directly.

## Loading

1. Read the built-in theme named by `base` (always complete).
2. Apply derivation, then the explicit `colors`.
3. Build one frozen `SolidColorBrush` per name, plus the `Color` entries and the two tab-strip gradients.
4. Replace the dictionary in `Application.Resources` that holds them. Every view reads these with
   `{DynamicResource}`, so they repaint without a restart.
5. Tell the window chrome (`WindowThemeService`) to re-apply the caption color and dark/light mode, and
   raise `ThemeChanged` for the code that cached a brush.

Dark is the default and the fallback for anything that fails.

## Built-in themes

| Theme | How it is defined |
|---|---|
| Dark | Complete. The current look, and the base the others patch. |
| Light | Complete, hand-tuned: darker AccentText, darker status colors, light scrims. |
| High Contrast | Follows Windows' contrast colors (`SystemColors`). |
| Green, Amber, Purple | Dark with a different `Accent` (Purple also a purple-tinted `Background`). About five lines each; also the examples for authors. |

Settings > Appearance offers these, plus the user's own, plus **Auto**, which follows Windows' light or
dark mode.

## Out of scope for this version

Fonts and text sizes stay fixed. They are already named (`Typography.xaml`), so making them themeable later
is a smaller step than this one.

## Corner radii (1.6.3)

Corner radii are themeable. `Shapes.xaml` keeps its names, and a theme file's optional `shape` block sets the
ones it changes, as `colors` does:

```
"shape": { "RadiusCard": 20, "RadiusControl": [0, 12, 0, 12] }
```

- **Four kinds.** `RadiusSmall` (tags and badges, 0 to 16), `RadiusControl` (buttons, boxes and rows, 0 to 48),
  `RadiusCard` (cards and panels, 0 to 64) and `RadiusPoster` (the cover art, 0 to 96). A number sets all four
  corners; four numbers set top-left, top-right, bottom-right, bottom-left. The word `Radius` may be left off.
  `RadiusLarge` (circles) is not themeable.
- **What follows the card.** A poster that the theme does not set is the card's radius less 1 pixel per corner, which is
  the card's border, so the art meets the outline. The card's inner top and bottom edges (`RadiusInnerTop`,
  `RadiusInnerBottom`) follow it the same way. A library card that shows its poster has no fill of its own, its outline takes the poster's
  corners (`PosterCardRadiusConverter`, scaled to the card's real size as the art's clip is) and the two dark shades over the art are cut to the
  poster's shape (`PosterShadeClipConverter`), so nothing of the card shows in a corner the art does not fill, for any shape and either card size. With no `shape` block every radius is the app's own, so an existing theme
  looks as it did.
- **How it reaches the views.** Every radius is read with `DynamicResource`, and `ThemeService` swaps in a fresh copy of
  `Shapes.xaml` built by `ThemeShape` when a theme applies, as it does for colors. The poster art, the Game Details art and
  the Edit Game art are clipped by `RoundedClipConverter` from their own border's radius and size, so they follow a
  theme's corners; no view clips to a written number. `ShapeGuardTests` enforces both.
- **No shape names in the file.** The Edit Theme window offers eleven shapes (Rounded, Leaf, Reverse leaf, Folder tab,
  Speech bubble, Pill, Hanging, Left cap, One big corner, Ramp, Blade) and a roundness from 0 to 16 (steps past 8 are steeper and marked extreme), for the whole theme and,
  under Customize, for each kind of corner. They exist only in the window; the file holds the numbers they work out to, and
  the window shows a file's numbers as the shape and size that draw them, or as Custom when none does. A fully round corner
  is 16 pixels for tags and controls (a larger radius on a wide control stretches into a lens, not a pill), 64 for cards and 96 for posters.
- **Live preview.** The editor holds only the controls. The preview is a panel on the window's right edge, opened by a tab: the
  window widens by the panel's width (moving left if it would leave the screen) and the editor keeps its size. It starts closed. Its game cards are the library's own card (184 x 276) with the player's most played games that have a cover, or made-up
  games when there are none (`PosterSample`).
- **Not themeable:** launcher icons, elliptical or cut corners, and anything Windows draws itself.
