namespace BwbAlarmWatcher.Tests;

public class TvControlStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan AutoOffAfter = TimeSpan.FromMinutes(30);

    private readonly TvControlState sut = new();

    [Fact]
    public void MarkTurnedOnByService_SetsFlagAndDeadline()
    {
        sut.MarkTurnedOnByService(Now, AutoOffAfter);

        Assert.True(sut.TurnedOnByService);
        Assert.Equal(Now + AutoOffAfter, sut.AutoOffAt);
    }

    [Fact]
    public void IsAutoOffDue_BeforeDeadline_ReturnsFalse()
    {
        sut.MarkTurnedOnByService(Now, AutoOffAfter);

        Assert.False(sut.IsAutoOffDue(Now + AutoOffAfter - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void IsAutoOffDue_AtDeadline_ReturnsTrue()
    {
        sut.MarkTurnedOnByService(Now, AutoOffAfter);

        Assert.True(sut.IsAutoOffDue(Now + AutoOffAfter));
    }

    [Fact]
    public void IsAutoOffDue_NotTurnedOnByService_ReturnsFalse()
    {
        Assert.False(sut.IsAutoOffDue(Now.AddDays(1)));
    }

    [Fact]
    public void ExtendAutoOff_TurnedOnByService_MovesDeadline()
    {
        sut.MarkTurnedOnByService(Now, AutoOffAfter);
        var later = Now + TimeSpan.FromMinutes(20);

        sut.ExtendAutoOff(later, AutoOffAfter);

        Assert.Equal(later + AutoOffAfter, sut.AutoOffAt);
    }

    [Fact]
    public void ExtendAutoOff_NotTurnedOnByService_DoesNothing()
    {
        sut.ExtendAutoOff(Now, AutoOffAfter);

        Assert.False(sut.TurnedOnByService);
        Assert.Null(sut.AutoOffAt);
    }

    [Fact]
    public void Reset_ClearsFlagAndDeadline()
    {
        sut.MarkTurnedOnByService(Now, AutoOffAfter);

        sut.Reset();

        Assert.False(sut.TurnedOnByService);
        Assert.Null(sut.AutoOffAt);
    }
}
