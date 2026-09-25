using FluentAssertions;
using NewHorizon.Automation.ErpClient.Flows.IssueToShopFloor;

namespace NewHorizon.Automation.UnitTests.Flows.IssueToShopFloor;

public sealed class IssueAllocatorTests
{
    [Fact]
    public void Takes_everything_from_the_first_warehouse_then_the_rest_from_the_next()
    {
        // The worked example: 22 required, WH1 has 12, WH2 has 200 → 12 + 10.
        var item = Item("RM-100", required: 22);

        var allocation = IssueAllocator.Allocate([(item, [Stock(1, "WH1", 12), Stock(2, "WH2", 200)])]);

        allocation.Shortages.Should().BeEmpty();
        allocation.Lines.Select(line => (line.WarehouseCode, line.Quantity))
            .Should().Equal(("WH1", 12m), ("WH2", 10m));
    }

    [Fact]
    public void Stops_at_the_first_warehouse_when_it_covers_the_requirement()
    {
        var allocation = IssueAllocator.Allocate([(Item("RM-100", required: 5), [Stock(1, "WH1", 12), Stock(2, "WH2", 200)])]);

        allocation.Lines.Should().ContainSingle().Which.Quantity.Should().Be(5m);
    }

    [Fact]
    public void Reports_a_shortage_with_what_was_available()
    {
        var allocation = IssueAllocator.Allocate([(Item("RM-100", required: 30), [Stock(1, "WH1", 12), Stock(2, "WH2", 8)])]);

        allocation.Shortages.Should().ContainSingle()
            .Which.Should().Be(new IssueShortage("RM-100", 11, Required: 30m, Available: 20m));
    }

    [Fact]
    public void An_item_with_no_stock_at_all_is_a_shortage_of_everything()
    {
        var allocation = IssueAllocator.Allocate([(Item("RM-100", required: 4), [])]);

        allocation.Lines.Should().BeEmpty();
        allocation.Shortages.Should().ContainSingle().Which.Available.Should().Be(0m);
    }

    [Fact]
    public void A_stock_row_shared_by_two_lines_is_not_promised_twice()
    {
        // The same item at two CBOM positions is offered the same stock row. The second line may only
        // have what the first left, or the ERP would refuse the save.
        var shared = Stock(1, "WH1", 10);
        var first = Item("RM-100", required: 6, randomNumber: 11);
        var second = Item("RM-100", required: 6, randomNumber: 12);

        var allocation = IssueAllocator.Allocate([(first, [shared]), (second, [shared, Stock(2, "WH2", 50)])]);

        allocation.Shortages.Should().BeEmpty();
        allocation.Lines.Where(line => line.RandomNumber == 12).Select(line => (line.WarehouseCode, line.Quantity))
            .Should().Equal(("WH1", 4m), ("WH2", 2m));
    }

    [Fact]
    public void Issues_the_pending_quantity_when_it_is_less_than_the_requirement()
    {
        // Part already issued: the screen's Fill issues min(pending, required).
        var item = Item("RM-100", required: 22) with { PendingQuantity = 7 };

        var allocation = IssueAllocator.Allocate([(item, [Stock(1, "WH1", 100)])]);

        allocation.Lines.Should().ContainSingle().Which.Quantity.Should().Be(7m);
    }

    private static IssueItem Item(string itemCode, decimal required, int randomNumber = 11) =>
        new(itemCode, SjoId: 5341, WoId: 900, randomNumber, required, PendingQuantity: required, InwardRequired: false, BarcodeRequired: false, Remarks: string.Empty);

    private static StockRow Stock(long stockId, string warehouse, decimal available) =>
        new(stockId, LineNo: stockId * 10, warehouse, available);
}
