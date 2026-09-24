using FluentAssertions;
using NewHorizon.Automation.Domain;
using NewHorizon.Automation.Domain.Jobs;

namespace NewHorizon.Automation.UnitTests.Domain;

public sealed class IdempotencyKeyTests
{
    [Fact]
    public void The_same_document_and_workflow_always_produce_the_same_key()
    {
        var first = IdempotencyKey.For("SalesOrder", "SO-123", "SJO");
        var second = IdempotencyKey.For("SalesOrder", "SO-123", "SJO");

        second.Should().Be(first);
    }

    [Theory]
    [InlineData("salesorder", "so-123", "sjo")]
    [InlineData("SalesOrder", " SO-123", "SJO")]
    [InlineData(" SalesOrder ", "SO-123 ", " SJO")]
    public void Casing_and_padding_do_not_create_a_second_key(
        string documentType,
        string documentId,
        string workflowType)
    {
        // The ERP push and the reconciliation poll may format the same identifier differently.
        // If those produced different keys the safety net would duplicate every job it re-reported.
        var canonical = IdempotencyKey.For("SalesOrder", "SO-123", "SJO");

        IdempotencyKey.For(documentType, documentId, workflowType).Should().Be(canonical);
    }

    [Theory]
    [InlineData("PurchaseOrder", "SO-123", "SJO")]
    [InlineData("SalesOrder", "SO-124", "SJO")]
    [InlineData("SalesOrder", "SO-123", "OAF")]
    public void Any_differing_component_produces_a_different_key(
        string documentType,
        string documentId,
        string workflowType)
    {
        // Notably the workflow type: one sales order may legitimately have both an SJO and an
        // OAF run, and they must not collide.
        var baseline = IdempotencyKey.For("SalesOrder", "SO-123", "SJO");

        IdempotencyKey.For(documentType, documentId, workflowType).Should().NotBe(baseline);
    }

    [Fact]
    public void Components_cannot_be_confused_by_moving_a_delimiter()
    {
        // "AB|C" and "A|BC" must not hash alike; the separator is not part of any component.
        var first = IdempotencyKey.For("SalesOrder", "SO", "123");
        var second = IdempotencyKey.For("SalesOrder", "SO|123", "SJO");

        first.Should().NotBe(second);
    }

    [Fact]
    public void The_key_is_a_fixed_width_lowercase_hash()
    {
        var key = IdempotencyKey.For("SalesOrder", "SO-123", "SJO");

        key.Value.Should().HaveLength(IdempotencyKey.Length);
        key.Value.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_components_are_rejected(string blank)
    {
        var create = () => IdempotencyKey.For("SalesOrder", blank, "SJO");

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_stored_key_of_the_wrong_width_is_rejected()
    {
        var rehydrate = () => IdempotencyKey.FromStoredValue("too-short");

        rehydrate.Should().Throw<DomainException>();
    }

    [Fact]
    public void A_stored_key_round_trips()
    {
        var original = IdempotencyKey.For("SalesOrder", "SO-123", "SJO");

        IdempotencyKey.FromStoredValue(original.Value).Should().Be(original);
    }
}
