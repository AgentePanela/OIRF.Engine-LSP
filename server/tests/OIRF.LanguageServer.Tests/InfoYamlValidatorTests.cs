using OIRF.LanguageServer.Features;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// Every "must ..." rule here is verified directly against the throw conditions in the real
/// Engine.Client.Assets.AssetManager.Animation.cs (ParseAnimationEntry) - see InfoYamlValidator's
/// class doc comment.
/// </summary>
public class InfoYamlValidatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("OIRF.LanguageServer.Tests.InfoYaml.").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void CreatePng(string nameNoExtension) => File.WriteAllText(Path.Combine(_dir, nameNoExtension + ".png"), "");

    [Fact]
    public void No_animations_key_is_valid()
    {
        var diagnostics = InfoYamlValidator.Validate("foo: bar\n", _dir).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Missing_id_is_error()
    {
        var diagnostics = InfoYamlValidator.Validate("animations:\n  - files: [idle]\n", _dir).ToList();

        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("Missing required field 'id'"));
    }

    [Fact]
    public void Missing_files_is_error()
    {
        var diagnostics = InfoYamlValidator.Validate("animations:\n  - id: idle\n", _dir).ToList();

        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("is missing 'files'"));
    }

    [Fact]
    public void Non_spritesheet_files_must_be_a_list()
    {
        var diagnostics = InfoYamlValidator.Validate("animations:\n  - id: idle\n    files: idle\n", _dir).ToList();

        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("must be a list of file names"));
    }

    [Fact]
    public void Spritesheet_files_must_be_a_single_scalar()
    {
        var text = "animations:\n  - id: walk\n    spritesheet: true\n    files: [a, b]\n    frameWidth: 1\n    frameHeight: 1\n    frameCount: 1\n";
        var diagnostics = InfoYamlValidator.Validate(text, _dir).ToList();

        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("must be a single file name"));
    }

    [Fact]
    public void Spritesheet_missing_frame_count_is_error()
    {
        var text = "animations:\n  - id: walk\n    spritesheet: true\n    files: sheet\n    frameWidth: 1\n    frameHeight: 1\n";
        CreatePng("sheet");

        var diagnostics = InfoYamlValidator.Validate(text, _dir).ToList();

        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("Missing required field 'frameCount'"));
    }

    [Fact]
    public void Missing_texture_file_is_warning()
    {
        var diagnostics = InfoYamlValidator.Validate("animations:\n  - id: idle\n    files: [ghost]\n", _dir).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("'ghost.png' was not found", diagnostic.Message);
    }

    [Fact]
    public void Existing_texture_file_is_not_flagged()
    {
        CreatePng("idle");

        var diagnostics = InfoYamlValidator.Validate("animations:\n  - id: idle\n    files: [idle]\n", _dir).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Duplicate_id_is_warning()
    {
        CreatePng("idle");
        CreatePng("idle2");
        var text = "animations:\n  - id: idle\n    files: [idle]\n  - id: idle\n    files: [idle2]\n";

        var diagnostics = InfoYamlValidator.Validate(text, _dir).ToList();

        Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Warning && d.Message.Contains("Duplicate animation id 'idle'"));
    }

    [Fact]
    public void Empty_files_list_is_warning()
    {
        var diagnostics = InfoYamlValidator.Validate("animations:\n  - id: idle\n    files: []\n", _dir).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("zero frames", diagnostic.Message);
    }

    [Fact]
    public void Valid_spritesheet_entry_has_no_diagnostics()
    {
        CreatePng("walking-sheet");
        var text = "animations:\n  - id: walking\n    spritesheet: true\n    files: walking-sheet\n    frameWidth: 32\n    frameHeight: 64\n    frameCount: 4\n";

        var diagnostics = InfoYamlValidator.Validate(text, _dir).ToList();

        Assert.Empty(diagnostics);
    }
}
