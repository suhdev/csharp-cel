# Cel for .NET — documentation site

Astro Starlight site, source under `src/content/docs/`.

## Local development

```sh
cd docs
npm install
npm run dev          # http://localhost:4321
```

## Build

```sh
npm run build        # outputs to docs/dist
npm run preview      # serve the built site locally
```

## Layout

```
docs/
├── astro.config.mjs        # Site title, sidebar, theme
├── src/
│   ├── content.config.ts   # Starlight content collection
│   ├── content/docs/       # All markdown / mdx pages
│   │   ├── index.mdx
│   │   ├── getting-started/
│   │   ├── concepts/
│   │   ├── guides/
│   │   └── reference/
│   └── styles/custom.css
└── package.json
```

## Editing

Pages are plain Markdown (`.md`) with Starlight's frontmatter:

```markdown
---
title: Page title
description: Shown in search and as the meta description.
---
```

The MDX landing page (`index.mdx`) imports Starlight components for hero +
card grid layouts.

## Adding a page

1. Drop a new `.md` (or `.mdx`) under `src/content/docs/<section>/`.
2. Add an entry to the matching sidebar group in `astro.config.mjs`:

   ```js
   { label: 'My new page', slug: '<section>/<filename>' }
   ```

3. `npm run dev` picks it up immediately.
