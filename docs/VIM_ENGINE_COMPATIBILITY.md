# VimEngine compatibility boundary

`VimEngine` is the stable public facade. Its host-facing state, input, selection,
buffer, Ex-command, clipboard, search, spell, and extension APIs delegate to an
internal runtime that is not part of the compatibility surface.

New integrations should group registries and host dependencies in
`VimEngineServices`. The older individual constructor parameters
(`syntaxLanguages`, `commands`, `services`, `keyBindings`, `normalCommands`, and
`commandGrammar`) remain supported as per-engine overrides, so existing source
code does not need to change.

No public `VimEngine` API is currently scheduled for removal. If an API becomes
obsolete later, it must first receive an `ObsoleteAttribute`, a replacement
documented here, and a compatibility test before removal in a major release.
