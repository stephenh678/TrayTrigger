# Themes

## Choosing one

**Settings > General > Appearance** has the theme picker. The choice applies at once, with no restart, and is remembered.

- **Dark** is the default.
- **Light** is a full light look.
- **Green**, **Amber** and **Purple** are Dark with a different accent color.
- **Auto (follow Windows)** uses Light or Dark to match Windows' app mode, and switches when Windows does.
- Any theme of your own appears below those, marked "(yours)".

Each theme has a **background picture** behind the pages, showing around the cards. Untick **Show the theme's background picture** to use the theme's plain background color instead. The picture is part of the theme, so switching themes switches it.

The title bars follow the theme too. A theme that is hard to read (text too close to its background) is still applied, with a line in Appearance saying which text is too faint.

## Making your own

Press **New theme from this...** in Appearance while a built-in theme is chosen. A built-in theme cannot be changed, so it opens as a copy ("My Dark") that is saved as a theme of your own. With one of your own themes chosen the button reads **Edit theme...** and the theme opens as it is.

- **Colors.** Seven colors make up a theme: Background, Surface (cards and panels), Text, Accent (buttons, the selected tab, links), Danger, and the optional Warning and Success. Click a swatch to pick a color: drag in the square (or use the arrow keys, with Shift for bigger steps), move the hue strip, or choose one of the suggested colors. Or type or paste `#RRGGBB` in the box. The colors that go with them (hover, pressed, tints, dimmer text) are worked out for you.
- **Preview.** The preview card shows the colors as you change them. Nothing else changes until you press **Save Changes**, and **Cancel** leaves everything as it was. A line at the foot says whether all the text is easy to read.
- **Picture.** **Change...** chooses the theme's background picture and **Remove** takes it away; the slider sets how strongly it shows. **Keep in view** sets which part of the picture always stays on screen when the window is a different shape (Center, Left, Right, Top or Bottom). See below for what a picture has to be.
- **Starts from.** Dark or Light is where the colors you leave alone come from. Changing it starts the colors again from Dark or Light.
- **Fine-tuning.** About thirty more colors (hover shades, borders, badge colors) are not in the window. They are in the theme file: **Open file** shows it in Explorer. Open it in any text editor and change them there. The app follows each time you save the file: no restart, and no need to reselect it. If the file sets some of them itself, the window says so, because a color the file lists keeps its value when you change a main color.

**Open themes folder** goes to `%AppData%\TrayTrigger\Themes` any time. Theme files end in `.theme.json`; copy one to share it (and its pictures). A theme is known by its file name; the `name` inside it is only what the picker shows, so a copy of a file is a theme of its own.

To get rid of a theme of your own, select it and press the **trash button** beside the picker. TrayTrigger asks first. The theme file and the pictures made for it go to the Recycle Bin, so you can bring them back from there, and TrayTrigger returns to Dark. If the Recycle Bin is turned off for that drive, the question says the files will be deleted for good. Built-in themes cannot be deleted.

A theme file looks like this:

```
{
  "schema": 1,
  "name": "Forest",
  "base": "dark",
  "colors": {
    "Accent": "#238636",
    "Background": "#101412"
  }
}
```

- **base** is `dark` or `light`. It is the built-in theme that supplies every color the file does not list, and it sets whether the title bar is dark.
- **colors** lists only what you change (a file made by the editor also lists the rest as comments: remove the `//` in front of one to set it yourself). Colors are `#RRGGBB`, or `#AARRGGBB` when they are partly see-through.
- A color you do not list is worked out for you. Setting `Accent` also moves the hover, pressed and link colors that go with it; setting `Background` moves the card and border colors; setting `TextPrimary` moves the dimmer text colors. Name a color yourself and yours is used.

## A background picture for your own theme

In **Edit theme...**, **Change...** checks the picture and, when you save, copies it unchanged next to your theme file, numbered in the order you add them (`Forest.theme.json` gets `Forest-1.jpg`, then `Forest-2.png`), and sets it in the file. Nothing is ever deleted, so to go back to an earlier picture choose it again from the Themes folder.

A picture has to be:

- a **JPG or PNG**, at least **1280 x 720** (smaller looks blurry), and no larger than 8K (7680 x 4320) or 20 MB. TrayTrigger does not resize it: if it is too small or too large you are told what to change.
- **any shape.** The picture fills the window and the edges that do not fit are cropped, never squashed, so keep what matters near the middle. A picture that will be cropped a lot says so. A picture 1920 x 1080 is plenty; a bigger one is kept in memory shrunk to about 3.7 megapixels (2560 x 1440). A photo taken on its side is turned upright.

The picture is only held in memory while the window is open, not while TrayTrigger waits in the tray.

To do it by hand, put the picture in the same folder as the theme file and name it in the file:

```
"background": { "image": "forest.jpg", "strength": 0.6, "position": "center" }
```

- **image** is the file name, in the Themes folder beside the theme file. `"none"` turns the picture off for the theme.
- **position** is which part of the picture stays in view when the window is a different shape: `center` (the default for a picture of your own), `left`, `right`, `top` or `bottom`. The built-in pictures are kept to the `right`, where the TT mark is, and a theme with no picture of its own takes its base theme's position along with its picture.
- **strength** is how much of the picture shows, from 0 to 1. The rest is covered by the theme's `Background` color, which keeps the text on the page readable whatever the picture is. A picture with no strength is shown at 0.6.
- A theme with no `background` at all takes the picture of the built-in theme it is based on.
- A picture put in the folder by hand may also be a BMP. If it cannot be used it is left out and Appearance says why.
- Copy the picture files along with the theme file to share a theme.

## The color names

| Name | What it colors |
|---|---|
| Background | The window behind everything |
| Surface | Cards, the sidebar and menus |
| SurfaceHover | A row or button under the mouse |
| DialogCard | The cards inside dialogs and the text boxes |
| Border, BorderStrong | Thin lines, and the outline of a text box or button |
| BorderAccent | An accent-colored outline |
| Accent | Primary buttons and the selected tab |
| AccentHover, AccentPressed | The same buttons under the mouse, and pressed |
| AccentText | Links, section headings and icons in the accent color |
| AccentWash | A faint accent background (a selected row, an info badge) |
| Brand | The "Trigger" half of the name in the sidebar |
| InfoText | Text on an AccentWash |
| TextPrimary | Main text |
| TextSecondary, TextMuted, TextSoft, TextDisabled | Dimmer text, from nearly primary to hardly there |
| TextOnSolid | Text on a solid accent, danger or success fill: keep it light |
| ArtText, ArtTextSecondary, ArtTextMuted | Text drawn over a game's poster: keep these light, the poster fade is dark in every theme |
| Danger, DangerWash, DangerBorder, DangerSolid, DangerSolidHover | Errors and destructive buttons |
| Warning, WarningWash, WarningBorder, Highlight | Cautions, and the search highlight |
| SuccessText, SuccessWash, SuccessSolid | Things that are fine, or done |
| Gold | Favorite stars |
| BadgePurple, BadgePurpleWash | The GOG and Admin badges |
| Scrim | The dark panel over a poster (a play button, a category tag) |
| OverlayLine | The thin line around a badge |

Text and background pairs should differ enough to read: about 4.5 to 1 is the usual rule. Appearance tells you when a pair falls short.

## If something goes wrong

- A file that is not valid, or from a newer TrayTrigger, is not loaded. Appearance names the file and why, and the app stays on Dark.
- A color that is not written as `#RRGGBB`, and a color name TrayTrigger does not have (a typo), are skipped and listed in Appearance; the rest of the file is still used.
- Choosing **Dark (Default)** in the picker puts the default back whatever state a theme is in.
