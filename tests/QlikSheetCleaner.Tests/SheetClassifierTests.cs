using QlikSheetCleaner.Models;
using QlikSheetCleaner.Processing;
using Xunit;

namespace QlikSheetCleaner.Tests;

public class SheetClassifierTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void Unpublished_app_is_scenario_A(bool allow, bool published, bool approved)
    {
        var c = new SheetClassifier(allow).Classify(appPublished: false, published, approved);
        Assert.Equal(Scenario.A, c.Scenario);
        Assert.Equal(DeleteAction.EngineDestroy, c.Action);
    }

    [Fact]
    public void Personal_sheet_in_published_app_is_scenario_B()
    {
        var c = new SheetClassifier(false).Classify(true, false, false);
        Assert.Equal(Scenario.B, c.Scenario);
        Assert.Equal(DeleteAction.QrsDelete, c.Action);
    }

    [Fact]
    public void Community_sheet_is_skipped_by_default()
    {
        var c = new SheetClassifier(false).Classify(true, true, false);
        Assert.Equal(Scenario.C, c.Scenario);
        Assert.Equal(DeleteAction.Skip, c.Action);
        Assert.Equal(SheetClassifier.CommunitySheetReason, c.SkipReason);
    }

    [Fact]
    public void Community_sheet_is_unpublished_then_deleted_when_allowed()
    {
        var c = new SheetClassifier(true).Classify(true, true, false);
        Assert.Equal(Scenario.C, c.Scenario);
        Assert.Equal(DeleteAction.QrsUnpublishThenDelete, c.Action);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void Base_sheet_is_always_skipped_even_when_community_delete_allowed(bool allow, bool published)
    {
        var c = new SheetClassifier(allow).Classify(true, published, sheetApproved: true);
        Assert.Equal(Scenario.D, c.Scenario);
        Assert.Equal(DeleteAction.Skip, c.Action);
        Assert.Equal(SheetClassifier.BaseSheetReason, c.SkipReason);
    }
}
