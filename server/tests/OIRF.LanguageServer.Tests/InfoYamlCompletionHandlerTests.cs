using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using OIRF.LanguageServer.Features;

namespace OIRF.LanguageServer.Tests;

public class InfoYamlCompletionHandlerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("OIRF.LanguageServer.Tests.InfoYamlCompletion.").FullName;

    public InfoYamlCompletionHandlerTests()
    {
        File.WriteAllText(Path.Combine(_dir, "idle.png"), "");
        File.WriteAllText(Path.Combine(_dir, "walking-sheet.png"), "");
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Offers_png_names_inside_an_inline_flow_list()
    {
        const string filesLine = "    files: [";
        var text = "animations:\n  - id: idle\n" + filesLine + "\n";
        var position = new LspPosition(2, filesLine.Length);

        var items = InfoYamlCompletionHandler.Handle(text, position, _dir).ToList();

        Assert.Contains(items, i => i.Label == "idle");
        Assert.Contains(items, i => i.Label == "walking-sheet");
    }

    [Fact]
    public void Offers_png_names_after_a_partial_entry_in_a_flow_list()
    {
        const string filesLine = "    files: [idl";
        var text = "animations:\n  - id: idle\n" + filesLine + "\n";
        var position = new LspPosition(2, filesLine.Length);

        var items = InfoYamlCompletionHandler.Handle(text, position, _dir).ToList();

        Assert.Contains(items, i => i.Label == "idle");
    }

    [Fact]
    public void Offers_png_names_on_a_block_list_bullet()
    {
        const string bulletLine = "      - ";
        var text = "animations:\n  - id: idle\n    files:\n" + bulletLine + "\n";
        var position = new LspPosition(3, bulletLine.Length);

        var items = InfoYamlCompletionHandler.Handle(text, position, _dir).ToList();

        Assert.Contains(items, i => i.Label == "idle");
    }

    [Fact]
    public void Offers_png_names_for_a_spritesheet_scalar_value()
    {
        const string filesLine = "    files: ";
        var text = "animations:\n  - id: walk\n    spritesheet: true\n" + filesLine + "\n";
        var position = new LspPosition(3, filesLine.Length);

        var items = InfoYamlCompletionHandler.Handle(text, position, _dir).ToList();

        Assert.Contains(items, i => i.Label == "walking-sheet");
    }

    [Fact]
    public void Offers_nothing_once_the_flow_list_is_closed()
    {
        const string filesLine = "    files: [idle] ";
        var text = "animations:\n  - id: idle\n" + filesLine + "\n";
        var position = new LspPosition(2, filesLine.Length);

        var items = InfoYamlCompletionHandler.Handle(text, position, _dir).ToList();

        Assert.Empty(items);
    }

    [Fact]
    public void Offers_nothing_outside_a_files_value()
    {
        const string idLine = "  - id: ";
        var text = "animations:\n" + idLine + "\n";
        var position = new LspPosition(1, idLine.Length);

        var items = InfoYamlCompletionHandler.Handle(text, position, _dir).ToList();

        Assert.Empty(items);
    }

    [Fact]
    public void Offers_a_block_list_bullet_under_a_sibling_key_nothing()
    {
        // "id:" sits at the same indentation as "files:" - a bullet nested under a DIFFERENT
        // key entirely must not pick up "files:" from a few lines further up.
        const string bulletLine = "      - ";
        var text = "animations:\n  - files:\n      - a\n    id:\n" + bulletLine + "\n";
        var position = new LspPosition(4, bulletLine.Length);

        var items = InfoYamlCompletionHandler.Handle(text, position, _dir).ToList();

        Assert.Empty(items);
    }
}
