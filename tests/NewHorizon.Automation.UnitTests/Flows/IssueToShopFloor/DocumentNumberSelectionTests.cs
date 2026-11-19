using FluentAssertions;
using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.UnitTests.Flows.IssueToShopFloor;

public sealed class DocumentNumberSelectionTests
{
    private static readonly SjoHeader ThisYear = Sjo(1, "26-27", "SJ", "NF1", "000123");
    private static readonly SjoHeader LastYear = Sjo(2, "25-26", "SJ", "NF1", "000123");
    private static readonly SjoHeader Similar = Sjo(3, "26-27", "SJ", "NF1", "001230");
    private static readonly SjoHeader AlphanumericSjo = Sjo(4, "26-27", "SJ", "CBP", "S26037");

    [Fact]
    public void A_full_number_matches_exactly_one_sjo()
    {
        Parse("26-27/SJ/NF1/000123").Match([ThisYear, LastYear, Similar])
            .Should().ContainSingle().Which.Id.Should().Be(1);
    }

    [Fact]
    public void A_full_number_is_case_and_space_insensitive_and_pads_the_running_number()
    {
        Parse(" 26-27 / sj / nf1 / 123 ").Match([ThisYear]).Should().ContainSingle();
    }

    [Fact]
    public void A_bare_number_matching_two_sjos_returns_both_so_the_caller_can_refuse()
    {
        Parse("123").Match([ThisYear, LastYear, Similar]).Select(sjo => sjo.Id).Should().BeEquivalentTo([1L, 2L]);
    }

    [Fact]
    public void Never_matches_a_substring()
    {
        // The ERP's own search is LIKE '%0123%', which would offer 001230 as well.
        Parse("0123").Match([Similar]).Should().BeEmpty();
    }

    [Fact]
    public void Full_and_bare_alphanumeric_sjo_numbers_are_supported_without_zero_padding()
    {
        var fullSelection = Parse("26-27/SJ/CBP/S26037");
        fullSelection.IsFullNumber.Should().BeTrue();
        fullSelection.Number.Should().Be("S26037");
        fullSelection.Match([AlphanumericSjo]).Should().ContainSingle().Which.Id.Should().Be(4);

        var bareSelection = Parse("S26037");
        bareSelection.IsFullNumber.Should().BeFalse();
        bareSelection.Number.Should().Be("S26037");
        bareSelection.Match([AlphanumericSjo]).Should().ContainSingle().Which.Id.Should().Be(4);
    }

    [Fact]
    public void Numeric_running_numbers_are_padded_to_six_digits()
    {
        Parse("26-27/SJ/NF1/123").Number.Should().Be("000123");
        Parse("123").Number.Should().Be("000123");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("26-27/SJ/000123")]
    [InlineData("26-27/SJ/NF1/12#A")]
    [InlineData("26-27/SJ/NF1/12345678901")]
    [InlineData("12345678901")]
    [InlineData("26-27//NF1/123")]
    public void Rejects_text_that_is_not_an_sjo_number(string input)
    {
        DocumentNumberSelection.TryParse(input, out var selection, out var error).Should().BeFalse();
        selection.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    private static DocumentNumberSelection Parse(string input)
    {
        DocumentNumberSelection.TryParse(input, out var selection, out var error).Should().BeTrue(error);
        return selection!;
    }

    private static SjoHeader Sjo(long id, string year, string group, string site, string number) =>
        new(id, year, group, SiteId: 1, site, number, ItemCode: "FG-1", Status: "A", StatusDescription: "Authorized");
}
