# Architecture

## Overview

Two pieces:

- **`client/`** — a thin VSCode extension (TypeScript). Decides whether the current workspace
  looks like an OIRF/Eptus engine project, and if so spawns the server and forwards LSP traffic
  to it via `vscode-languageclient`.
- **`server/`** — the actual language server (`OIRF.LanguageServer`, .NET 9). Talks LSP over
  stdio via `OmniSharp.Extensions.LanguageServer`.

The server is deliberately where all the intelligence lives. It opens the workspace's own
`.sln`/`.slnx`/`.csproj` with Roslyn (`Microsoft.CodeAnalysis.MSBuild`) and reads `[Prototype]` /
`[RegisterComponent]` / `[DataField]` types straight from source — the engine's own built-in
types and any custom ones a downstream game project defines, indistinguishably, since both come
from the same semantic-model walk. No build step, no separate schema-export tool: editing a
custom component's C# and saving is enough for the schema to pick it up (debounced ~750ms).

## Request flow

```
VSCode workspace
   │
   ├─ client/src/workspaceDetection.ts   -- cheap heuristic scoring (see below)
   │      │ score >= 2
   │      ▼
   ├─ client/src/extension.ts            -- spawns server, wires LanguageClient
   │
   ▼
server/src/OIRF.LanguageServer/
   ├─ Program.cs                         -- MSBuildLocator.RegisterDefaults() FIRST
   ├─ LanguageServerHost.cs              -- OmniSharp LSP wiring, all handler registration
   ├─ Workspace/
   │    ├─ EngineWorkspaceLocator.cs     -- finds the right .sln/.slnx/.csproj to open
   │    ├─ RoslynWorkspaceHost.cs        -- owns MSBuildWorkspace, engine-relevant projects
   │    ├─ EngineWorkspaceManager.cs     -- orchestrator: schema + resource index + rescans
   │    └─ DebouncedRescanQueue.cs
   ├─ Schema/
   │    ├─ SchemaBuilder.cs              -- the Roslyn walk: [Prototype]/[RegisterComponent] → EngineSchema
   │    ├─ EngineSchema.cs               -- data model (PrototypeTypeInfo, ComponentTypeInfo, ...)
   │    ├─ XmlDocToMarkdown.cs           -- /// doc comments → hover Markdown, incl. <inheritdoc cref>
   │    └─ AssetFieldHeuristics.cs       -- SpriteKey/ShaderPath/"Key"-on-Animation* → asset kind
   ├─ Yaml/
   │    ├─ PrototypeYamlParser.cs        -- YamlDotNet → PrototypeDocument with LSP ranges
   │    ├─ NodeContext.cs / NodeContextResolver.cs  -- "what's under the cursor"
   │    └─ OpenDocumentStore.cs          -- full-text sync cache for open documents
   ├─ Assets/
   │    ├─ ResourceIndexer.cs            -- finds Textures/Shaders folders anywhere in the workspace
   │    └─ SpriteInfoYamlReader.cs       -- reimplements AssetManager.Animation.cs's info.yml parsing
   └─ Features/
        ├─ CompletionHandler.cs
        ├─ HoverHandler.cs
        └─ PrototypeValidator.cs        -- diagnostics, severities mirrored from PrototypeManager
```

## Key design decisions

**Resource roots are not fixed to a folder named "Resources".** The engine supports registering
additional resource roots (`SharedResourceManager.AddResourcesFolder`). So detection and indexing
key off folders literally named `Prototypes`/`Textures`/`Shaders` **anywhere** in the workspace,
never a fixed `Resources/Prototypes` path.

**Grammar scope (Milestone 1): Prototypes only.** `Resources/Maps/*.yml` (or wherever a project's
maps live) uses a structurally different grammar — mapping root instead of sequence root,
`components:` as a map keyed by PascalCase CLR member name instead of a list, matched
case-insensitively. `EngineSchema` is grammar-agnostic (pure CLR data), so adding Maps support
later only needs a new parser/validator pair, not a schema rework.

**Diagnostic severities mirror the engine exactly**, verified against source
(`PrototypeManager.cs`, `DataFieldConverter.cs`):

| Condition | Severity |
|---|---|
| unknown `type:` value | Error |
| missing `type:`/`id:` structurally | Error |
| `[DataField(required:true)]` missing (no `parent:`) | Error |
| unknown top-level prototype field | **Warning** |
| unknown component `type:` | Error |
| unknown component field | **Error** |

**Known YamlDotNet quirk**: block mapping/sequence nodes report a degenerate `End` mark (equal to
`Start`) since there's no explicit closing token to anchor it to. `PrototypeYamlParser.GetTrueEnd`
recomputes the real end recursively from the last contained leaf scalar — without this, every
multi-line prototype/component item's range would collapse to a single point and completion/hover
would only ever work on line 1 of a file. Covered by `PrototypeYamlParserTests`.

**Required-field check and `parent:` inheritance**: prototype inheritance merges parent fields
before a real load would check for required ones, so `PrototypeValidator` only runs the
"missing required field" check on prototypes with no `parent:` at all — a documented Milestone 1
stopgap (full parent-chain resolution needs a workspace-wide pre-scan, not just the open
document).

## Workspace activation heuristic

Client-side (`workspaceDetection.ts`), before spawning the server at all:

| Signal | Weight |
|---|---|
| any `**/Prototypes/**/*.{yml,yaml}` exists | +2 |
| a `.sln`/`.slnx`/`.csproj` references `Engine.Shared`/`Engine.Client`/`Engine.Server` | +2 |
| `.gitmodules` references `OIRF.Engine` | +1 |
| a sampled `.cs` file uses `Engine.Shared.Prototypes`/`[RegisterComponent(`/`[DataField(` | +1 |

Spawns the server only at score ≥ 2. Server-side, `EngineWorkspaceManager.IsEngineWorkspace` is
re-checked after the schema builds (defense-in-depth) — every feature handler no-ops if false.
