using FluentAssertions;
using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.UnitTests.Flows.IssueToShopFloor;

public sealed class InwardFifoTests
{
    [Fact]
    public void Orders_by_the_receipt_date_in_the_inward_number_not_the_erps_text_order()
    {
        // The ERP lists these by text, which puts the 2025 inward first. Real numbers from BATTERY7854.
        var rows = new[]
        {
            Row(27740, "INW03022025063350I1"),
            Row(9119, "INW05022024013943I1"),
            Row(18900, "INW05062024051653I1"),
        };

        InwardFifo.Order(rows).Select(row => row.StockId).Should().Equal(9119, 18900, 27740);
    }

    [Fact]
    public void Breaks_a_same_day_tie_by_stock_id_because_the_time_part_is_not_receipt_order()
    {
        // 031330 has the higher stock id than 110505 on the same day: the time reads as a 12-hour clock.
        var rows = new[]
        {
            Row(9173, "INW06022024031330I1"),
            Row(9158, "INW06022024110505I1"),
        };

        InwardFifo.Order(rows).Select(row => row.StockId).Should().Equal(9158, 9173);
    }

    [Fact]
    public void An_inward_number_without_a_date_goes_after_every_dated_one()
    {
        var rows = new[]
        {
            Row(5, "MANUAL-1"),
            Row(900, "INW01012026000000I1"),
            Row(3, string.Empty),
        };

        InwardFifo.Order(rows).Select(row => row.StockId).Should().Equal(900, 3, 5);
    }

    [Fact]
    public void Opening_stock_is_older_than_a_later_regular_inward()
    {
        // Seen live on OAF 24-25/OF/NF1/000037, item ITEM_IA_REG11: the 2024 opening-stock inward must go first.
        var rows = new[]
        {
            Row(29363, "INW06062025YAS1"),
            Row(24223, "OPINW01102024112816I"),
        };

        InwardFifo.Order(rows).Select(row => row.StockId).Should().Equal(24223, 29363);
    }

    // Every prefix below is on the ERP (XSTKIDEN.XSIINWD, Main_4Automation_C_Fab_2511).
    [Theory]
    [InlineData("INW05022024013943I1", 2024, 2, 5)]
    [InlineData("inw31122023235959", 2023, 12, 31)]
    [InlineData("INW06062025YAS1", 2025, 6, 6)]
    [InlineData("IW01062024125119I1", 2024, 6, 1)]
    [InlineData("LBTIW02072024BWZ1", 2024, 7, 2)]
    [InlineData("OPINW01102024112816I", 2024, 10, 1)]
    [InlineData("PVIW11122023020951I1", 2023, 12, 11)]
    [InlineData("CPIW_22092025_174429", 2025, 9, 22)]
    [InlineData("GRIW_30092025_133741", 2025, 9, 30)]
    public void Reads_the_date_from_the_inward_number(string inwardNo, int year, int month, int day)
    {
        InwardFifo.ReceivedOn(inwardNo).Should().Be(new DateOnly(year, month, day));
    }

    [Theory]
    [InlineData("INW32132024000000")]
    [InlineData("OPINW0001")]
    [InlineData("CASHINW001")]
    [InlineData("GRNWTPINW00001")]
    [InlineData("INWARDNUMBER1001")]
    [InlineData("111111WSADXSDFC")]
    [InlineData("INWARD")]
    [InlineData("")]
    public void Anything_else_has_no_date(string inwardNo)
    {
        InwardFifo.ReceivedOn(inwardNo).Should().BeNull();
    }

    private static StockRow Row(long stockId, string inwardNo) =>
        new(stockId, LineNo: 727, "ABCD", Available: 100, inwardNo);
}
