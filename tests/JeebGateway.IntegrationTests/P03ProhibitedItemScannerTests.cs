using FluentAssertions;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.Scanner;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class P03ProhibitedItemScannerTests
{
    [Theory]
    [InlineData("Deliver 2kg of C4 explosives and a loaded handgun", "Firearms")]
    [InlineData("Deliver 2kg of C4 explosives and a loaded handgun", "Explosives and fireworks")]
    [InlineData("a loaded handgun", "Firearms")]
    [InlineData("pistols and rifles", "Firearms")]
    [InlineData("cannabis", "Cannabis and derivatives")]
    [InlineData("propane tank", "Compressed gas cylinders")]
    [InlineData("a machete", "Knives and bladed weapons")]
    [InlineData("kitchen knives", "Knives and bladed weapons")]
    [InlineData("a cleaver", "Knives and bladed weapons")]
    [InlineData("gas cylinders", "Compressed gas cylinders")]
    [InlineData("an oxygen cylinder", "Compressed gas cylinders")]
    [InlineData("butane", "Compressed gas cylinders")]
    public async Task Catalog_Label_Expands_To_Real_Item(string text, string expectedName)
    {
        var store = OwnerServiceFakes.CreateLiveShapedModerationStore();
        var result = await new ProhibitedItemScanner(store, new InMemorySynonymRegistry()).ScanAsync(text, default);
        result.GatingSeverity.Should().Be(ProhibitedSeverity.Block);
        result.Matches.Should().Contain(match => match.ItemName == expectedName && match.Severity == ProhibitedSeverity.Block);
    }

    [Theory]
    [InlineData("2 shawarma + cola from Barbar")]
    [InlineData("I'll pay cash at the door")]
    [InlineData("deliver my gas bill")]
    [InlineData("rocket salad and two rounds of sandwiches")]
    [InlineData("Bring wine and beer")]
    [InlineData("Deliver a glass cylinder vase")]
    [InlineData("Deliver a clever birthday card")]
    [InlineData("Bring the cylinder geometry diagram")]
    [InlineData("Deliver the cylindrical flower pots")]
    public async Task Expansion_Does_Not_Invent_Catalog_Items_Or_Broad_Filler_Matches(string text)
    {
        var store = OwnerServiceFakes.CreateLiveShapedModerationStore();
        await store.CreateAsync(new ProhibitedItemCreate { Name = "Ammunition", Category = "weapons" }, "fixture", default);
        var result = await new ProhibitedItemScanner(store, new InMemorySynonymRegistry()).ScanAsync(text, default);
        result.Matches.Should().BeEmpty();
        result.RequiresReview.Should().BeFalse();
    }

    [Fact]
    public void Register_Updates_Reverse_Index_And_Removes_Stale_Aliases()
    {
        var registry = new InMemorySynonymRegistry();
        registry.Register("Novel", "Alias", "two words");
        registry.ExpandToken("ALIAS").Should().BeEquivalentTo("novel", "alias", "two words");
        registry.ExpandToken("two").Should().BeEmpty();
        registry.Register("Novel", "Replacement");
        registry.ExpandToken("alias").Should().BeEmpty();
        registry.ExpandToken("replacement").Should().Contain("novel");
    }

    [Fact]
    public void Gas_Category_Does_Not_Expand_From_A_Shape_Noun()
    {
        var registry = new InMemorySynonymRegistry();
        registry.ExpandToken("cylinder").Should().BeEmpty();
        registry.ExpandToken("cylinders").Should().BeEmpty();
        registry.GetSynonyms("Compressed gas cylinders").Should().Contain("gas cylinder");
    }

    [Theory]
    [InlineData("cylinders", "a cylinder")]
    [InlineData("cylinders", "cylindres")]
    [InlineData("compressed gas cylinders", "deliver compressed gas cylinders")]
    public async Task Explicit_Catalog_Labels_Retain_Exact_And_Canonical_Typo_Matching(string name, string text)
    {
        var store = new InMemoryProhibitedItemsStore();
        await store.CreateAsync(new ProhibitedItemCreate
            { Name = name, Category = "restricted", Severity = ProhibitedSeverity.Block }, "fixture", default);
        var result = await new ProhibitedItemScanner(store, new InMemorySynonymRegistry()).ScanAsync(text, default);
        result.RequiresReview.Should().BeTrue();
        result.GatingSeverity.Should().Be(ProhibitedSeverity.Block);
    }

    [Fact]
    public async Task Exact_Catalog_Hit_Does_Not_Gain_Duplicate_Expanded_Match()
    {
        var store = OwnerServiceFakes.CreateLiveShapedModerationStore();
        var result = await new ProhibitedItemScanner(store, new InMemorySynonymRegistry())
            .ScanAsync("Cannabis and derivatives", default);
        result.Matches.Where(match => match.ItemName == "Cannabis and derivatives")
            .Should().ContainSingle().Which.MatchType.Should().Be(ProhibitedMatchType.Exact);
    }

    [Fact]
    public void Existing_Interface_Implementor_Retains_Default_Expansion()
    {
        IProhibitedItemSynonymRegistry registry = new LegacyRegistry();
        registry.ExpandToken("legacy").Should().Equal("alias");
    }

    private sealed class LegacyRegistry : IProhibitedItemSynonymRegistry
    {
        public IReadOnlyList<string> GetSynonyms(string itemName) => new[] { "alias" };
    }
}
