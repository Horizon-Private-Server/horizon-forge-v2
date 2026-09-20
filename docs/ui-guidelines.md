# Forge UI guidelines

Forge is a desktop editor, so its interface favors information density over the
roomier defaults common to web applications. The shared Mantine theme in
`src/renderer/theme.ts` is the source of truth.

## Density rules

- Use the themed component defaults. Do not repeat `size`, `gap`, `padding`, or
  `radius` props unless a control has a specific reason to differ.
- Use `xs` gaps within a control group, `sm` padding at panel edges, and `md`
  spacing only between distinct sections. Prefer borders or headings over empty
  space when separating editor regions.
- Keep routine controls one compact row tall. Toolbars and tree rows should aim
  for 24–28 px; text must remain at least 11 px and keyboard focus must remain
  visible.
- Put labels beside controls when width allows. Use stacked labels for narrow
  panels or when descriptions and validation messages are needed.
- Reserve roomy layouts for onboarding, empty states, and destructive
  confirmations where reduced density improves comprehension.
- Build with Mantine tokens and components before adding local CSS. Add a shared
  theme default when the same override appears in three places.

## Component conventions

- Toolbars: compact buttons or action icons, `xs` gaps, no decorative padding.
- Docked panels: `sm` edge padding; section headers use `Title` order 5 or 6.
- Docked panel placement belongs to Dockview. Persist its layout through
  `ui.editorLayout`; never reproduce docking or splitter behavior in local CSS.
- A closed panel is hidden, not deleted. Keep a View-menu action that can reopen
  every registered panel and a Reset Layout action for recovery.
- Forms: `xs` inputs, short descriptions, errors directly below their field.
- Lists and trees: a single-line primary label, optional muted metadata, and
  selection state that does not change row dimensions.
- Modals: use the smallest width that avoids wrapping primary controls; keep
  actions together at the bottom edge.

Any exception should solve a concrete usability or accessibility issue rather
than recreating web-page spacing inside the editor shell.
