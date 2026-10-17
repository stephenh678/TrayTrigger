# Themes

## Choosing one

**Settings > General > Appearance** has the theme picker. The choice applies at once, with no restart, and is remembered.

- **Dark** is the default.
- **Light** is a full light look.
- **Green**, **Amber** and **Purple** are Dark with a different accent color.
- **Abyssal** is a deep-blue theme with a glowing background picture, and shows off the Corners card: a different shape for tags, buttons, cards and posters.
- **Auto (follow Windows)** uses Light or Dark to match Windows' app mode, and switches when Windows does.
- Any theme of your own appears below those, marked "(yours)".

Each theme has a **background picture** behind the pages, showing around the cards. Untick **Show the theme's background picture** to use the theme's plain background color instead. The picture is part of the theme, so switching themes switches it.

The title bars follow the theme too. A theme that is hard to read (text too close to its background) is still applied, with a line in Appearance saying which text is too faint.

## Text size

**Settings > General > Appearance** has a **Text size** slider, from 90% to 150%. It makes all of TrayTrigger's text larger or smaller together, so headings stay larger than body text. It applies at once, is remembered, and is not changed by Reset Settings to Defaults. Type a number in the box for an exact size, or press **Reset to 100%**, the size the app was designed at.

That size is used for every theme, the built-in ones too. A theme can also set a size of its own: in the theme editor, untick **From Settings** next to **Size** on the **Font and text size** card and choose one. The editor's preview shows it before you save, and when the theme on screen has its own size, the Settings slider is greyed out with a note saying so, since it would show nothing until another theme is chosen.

The smallest text (the labels on badges) never goes below its normal size. Past 130%, some screens may clip text; the slider says so, and lowering the size fixes it. The tray menu's text follows it too; its icons keep their size.

In the theme file a theme's own size is one line, `"textSize": 120`. Leave it out to follow Settings.

## Font

**Settings > General > Appearance** also has a **Font** list. It sets the font for all of TrayTrigger's text. Any font installed on your computer can be chosen (symbol fonts such as Wingdings are left out, since they would turn every label into pictures), and each name is shown in its own face. It applies at once, is remembered, and is not changed by Reset Settings to Defaults. Segoe UI fills in anything the chosen font does not have, and **Reset to Segoe UI** puts the default back. Icons, and the code-style text in Help, keep their own fonts. Some fonts are taller or wider than Segoe UI, so a very wide one may clip text in places, as a large text size can.

Like the text size, a theme can set a font of its own: in the theme editor, untick **From Settings** next to **Font** on the **Font and text size** card and pick one. The editor's preview shows it before you save, and when the theme on screen has its own font, the Settings list is greyed out with a note saying so. If a theme's font is not installed on a computer, the font from Settings is used there. In the theme file it is one line, `"font": "Georgia"`; leave it out to follow Settings.

## Making your own

Press **New theme from this...** in Appearance while a built-in theme is chosen. A built-in theme cannot be changed, so it opens as a copy ("My Dark") that is saved as a theme of your own. With one of your own themes chosen the button reads **Edit theme...** and the theme opens as it is.

- **Colors.** Seven colors make up a theme: Background, Surface (cards and panels), Text, Accent (buttons, the selected tab, links), Danger, and the optional Warning and Success. Click a swatch to pick a color: drag in the square (or use the arrow keys, with Shift for bigger steps), move the hue strip, or choose one of the suggested colors. Or type or paste `#RRGGBB` in the box. The colors that go with them (hover, pressed, tints, dimmer text) are worked out for you.
- **Live preview.** The **Live preview >** tab at the top right slides a preview out to the right of the window (the window gets wider; the editor keeps its size, and the window moves left if it would run off the screen). It shows your colors, corners, text size and font as you change them: the tab strip and buttons, a game row, three game cards at the size they have in the library (a card at rest, one under the mouse and one selected; they are your own most played games with a cover, or made-up games if the library has none), a text box in a dialog card, and a strip of how buttons and rows look when pressed, under the mouse, focused, disabled and selected. It starts closed. Nothing else changes until you press **Save Changes**, and **Cancel** leaves everything as it was. The **Main colors** card names any text that may be hard to read, and says nothing when all of it is fine.
- **Corners.** The **Corners** card sets how round everything is. Pick a shape from the list (Rounded, Leaf, Reverse leaf, Blade, Reverse blade, Ramp, Reverse ramp, Left cap, Right cap, Folder tab, Hanging, Speech bubble, Reverse speech bubble, One big corner or Pill; each is drawn beside its name) and set **Roundness** from 0 (square) to 16. 4 with Rounded is how TrayTrigger looks without a theme. Up to 8 the corners grow gently; past 8 each step is much larger and the number turns the warning color, because very round corners can clip text and art. A Pill is as round as it can be, so it has no roundness. **Customize** (open at first, **Hide** closes it) gives tags, controls, cards and posters a shape and a size each. Tags, controls, cards and posters are four separate rows: a row that says **Default** follows the shape and roundness above it (a poster says **Follows cards**: it follows the cards, one pixel less round; a library game card showing its poster has no background of its own and its outline follows the poster, so a very round poster is not left with a squarer card behind it). Change a row and it has its own shape and size; the arrow beside it puts it back to Default. The shape and roundness above always work: choosing a new shape sets every row to that shape, and moving the roundness makes every row rounder or squarer by the same amount, so the differences between rows stay. A theme with a different shape on every row can go back to one shape in a step (Cancel keeps what you had). Moving the roundness leaves each row's shape alone, and choosing a shape leaves each row's size alone. A theme file that sets a tag, control, card or poster shows that row as its own. **Reset** puts every corner back to TrayTrigger's own. Tags are badges and small chips, controls are buttons, boxes and list rows, cards are cards and panels, and posters are the cover art inside a game card.
- **Picture.** **Change...** chooses the theme's background picture and **Remove** takes it away; the slider sets how strongly it shows. **Keep in view** sets which part of the picture always stays on screen when the window is a different shape (Center, Left, Right, Top or Bottom). See below for what a picture has to be.
- **Font and text size.** The **Font and text size** card (left column, under Background picture) has a part for each. Each follows Settings while its **From Settings** box is ticked; untick it to give the theme its own: a font from the list, or a size from 90 to 150 (the slider, or a number in the box). **Reset** on the card puts both back to following Settings. Past 130% the card says some screens may clip text. Only the live preview changes until you save.
- **Window size.** The editor opens at the size you last left it. The live preview panel is not part of that size: it is added beside the window each time it opens.
- **Randomize.** **Randomize** (top right) fills the main colors, the corners, the text size, the font and (when the theme has a picture) how strongly the picture shows at random with a new starting point. The text size stays between 90% and 130%, the font is one that can write ordinary text, and the theme gets its own text size and font. The colors are built from one hue and their text is easy to read; the corners are ones where tags, controls, cards and posters often get a shape and size of their own, anywhere on the sliders (so a roll can be extreme; Undo and the next press are one click away). It is only a start: change any color, shape or size by hand afterwards. The small **Randomize** link on the Main colors card rolls only the colors, and the one on the Corners card only the corners. Press it again for another try, and **Undo randomize** (it appears after the first press) puts back what was there before the last one. The name, the picture, the Dark or Light start and any fine-tuning colors the file sets by hand are left alone. **Reset** on the Main colors card puts the main colors back to how they were when the window opened.
- **Starts from.** Dark or Light is where the colors you leave alone come from. Changing it starts the colors again from Dark or Light.
- **Fine-tuning.** About thirty more colors (hover shades, borders, badge colors) are not in the window. They are in the theme file: **Open file** shows it in Explorer. Open it in any text editor and change them there. The app follows each time you save the file: no restart, and no need to reselect it. If the file sets some of them itself, the window says so, because a color the file lists keeps its value when you change a main color.

**Import theme...** adds a theme made elsewhere: choose its `.theme.json` (or a plain `.json`) and, if it has one, its picture, in the same window. The theme is copied into your Themes folder and chosen at once. Nothing is overwritten: a name already in use becomes "Name (2)". A picture the theme names is found next to the file if you do not choose it.

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
- **shape** (optional) sets corner radii in pixels, and is covered in the next section.
- **font** (optional) is the name of an installed font used for all the text while the theme is on. Leave it out to use the font from Settings.
- **textSize** (optional) is how large all the text is while the theme is on, in percent, from 90 to 150. Leave it out to use the size from Settings.
- A color you do not list is worked out for you. Setting `Accent` also moves the hover, pressed and link colors that go with it; setting `Background` moves the card and border colors; setting `TextPrimary` moves the dimmer text colors. Name a color yourself and yours is used.

## Corners in the theme file

The editor writes the corners as plain numbers in an optional `shape` block, and you can write them yourself:

```
"shape": { "RadiusCard": 20, "RadiusControl": [0, 12, 0, 12] }
```

- A number sets all four corners. Four numbers set the top-left, top-right, bottom-right and bottom-left. 0 is square.
- **RadiusSmall** (tags and badges, 0 to 16), **RadiusControl** (buttons, boxes and rows, 0 to 48), **RadiusCard** (cards and panels, 0 to 64) and **RadiusPoster** (cover art, 0 to 96). The word `Radius` may be left off (`"Card": 20`).
- A radius you leave out is TrayTrigger's own. A poster you leave out is the card's radius less 1, corner by corner, so the art meets the card's outline.
- A larger number than the limit is held to the limit. A name TrayTrigger does not have, or a value that is not a number or four numbers, is skipped and listed in Appearance.
- A radius larger than half a wide control's height stretches it into a lens rather than a pill; 16 is plenty for a button. A poster is about 180 pixels wide, so 90 or more makes its top an arch.
- The editor shows numbers it did not draw as **Custom (from the file)** and leaves them exactly as they are until you pick a shape for that kind of corner. Saving a theme only changes the corners you changed.

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
- A corner radius that is not a number or four numbers, and a radius name TrayTrigger does not have, are skipped and listed in Appearance; the rest of the file is still used.
- A color that is not written as `#RRGGBB`, and a color name TrayTrigger does not have (a typo), are skipped and listed in Appearance; the rest of the file is still used.
- A `font` that is not a name is skipped and listed in Appearance. One that is not installed on this computer is listed too, and the theme shows in the font from Settings.
- A `textSize` that is not a number is skipped and listed in Appearance, and the theme follows the size in Settings. One outside 90 to 150 is held to the nearest limit and listed too.
- Choosing **Dark (Default)** in the picker puts the default back whatever state a theme is in.
