using Mtp.Platform.Core;

namespace Mtp.Platform.Core.Tests;

public sealed class EventGroupLayoutTests
{
    [Fact]
    public void Default_position_stacks_registered_groups_from_bottom_left_upward()
    {
        var result = EventGroupLayout.Calculate(new(0, 0, 1000, 800), [new("first", 320, 100), new("second", 320, 120)]);
        Assert.True(result.IsSuccess);
        Assert.Equal(new TaskbarDipRect(16, 684, 320, 100), result.Value!.Placements[0].Bounds);
        Assert.Equal(new TaskbarDipRect(16, 556, 320, 120), result.Value.Placements[1].Bounds);
        Assert.All(result.Value.Placements, value => Assert.False(value.Overlaps));
        Assert.Empty(result.Value.Reasons);
    }

    [Theory]
    [InlineData(HintPosition.Default, 16, 684)]
    [InlineData(HintPosition.TopLeft, 16, 16)]
    [InlineData(HintPosition.TopCenter, 340, 16)]
    [InlineData(HintPosition.TopRight, 664, 16)]
    [InlineData(HintPosition.BottomLeft, 16, 684)]
    [InlineData(HintPosition.BottomCenter, 340, 684)]
    [InlineData(HintPosition.BottomRight, 664, 684)]
    [InlineData(HintPosition.Center, 340, 350)]
    [InlineData(HintPosition.LowerCenter, 340, 590)]
    public void Global_supported_positions_are_resolved_inside_the_safe_work_area(HintPosition position, double x, double y)
    {
        var result = EventGroupLayout.Calculate(new(0, 0, 1000, 800), [new("one", 320, 100)], position);
        Assert.True(result.IsSuccess);
        Assert.Equal(new TaskbarDipRect(x, y, 320, 100), Assert.Single(result.Value!.Placements).Bounds);
    }

    [Fact]
    public void Host_resolved_per_group_preference_can_override_global_position()
    {
        var result = EventGroupLayout.Calculate(new(-1000, -800, 1000, 800),
            [new("left", 320, 100), new("right", 320, 100, HintPosition.TopRight)]);
        Assert.True(result.IsSuccess);
        Assert.Equal(new TaskbarDipRect(-984, -116, 320, 100), result.Value!.Placements[0].Bounds);
        Assert.Equal(new TaskbarDipRect(-336, -784, 320, 100), result.Value.Placements[1].Bounds);
    }

    [Fact]
    public void Higher_priority_hint_is_avoided_without_a_close_decision()
    {
        var hint = new TaskbarDipRect(16, 684, 320, 100);
        var result = EventGroupLayout.Calculate(new(0, 0, 1000, 800), [new("event", 320, 100)], higherPriority: [hint]);
        Assert.True(result.IsSuccess);
        Assert.Equal(new TaskbarDipRect(16, 576, 320, 100), Assert.Single(result.Value!.Placements).Bounds);
        Assert.False(result.Value.Placements[0].Overlaps);
        Assert.Contains("HigherPriorityAvoided", result.Value.Reasons);
    }

    [Fact]
    public void Crowded_groups_remain_bounded_and_present_with_explicit_overlap()
    {
        var groups = Enumerable.Range(0, 10).Select(i => new EventGroupMeasure("event" + i, 500, 400)).ToArray();
        var result = EventGroupLayout.Calculate(new(0, 0, 200, 200), groups);
        Assert.True(result.IsSuccess);
        Assert.Equal(10, result.Value!.Placements.Count);
        Assert.All(result.Value.Placements, value =>
        {
            Assert.Equal(new TaskbarDipRect(16, 16, 168, 168), value.Bounds);
            Assert.True(value.Scrolls);
            Assert.True(value.Overlaps);
        });
        Assert.Contains("EventGroupsOverlap", result.Value.Reasons);
        Assert.Contains("EventViewportClamped", result.Value.Reasons);
    }

    [Fact]
    public void Higher_priority_full_screen_overlay_does_not_delete_or_move_events_offscreen()
    {
        var result = EventGroupLayout.Calculate(new(0, 0, 400, 300), [new("event", 320, 100)],
            higherPriority: [new(0, 0, 400, 300)]);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Value!.Placements);
        Assert.True(result.Value.Placements[0].Overlaps);
        Assert.Contains("HigherPriorityOverlay", result.Value.Reasons);
    }

    [Fact]
    public void Oversized_group_scrolls_with_a_reachable_minimum_operating_area()
    {
        var result = EventGroupLayout.Calculate(new(0, 0, 128, 112), [new("event", 320, 1000)]);
        Assert.True(result.IsSuccess);
        Assert.Equal(new TaskbarDipRect(16, 16, 96, 80), Assert.Single(result.Value!.Placements).Bounds);
        Assert.True(result.Value.Placements[0].Scrolls);
        Assert.False(EventGroupLayout.Calculate(new(0, 0, 127, 112), [new("event", 320, 100)]).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(new(0, 0, 128, 111), [new("event", 320, 100)]).IsSuccess);
    }

    [Fact]
    public void Untrusted_measures_obstacles_and_counts_are_validated_before_layout()
    {
        var work = new TaskbarDipRect(0, 0, 1000, 800);
        Assert.False(EventGroupLayout.Calculate(work, null!).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work, [null!]).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work, [new("", 320, 100)]).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work, [new(new string('k', 2049), 320, 100)]).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work, [new("a", 320, 100), new("a", 320, 100)]).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work, Enumerable.Range(0, 11).Select(i => new EventGroupMeasure("a" + i, 320, 100)).ToArray()).IsSuccess);
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, 0, -1, 1e10 })
        {
            Assert.False(EventGroupLayout.Calculate(work, [new("a", invalid, 100)]).IsSuccess);
            Assert.False(EventGroupLayout.Calculate(work, [new("a", 320, invalid)]).IsSuccess);
        }
        Assert.False(EventGroupLayout.Calculate(work, [new("a", 320, 100, (HintPosition)999)]).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work, [], (HintPosition)999).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work with { X = double.NaN }, []).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work, [], higherPriority: [new(0, 0, double.PositiveInfinity, 100)]).IsSuccess);
        Assert.False(EventGroupLayout.Calculate(work, [], higherPriority: Enumerable.Repeat(work, 65).ToArray()).IsSuccess);
        Assert.Empty(EventGroupLayout.Calculate(work, []).Value!.Placements);
    }

    [Fact]
    public void Top_position_grows_downward_and_unrelated_outside_obstacles_do_not_shift_it()
    {
        var result = EventGroupLayout.Calculate(new(0, 0, 1000, 800), [new("a", 320, 100), new("b", 320, 100)],
            HintPosition.TopLeft, [new(-500, -500, 100, 100)]);
        Assert.True(result.IsSuccess);
        Assert.Equal(new TaskbarDipRect(16, 16, 320, 100), result.Value!.Placements[0].Bounds);
        Assert.Equal(new TaskbarDipRect(16, 124, 320, 100), result.Value.Placements[1].Bounds);
        Assert.Empty(result.Value.Reasons);
    }
}
