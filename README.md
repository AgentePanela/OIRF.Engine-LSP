# OIRF Engine LSP

VSCode extension + language server providing IntelliSense (completion, hover docs, diagnostics,
asset-path completion) for the OIRF/Eptus engines YAML **Prototype** files

The server is a C# (.NET 9) process that opens the current workspace's `.sln`/`.csproj` and reflects over `[Prototype]`/`[RegisterComponent]`/`[DataField]` types, and reads
`///` XML doc comments straight from source for hover text. The VSCode extension is a thin
TypeScript client that spawns this server.

> [!note]
> WARNING: This project is 99% vibecoded with my instructions because i dont have enough time
> and knowlodgment to write this. I have plans to rework this project.

## Development

```
# Build the server
dotnet build server/OIRF.LanguageServer.sln

# Run server tests
dotnet test server/OIRF.LanguageServer.sln

# Build the client
cd client && npm install && npm run compile
```

Press F5 in VSCode (with `client/` open, or via the provided launch config) to start an
Extension Development Host with the extension loaded.
