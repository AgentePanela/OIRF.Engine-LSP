# Milestones

## Milestone 1 — Prototype IntelliSense (this repo's current state)

Shipped:
- Workspace activation/detection (client heuristic + server-side defense-in-depth).
- Roslyn-based schema extraction: `[Prototype]`/`IPrototype`/`[DataField]` and
  `[RegisterComponent]`/`Component` types, built-in **and** custom, picked up from source with no
  build step (debounced rescan on `.cs` save).
- Full completion/hover/diagnostics for Prototype YAML (`**/Prototypes/**/*.{yml,yaml}`):
  prototype `type:` completion, field-name completion, component `type:` completion, component
  field-name completion, hover with `///` doc comments (incl. `<inheritdoc cref>` resolution),
  diagnostics matching the engine's own severities.
- Asset-path completion for sprite (`SpriteKey` + name heuristics, including full `info.yml`
  spritesheet/explicit-frame key derivation) and shader (`ShaderPath` + name heuristics) fields,
  plus a distinct `Animation` key kind for `AnimationComponent.Key`-shaped fields.
- Editing support for `info.yml` itself (`**/Textures/**/info.yml`): diagnostics mirroring every
  throw condition in the real `Engine.Client.Assets.AssetManager.Animation.cs`
  (`ParseInfoFile`/`ParseAnimationEntry`) as Errors, plus Warnings for mistakes the engine doesn't
  crash on but silently misbehaves for (a `files:` entry pointing at a PNG that doesn't exist next
  to the info.yml, a duplicate `id` silently overwriting an earlier definition), and completion for
  PNG file names inside a `files:` value (both inline-flow and block-list styles). Hover/definition
  intentionally out of scope for now - see `InfoYamlValidator`/`InfoYamlCompletionHandler`.
- Completion for `ProtoId<T>` fields (bare, nullable, or as a collection/array element - e.g.
  `HashSet<ProtoId<TagPrototype>>`, `ProtoId<TilePrototype>?[,]`): offers every known `id:` value
  of prototypes registered as `T`, sourced from a new workspace-wide `PrototypeIdIndex` (built by
  re-running `PrototypeYamlParser` over every `**/Prototypes/**/*.{yml,yaml}` file, not just the
  one currently open - `EngineSchema` alone only knows the C# type *definitions*, never the actual
  instances authored in YAML). Verified against the real `Project.Trieste/Game/AutoTiling/AutoTileComponent.Group`
  field (`ProtoId<TagPrototype>`). See `AssetFieldHeuristics.ClassifyProtoId`/`PrototypeIdIndexer`.
- Verified end-to-end against the real `Project-Eptus` workspace: schema built (5 prototype
  types, 15 component types — matching the engine's built-ins plus `Project.Eptus`'s custom
  `NoSaveComponent`/`InputMoverComponent`), completion/hover/diagnostics all confirmed live
  against `Resources/Prototypes/Entities/Player.yml`.
- 67 unit tests (`SchemaBuilderTests`, `PrototypeYamlParserTests`, `PrototypeValidatorTests`,
  `EngineWorkspaceLocatorTests`, `RoslynWorkspaceHostSlnxTests`, `InfoYamlValidatorTests`,
  `InfoYamlCompletionHandlerTests`, `PrototypeIdIndexerTests`, and others), all passing.

Not yet done (explicitly out of scope for M1, tracked here so nothing is forgotten):
- **Packaging**: no `vsce package` / `.vsix` build has been produced yet, and the
  `dotnet publish` → `client/dist/server` copy step described in the README isn't wired into an
  actual script yet. Needed before this can be installed as a normal extension rather than run via
  F5 dev host.
- **F5 GUI verification**: the protocol-level round trip (completion/hover/diagnostics) has been
  proven directly against the server process; an actual VSCode Extension Development Host session
  (typing in a real editor, seeing the completion popup, etc.) has not been driven yet and needs a
  human or an automated VSCode extension test to close the loop.
- Cross-file/workspace-wide required-field check honoring `parent:` inheritance (currently a
  single-file stopgap — parent-having prototypes skip the check entirely rather than resolving the
  chain).
- Type-coercion value diagnostics (e.g. flagging `friction: "abc"` on a `float` field).
- Duplicate-component-in-one-list warnings, go-to-definition, rename, code actions.
- Self-contained/AOT multi-RID packaging (M1 assumes the target machine already has the .NET SDK,
  same as any Eptus engine developer's machine).

## Milestone 2 — Maps grammar

`Resources/Maps/*.yml` (mapping root, `components:` as a map keyed by PascalCase CLR member name,
`removedComponents`, `id`/`name` entity references). `EngineSchema` is already grammar-agnostic,
so this needs a new `MapYamlParser`/`MapValidator` pair and a second document selector, not a
schema rework.

## Milestone 3+ — Fluent locale (`.ftl`) autocomplete

Explicitly named as future work by the user. Separate document selector and schema source
entirely (parses `.ftl` message/function definitions, not C# reflection) — shares only the
extension host activation plumbing with Milestone 1.
