# extension-colored: a copy to try colours on

A copy of `../extension` (0.12.0, 2026-11-21), recoloured with the LockIn
logo's blues, softened for the eyes. The real extension in `../extension` is
untouched; nothing here ships.

## Where the colours are

- `theme.css`: every colour of the toolbar popup (`popup.html`) and the
  blocked-site page (`blocked.html`). One variable per role, with a comment
  on what it paints.
- `content/overlay.js`, the `THEME` block at the top: the YouTube block
  screen, the "Is this video study, or a distraction?" popup and the
  miniplayer notice. These are drawn inside YouTube's page, so they can't
  read `theme.css`.

Shapes, sizes and layout are the original's; only colours differ.

## Differences from `../extension` besides colour

- `manifest.json`: named "Smart Study Distraction Blocker (colored)", and no
  `key`, so it gets its own id and can be loaded next to the real one.
- `background.js`: `HOST` is a name that is never registered, so this copy
  always runs in standalone mode (no app), even on a PC where the app is
  installed. That way its full popup always shows. The app-connected face
  ("Control from App") isn't reachable here.

## Trying a colour

1. Edit `theme.css` (or `THEME` in `content/overlay.js`).
2. Reload the extension (the circular arrow on its card in
   `chrome://extensions`), then reopen the popup. For the YouTube screens,
   also reload the YouTube tab.

To carry a palette over to the real extension, copy the values into
`../extension`'s `popup.html` / `blocked.html` / `content/overlay.js`.
