# Kautuka brand system

This is the canonical written rule for the Kautuka visual identity across the
website, web application, installer, desktop tray, and shipped application
icons. Use the shared artwork and values described here instead of recreating
the logo or selecting colors by eye.

## Name and wordmark

- In ordinary writing, headings, page titles, metadata, accessible names, and
  product descriptions, spell the product **Kautuka** with a capital K.
- In the designed logo lockup, render the wordmark as **kautuka** in lowercase.
  The icon glyph is also a lowercase **k**. Do not use `Kautuka` as the visual
  wordmark beside the icon.
- Keep the established typeface, size, weight, letter spacing, and layout when
  using the wordmark. Do not use the gradient for ordinary text.

## Canonical mark and colors

The app mark is a rounded square with a corner radius of about 27% of its size,
filled with a horizontal vermilion–turmeric–vermilion gradient and a centered
bold white lowercase `k`. Use the canonical asset rather than drawing another
version.

```css
--k-vermilion: #E34234;
--k-turmeric: #E3A018;
--k-thread: linear-gradient(90deg, #E34234 0%, #E3A018 50%, #E34234 100%);
```

The gradient may be used for the logo mark, the lowercase wordmark, and large
branded headline/tagline text. Never place body or supporting copy on the
gradient. Do not apply the brand gradient to other text, controls, links,
backgrounds, or surfaces. Keep all non-brand colors and layout as defined by
the product or site design.

## Shared assets and implementation

| Surface | Source of truth |
|---|---|
| Kautuka website mark and icon generation | Maintained in the separate Kautuka website repository. |
| Kautuka web app mark | `src/frontend/web/public/assets/kautuka-mark.svg` (synced from the website source) |
| Web app lockup and wordmark styling | `src/frontend/web/src/components/BrandMark.tsx` and `src/frontend/web/src/index.css` |
| Web app page icons | `src/frontend/web/public/assets/icons/` and `src/frontend/web/index.html` |
| Windows installer lockup | `src/installer/Kautuka.Installer.Windows/wwwroot/index.html` |
| Windows desktop and Monitor tray marks | `src/installer/Kautuka.Monitor/MonitorTray.cs` |
| Windows executable icon | `src/frontend/web/public/assets/icons/kautuka.ico`, referenced by installer/Monitor project files |

When updating the artwork, update the website source and regenerate its icon
variants, then synchronize the generated assets into the app. Do not edit a
single generated copy in isolation. Preserve the same mark in the app header,
setup/onboarding, installer, desktop and Monitor tray, browser favicons, and
any other place that presents the Kautuka logo. Sentence text around the logo
continues to use the written form **Kautuka**.

## Icon sizes

- 16px and 32px favicon: solid vermilion rounded square, turmeric border (1px
  at 16px, 2px at 32px), and bold white lowercase `k`. Do not use the gradient
  at these sizes.
- 180px Apple touch icon and 512px app icon: use the gradient app mark.
- SVG and PNG variants must follow the same designs. Use the same favicon set
  on every Kautuka site and web-app page. Windows `.ico` variants use the
  gradient app mark at their supported sizes.

## Use checklist

Before adding a brand treatment, confirm:

1. Prose says **Kautuka**; a visual lockup says **kautuka** and uses the
   lowercase `k` mark.
2. The mark comes from the canonical asset or an explicitly synchronized native
   rendering of that same artwork.
3. Gradient colors and direction match the values above.
4. Gradient text is limited to the wordmark and large branded headline/tagline;
   ordinary body and supporting copy use the normal foreground color.
5. Small favicons use the solid vermilion treatment, and every page references
   the shared icon set.
6. No unrelated colors, backgrounds, controls, link styles, or layout changed.

## Current cross-system audit

The Kautuka app lockup uses lowercase `kautuka`; prose and page titles use
capitalized `Kautuka`. Its React `BrandMark` points at the shared SVG, and the
Windows installer uses the same mark artwork and a lowercase visual wordmark.
The Monitor tray draws a native equivalent of the canonical gradient mark. The
app entry page declares the SVG and PNG favicon sizes, Apple touch icon, and
512px app icon. The website mark SVG and app copy have matching SHA-256 hashes.

The related website audit found and corrected 16 guide lockups and the Kautuka
company product lockup to use lowercase `kautuka`; ordinary prose, titles,
navigation labels, and metadata remain capitalized **Kautuka**. The audit also
removed the stale blue mark background from the Kautuka guides stylesheet. Its
shared icon generator emits solid vermilion/turmeric small favicons and the
gradient large icons; the site refresh script injects icon links into the page
set. Check these same source points whenever the website or app icon assets are
regenerated so that they remain synchronized.
