// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import { remarkPrefixBase } from './plugins/remark-prefix-base.mjs';

const BASE = '/csharp-cel/';

export default defineConfig({
  // GitHub Pages: site is the user/org domain, base is the repo path. With
  // `<owner>.github.io/<repo>/` as the published URL, every internal link in
  // the built site needs the base prefix. Astro auto-prefixes static asset
  // URLs and Starlight's sidebar slugs, but plain markdown links written as
  // `/foo/bar/` are NOT — the remarkPrefixBase plugin below fixes that.
  site: 'https://suhdev.github.io',
  base: BASE,
  markdown: {
    remarkPlugins: [remarkPrefixBase(BASE)],
  },
  integrations: [
    starlight({
      title: 'CEL for .NET',
      description:
        'An idiomatic C# / .NET 10 implementation of the Common Expression Language.',
      social: {
        github: 'https://github.com/google/cel-spec',
      },
      editLink: {
        baseUrl: 'https://github.com/your-org/cel-csharp/edit/main/docs/',
      },
      sidebar: [
        {
          label: 'Start Here',
          items: [
            { label: 'Overview', slug: 'index' },
            { label: 'Installation', slug: 'getting-started/installation' },
            { label: 'Hello, world', slug: 'getting-started/hello-world' },
            { label: 'Core concepts', slug: 'getting-started/concepts' },
          ],
        },
        {
          label: 'Concepts',
          items: [
            { label: 'What is CEL?', slug: 'concepts/what-is-cel' },
            { label: 'Language tour', slug: 'concepts/language-tour' },
            { label: 'Type system', slug: 'concepts/type-system' },
            { label: 'Evaluation model', slug: 'concepts/evaluation-model' },
            { label: 'Errors & unknowns', slug: 'concepts/errors-and-unknowns' },
            { label: 'Gradual typing', slug: 'concepts/gradual-typing' },
          ],
        },
        {
          label: 'Guides',
          items: [
            { label: 'Compile & evaluate', slug: 'guides/compiling-and-evaluating' },
            { label: 'Working with POCOs', slug: 'guides/working-with-pocos' },
            { label: 'Working with protos', slug: 'guides/working-with-protos' },
            { label: 'Declaring variables', slug: 'guides/declaring-variables' },
            { label: 'Declaring functions', slug: 'guides/declaring-functions' },
            { label: 'Building extensions', slug: 'guides/building-extensions' },
            { label: 'Parser macros', slug: 'guides/parser-macros' },
            { label: 'Optionals & null', slug: 'guides/optionals-and-null' },
            { label: 'Error handling', slug: 'guides/error-handling' },
            { label: 'Performance & trimming', slug: 'guides/performance-and-trimming' },
          ],
        },
        {
          label: 'Reference',
          items: [
            {
              label: 'Public API',
              collapsed: false,
              items: [
                { label: 'CelExpression', slug: 'reference/api/cel-expression' },
                { label: 'CelEnv', slug: 'reference/api/cel-env' },
                { label: 'CompiledProgram', slug: 'reference/api/compiled-program' },
                { label: 'Activations', slug: 'reference/api/activations' },
                { label: 'CelValue', slug: 'reference/api/cel-value' },
                { label: 'CelType / CelTypes', slug: 'reference/api/cel-types' },
                { label: 'ITypeProvider', slug: 'reference/api/itype-provider' },
                { label: 'ICelExtension', slug: 'reference/api/icel-extension' },
              ],
            },
            {
              label: 'Language',
              collapsed: false,
              items: [
                { label: 'Operators', slug: 'reference/language/operators' },
                { label: 'Standard library', slug: 'reference/language/stdlib' },
                { label: 'Macros', slug: 'reference/language/macros' },
              ],
            },
            { label: 'Extensions', slug: 'reference/extensions' },
            { label: 'Conformance', slug: 'reference/conformance' },
          ],
        },
      ],
      customCss: ['./src/styles/custom.css'],
    }),
  ],
});
