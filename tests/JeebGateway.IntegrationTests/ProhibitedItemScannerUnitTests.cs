using FluentAssertions;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.Scanner;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Unit-level coverage of <see cref="ProhibitedItemScanner"/> that bypasses the
/// HTTP stack so we can exercise edge cases (diacritics, length-tiered fuzzy
/// budget, multi-word terms) without seeding the gateway each time.
/// </summary>
public class ProhibitedItemScannerUnitTests
{
    [Fact]
    public async Task Returns_No_Match_For_Empty_Description()
    {
        var scanner = NewScanner(("Knife", "weapons"));
        var result = await scanner.ScanAsync("   ", default);
        result.Matches.Should().BeEmpty();
        result.RequiresReview.Should().BeFalse();
    }

    [Fact]
    public async Task Exact_Match_Is_Case_And_Punctuation_Insensitive()
    {
        var scanner = NewScanner(("Knife", "weapons"));
        var result = await scanner.ScanAsync("Look — a KNIFE!!!", default);
        result.Matches.Should().ContainSingle()
            .Which.MatchType.Should().Be(ProhibitedMatchType.Exact);
        result.RequiresReview.Should().BeTrue();
    }

    [Fact]
    public async Task Diacritics_Are_Stripped_Before_Matching()
    {
        var scanner = NewScanner(("knife", "weapons"));
        var result = await scanner.ScanAsync("Le knífe est dangereux.", default);
        result.Matches.Should().ContainSingle()
            .Which.MatchType.Should().Be(ProhibitedMatchType.Exact);
    }

    [Fact]
    public async Task Multi_Word_Term_Requires_Word_Boundary()
    {
        var scanner = NewScanner(("hazardous material", "hazardous_materials"));

        var inside = await scanner.ScanAsync("Contains hazardous material in sealed drum.", default);
        inside.Matches.Should().ContainSingle()
            .Which.Confidence.Should().Be(1.0);

        // Not a boundary match (suffix attached) — must NOT trigger.
        var glued = await scanner.ScanAsync("Contains hazardousmaterialish gel.", default);
        glued.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Short_Tokens_Do_Not_Trigger_Fuzzy_Match()
    {
        // 'gun' is length 3 — too short for fuzzy, so 'fun' must not match.
        var scanner = NewScanner(("gun", "weapons"));
        var result = await scanner.ScanAsync("Just having some fun with friends.", default);
        result.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task Fuzzy_Match_Triggers_For_Common_Typos()
    {
        var scanner = NewScanner(("explosive", "weapons"));
        var result = await scanner.ScanAsync("contains exploive material", default);
        result.Matches.Should().Contain(m => m.MatchType == ProhibitedMatchType.Fuzzy);
    }

    [Fact]
    public async Task Synonym_Expansion_Matches_Pistol_To_Gun()
    {
        var scanner = NewScanner(("gun", "weapons"));
        var result = await scanner.ScanAsync("It's a small pistol for hunting.", default);
        result.Matches.Should().Contain(m =>
            m.ItemName == "gun" && m.MatchType == ProhibitedMatchType.Synonym);
    }

    [Fact]
    public async Task Inactive_Items_Are_Not_Scanned()
    {
        var store = new InMemoryProhibitedItemsStore();
        var created = await store.CreateAsync(new ProhibitedItemCreate
        {
            Name = "Knife", Category = "weapons", Description = null
        }, "admin", default);
        await store.UpdateAsync(created.Id, new ProhibitedItemPatch { Active = false }, "admin", default);

        var scanner = new ProhibitedItemScanner(store, new InMemorySynonymRegistry());
        var result = await scanner.ScanAsync("Sending a knife please.", default);
        result.Matches.Should().BeEmpty();
    }

    [Theory]
    [InlineData("knife", "knive", 1)]
    [InlineData("explosive", "exploive", 1)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("abcd", "acbd", 1)] // adjacent transposition costs 1
    public void DamerauLevenshtein_Computes_Expected_Distance(string a, string b, int expected)
    {
        DamerauLevenshtein.Distance(a, b, 5).Should().Be(expected);
    }

    [Fact]
    public void DamerauLevenshtein_Honors_Max_Cap()
    {
        // 'kitten' -> 'sitting' is 3 edits; cap at 1 must short-circuit to > cap.
        DamerauLevenshtein.Distance("kitten", "sitting", 1).Should().BeGreaterThan(1);
    }

    private static ProhibitedItemScanner NewScanner(params (string Name, string Category)[] items)
    {
        var store = new InMemoryProhibitedItemsStore();
        foreach (var (name, category) in items)
        {
            store.CreateAsync(new ProhibitedItemCreate
            {
                Name = name, Category = category, Description = null
            }, "admin-test", default).GetAwaiter().GetResult();
        }
        return new ProhibitedItemScanner(store, new InMemorySynonymRegistry());
    }
}
