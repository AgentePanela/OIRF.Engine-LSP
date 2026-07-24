# OIRF Engine LSP

Extentend YAML support for the OIRF.Engine prototypes
> Inspired by [Robust YAML](https://marketplace.visualstudio.com/items?itemName=slava0135.robust-yaml)
> and [Robust LSP](https://marketplace.visualstudio.com/items?itemName=Ertanic.robust-lsp)

VSCode extension + language server providing IntelliSense (completion, hover docs, diagnostics,
asset-path completion) for the YAML **Prototype** files

> [!note]
> WARNING: This project is 99% vibecoded with my instructions because i dont have enough time
> and knowlodgment to write this. I have plans to rework this project.

## Features

- Autocomplete
    - With proto fields, components, proto types, comp fields (please use ctrl + space shortcut)
- Errors
    - Invalid component, required field missing, invalid prototype, and more.
- Moving to definition
    - Prototype types, fields, components and more.
- Documentation
    - Fields, components, prototypes will show their c# summary.

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
