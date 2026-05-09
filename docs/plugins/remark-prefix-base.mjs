// @ts-check
import { visit } from 'unist-util-visit';

/**
 * Remark plugin that prefixes leading-slash links with a base path. Astro's `base`
 * config only auto-prefixes static asset URLs, not internal links written in
 * markdown. Without this, every `[foo](/bar/)` in a doc page resolves to the
 * apex domain (`/bar/`) and 404s on a project Pages site (`<owner>.github.io/<repo>/`).
 *
 * Skips protocol-relative URLs (`//host`), in-page anchors (`#frag`), and links
 * that already start with the base prefix (idempotent across re-runs).
 *
 * @param {string} base - The base path, with or without leading/trailing slash.
 */
export function remarkPrefixBase(base) {
  const trimmed = '/' + base.replace(/^\/+|\/+$/g, '');
  if (trimmed === '/') {
    // No base configured — nothing to do.
    return () => () => {};
  }
  return () => (tree) => {
    visit(tree, (node) => {
      if (
        node.type === 'link'
        && typeof node.url === 'string'
        && node.url.startsWith('/')
        && !node.url.startsWith('//')
        && !node.url.startsWith(trimmed + '/')
        && node.url !== trimmed
      ) {
        node.url = trimmed + node.url;
      }
    });
  };
}
