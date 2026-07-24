// LSP's own Position/Range model doubles as this server's internal document-position type
// throughout the Yaml/Features layers - `Range` bare would otherwise collide with System.Range.
global using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
global using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
