# Third-party notices

`Forge.App` carries icon geometry adapted from the projects below and embeds it as path data. Each section names what was
taken, where it lives, and the license and copyright line the upstream project requires.

Release artifacts must ship this file: the license below requires its notice and permission text to travel with the
binaries, so the published output of `Forge.App` (the app package, the installer payload and any tool package) carries
`THIRD_PARTY_NOTICES.md` next to the assembly that contains the adapted geometry.

## Fluent System Icons — adapted icon geometry

- Project: Fluent System Icons, <https://github.com/microsoft/fluentui-system-icons>
- License: MIT, Copyright (c) 2020 Microsoft Corporation (license text below)
- Taken from: the 24 px icon set published as `@fluentui/svg-icons` `1.1.345` (npm, matching `main` of the repository
  above). Only the `d` attribute of each icon's `<path>` is copied; the SVG wrapper, the `fill` colour, the `viewBox` and
  any `stroke-*` attributes are dropped.
- What was adapted, in `src/Forge.App/Resources/Icons.axaml` (one `StreamGeometry` per icon, on Fluent's 24×24 grid):
  `add`, `arrow_autofit_width`, `arrow_clockwise`, `arrow_download`, `arrow_expand`, `arrow_up`, `arrow_upload`, `book`,
  `chat_sparkle` (regular + filled), `checkmark`, `checkmark_circle`, `chevron_down`, `chevron_right`, `chevron_up`,
  `circle`,
  `circle_half_fill`, `cloud_arrow_down`, `comment_add`, `copy`, `delete`, `dismiss`, `document_bullet_list_clock`,
  `edit`, `folder`,
  `folder_add`, `layer`, `more_horizontal` (regular + filled), `open`, `pause_circle`, `person`, `pin` (regular +
  filled), `plug_connected`, `save`, `search`, `send`, `settings`, `shield_checkmark`, `sparkle`, `square`,
  `square_multiple`, `star` (regular + filled), `stop`, `subtract`, `text_wrap`, `window_new_rotate_180`, `wrench`.
- One icon comes from the 20 px set: `panel_bottom` exists only at 20 px, so its path was scaled by 24/20 about the grid
  centre into the 24 grid the app renders on (`IconTerminal`, the terminal button).
- The same geometry, for the tree glyphs, is reused through `src/Forge.App/ViewModels/TreeIcons.cs` (`branch`,
  `branch_fork`, `code`, `cube`, `document`, `folder_open`, `history`, `text_bullet_list` on top of the icons above).
- Nothing in the app's iconography is drawn locally: every glyph is copied from the set, and the one 20 px exception is
  only scaled, never redrawn.

### License text

```
MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
