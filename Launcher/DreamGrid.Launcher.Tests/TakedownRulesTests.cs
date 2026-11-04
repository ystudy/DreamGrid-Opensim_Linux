using DreamGrid.Launcher.Core.Database;
using DreamGrid.Launcher.Core.Security;
using Xunit;

namespace DreamGrid.Launcher.Tests;

/// <summary>Takedown wording, limits and what the sender sees (spec 2026-11-04-inventory-takedown-design).</summary>
public sealed class TakedownRulesTests
{
    private static readonly Guid Ann = Guid.Parse("0a000000-0000-0000-0000-00000000000a");
    private static readonly Guid Stranger = Guid.Parse("0e000000-0000-0000-0000-00000000000e");

    private static readonly TakedownNotice Notice = new(TakedownReason.Copyright, "Rita Rights", null, null, null, new DateOnly(2026, 11, 3), "Ours.");

    private static CaseItem Item(string name, string creatorId, string creatorName) =>
        new(Guid.NewGuid(), Ann, name, null, 0, creatorId, creatorName, "My Inventory / Secret folder", Guid.NewGuid().ToString(),
            new InventoryItemRow(null, 0, name, null, 0, 0, 0, creatorId, 0, 0, 0, 0, 0, "", 0, 0, Guid.NewGuid(), Ann, Guid.NewGuid(), 0), null, null, null,
            new DateTimeOffset(2026, 11, 4, 10, 0, 0, TimeSpan.Zero), null);

    private static TakedownCase Case(params CaseItem[] items) =>
        new("TD-2026-001", true, Notice, true, [new CasePerson(Ann, "Ann Holder", PersonOnCase.Named)], items, [], null);

    [Fact]
    public void Sender_view_masks_a_creator_who_is_a_holder()
    {
        var view = TakedownRules.SenderView(Case(Item("Own upload", Ann.ToString().ToUpperInvariant(), "Ann Holder"),
            Item("Bought", Stranger.ToString(), "Sam Maker")));
        Assert.Equal("a resident", view.Single(v => v.Name == "Own upload").Creator);
        Assert.Equal("Sam Maker", view.Single(v => v.Name == "Bought").Creator);
    }

    [Fact]
    public void Sender_view_has_no_holder_name_or_folder()
    {
        var view = TakedownRules.SenderView(Case(Item("Sword", Ann.ToString(), "Ann Holder")));
        var text = string.Join("|", view.Select(v => $"{v.Name} {v.Type} {v.Creator}"));
        Assert.DoesNotContain("Ann", text);
        Assert.DoesNotContain("Secret folder", text);
        Assert.Equal("object", TakedownRules.SenderView(Case(Item("Box", Stranger.ToString(), "S") with { AssetType = 6 })).Single().Type);
    }

    [Fact]
    public void Illegal_may_not_tell()
    {
        Assert.False(TakedownRules.MayTell(TakedownReason.Illegal));
        Assert.False(TakedownRules.DefaultTell(TakedownReason.Illegal));
        Assert.True(TakedownRules.MayTell(TakedownReason.Other));
    }

    [Fact]
    public void Defaults_tell_for_copyright_and_rules()
    {
        Assert.True(TakedownRules.DefaultTell(TakedownReason.Copyright));
        Assert.True(TakedownRules.DefaultTell(TakedownReason.Rules));
        Assert.False(TakedownRules.DefaultTell(TakedownReason.Other));
        Assert.Equal("Copyright (DMCA)", TakedownRules.ReasonWords(TakedownReason.Copyright));
        Assert.Equal("Illegal content", TakedownRules.ReasonWords(TakedownReason.Illegal));
        Assert.Equal("Breaks the grid's rules", TakedownRules.ReasonWords(TakedownReason.Rules));
        Assert.Equal("Other", TakedownRules.ReasonWords(TakedownReason.Other));
    }

    [Fact]
    public void Notice_without_sender_is_refused()
    {
        Assert.Equal("Who sent the notice is needed.", TakedownRules.NoticeProblem(Notice with { SenderName = "  " }));
        Assert.Null(TakedownRules.NoticeProblem(Notice));
        Assert.Equal("Each field can be at most 1000 characters.", TakedownRules.NoticeProblem(Notice with { Address = new string('a', 1001) }));
    }

    [Fact]
    public void Notice_text_over_4000_is_refused()
    {
        Assert.Equal("The notice text can be at most 4000 characters.", TakedownRules.NoticeProblem(Notice with { Text = new string('x', 4001) }));
        Assert.Null(TakedownRules.NoticeProblem(Notice with { Text = new string('x', 4000) }));
    }

    [Theory]
    [InlineData(0, "texture")] [InlineData(1, "sound")] [InlineData(2, "calling card")] [InlineData(3, "landmark")] [InlineData(5, "clothing")]
    [InlineData(6, "object")] [InlineData(7, "notecard")] [InlineData(10, "script")] [InlineData(13, "body part")] [InlineData(20, "animation")]
    [InlineData(21, "gesture")] [InlineData(49, "mesh")] [InlineData(56, "settings")] [InlineData(57, "material")] [InlineData(99, "item")]
    [InlineData(null, "item")]
    public void Type_words_cover_the_table_and_unknown_is_item(int? type, string words) => Assert.Equal(words, TakedownRules.TypeWords(type));

    [Fact]
    public void Takedown_audit_actions_are_recognised()
    {
        Assert.True(TakedownRules.IsTakedownAudit("inventory-viewed"));
        Assert.True(TakedownRules.IsTakedownAudit("takedown-removed"));
        Assert.False(TakedownRules.IsTakedownAudit("deleted"));
    }
}
