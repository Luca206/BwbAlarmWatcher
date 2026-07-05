using BwbAlarmWatcher.Alarms;
using BwbAlarmWatcher.Configuration;
using BwbAlarmWatcher.Tv;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace BwbAlarmWatcher.Tests;

public class WorkerTests
{
    private readonly Mock<IAlarmSource> alarmSourceMock = new();
    private readonly Mock<ITvStatusSensor> tvStatusSensorMock = new();
    private readonly Mock<ITvPowerActor> tvPowerActorMock = new();
    private readonly FakeTimeProvider fakeTime = new();

    private readonly Worker sut;

    public WorkerTests()
    {
        tvPowerActorMock.Setup(a => a.TurnOnAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        tvPowerActorMock.Setup(a => a.TurnOffAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        tvStatusSensorMock.Setup(s => s.IsTvReachableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetAlarmFeed();

        sut = new Worker(
            alarmSourceMock.Object,
            tvStatusSensorMock.Object,
            tvPowerActorMock.Object,
            fakeTime,
            Options.Create(new WorkerOptions()),
            NullLogger<Worker>.Instance);
    }

    private void SetAlarmFeed(params string[] alarmIds)
        => alarmSourceMock
            .Setup(a => a.GetActiveAlarmsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(alarmIds.Select(id => new ActiveAlarm(id)).ToArray());

    [Fact]
    public async Task ProcessOneCycleAsync_NoAlarms_DoesNothing()
    {
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvStatusSensorMock.Verify(s => s.IsTvReachableAsync(It.IsAny<CancellationToken>()), Times.Never);
        tvPowerActorMock.Verify(a => a.TurnOnAsync(It.IsAny<CancellationToken>()), Times.Never);
        tvPowerActorMock.Verify(a => a.TurnOffAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessOneCycleAsync_NewAlarmAndTvOff_TurnsOnTvAndArmsAutoOff()
    {
        SetAlarmFeed("A1");

        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvPowerActorMock.Verify(a => a.TurnOnAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(sut.State.TurnedOnByService);
        Assert.Equal(fakeTime.GetUtcNow() + TimeSpan.FromMinutes(30), sut.State.AutoOffAt);
    }

    [Fact]
    public async Task ProcessOneCycleAsync_NewAlarmAndTvManuallyOn_DoesNotArmAutoOff()
    {
        SetAlarmFeed("A1");
        tvStatusSensorMock.Setup(s => s.IsTvReachableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await sut.ProcessOneCycleAsync(CancellationToken.None);
        fakeTime.Advance(TimeSpan.FromMinutes(31));
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvPowerActorMock.Verify(a => a.TurnOnAsync(It.IsAny<CancellationToken>()), Times.Never);
        tvPowerActorMock.Verify(a => a.TurnOffAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.False(sut.State.TurnedOnByService);
    }

    [Fact]
    public async Task ProcessOneCycleAsync_SameAlarmInConsecutiveCycles_TurnsOnOnlyOnce()
    {
        SetAlarmFeed("A1");

        await sut.ProcessOneCycleAsync(CancellationToken.None);
        await sut.ProcessOneCycleAsync(CancellationToken.None);
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvPowerActorMock.Verify(a => a.TurnOnAsync(It.IsAny<CancellationToken>()), Times.Once);
        tvStatusSensorMock.Verify(s => s.IsTvReachableAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessOneCycleAsync_AutoOffElapsed_TurnsOffTv()
    {
        SetAlarmFeed("A1");
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        fakeTime.Advance(TimeSpan.FromMinutes(31));
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvPowerActorMock.Verify(a => a.TurnOffAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(sut.State.TurnedOnByService);
    }

    [Fact]
    public async Task ProcessOneCycleAsync_AutoOffNotElapsed_DoesNotTurnOff()
    {
        SetAlarmFeed("A1");
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        fakeTime.Advance(TimeSpan.FromMinutes(29));
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvPowerActorMock.Verify(a => a.TurnOffAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessOneCycleAsync_FurtherAlarmWhileOnByService_ExtendsAutoOffWithoutProbing()
    {
        SetAlarmFeed("A1");
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        fakeTime.Advance(TimeSpan.FromMinutes(20));
        SetAlarmFeed("A1", "A2");
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        // Deadline moved from t+30 to t+20+30.
        Assert.Equal(fakeTime.GetUtcNow() + TimeSpan.FromMinutes(30), sut.State.AutoOffAt);
        // The ping probe must not run again: the TV is on because the service turned it on.
        tvStatusSensorMock.Verify(s => s.IsTvReachableAsync(It.IsAny<CancellationToken>()), Times.Once);

        // Original deadline passes without a turn-off …
        fakeTime.Advance(TimeSpan.FromMinutes(15));
        await sut.ProcessOneCycleAsync(CancellationToken.None);
        tvPowerActorMock.Verify(a => a.TurnOffAsync(It.IsAny<CancellationToken>()), Times.Never);

        // … the extended one turns the TV off.
        fakeTime.Advance(TimeSpan.FromMinutes(16));
        await sut.ProcessOneCycleAsync(CancellationToken.None);
        tvPowerActorMock.Verify(a => a.TurnOffAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessOneCycleAsync_ApiThrows_AutoOffStillRuns()
    {
        SetAlarmFeed("A1");
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        alarmSourceMock
            .Setup(a => a.GetActiveAlarmsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));
        fakeTime.Advance(TimeSpan.FromMinutes(31));

        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvPowerActorMock.Verify(a => a.TurnOffAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(sut.State.TurnedOnByService);
    }

    [Fact]
    public async Task ProcessOneCycleAsync_TurnOnFails_RetriesNextCycle()
    {
        SetAlarmFeed("A1");
        tvPowerActorMock.SetupSequence(a => a.TurnOnAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);

        await sut.ProcessOneCycleAsync(CancellationToken.None);
        Assert.False(sut.State.TurnedOnByService);

        await sut.ProcessOneCycleAsync(CancellationToken.None);
        Assert.True(sut.State.TurnedOnByService);

        await sut.ProcessOneCycleAsync(CancellationToken.None);
        tvPowerActorMock.Verify(a => a.TurnOnAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProcessOneCycleAsync_TurnOffFails_RetriesNextCycle()
    {
        SetAlarmFeed("A1");
        tvPowerActorMock.SetupSequence(a => a.TurnOffAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false)
            .ReturnsAsync(true);
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        fakeTime.Advance(TimeSpan.FromMinutes(31));
        await sut.ProcessOneCycleAsync(CancellationToken.None);
        Assert.True(sut.State.TurnedOnByService);

        await sut.ProcessOneCycleAsync(CancellationToken.None);
        Assert.False(sut.State.TurnedOnByService);
        tvPowerActorMock.Verify(a => a.TurnOffAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ProcessOneCycleAsync_AlarmClosedAndReopened_TreatedAsNewAlarm()
    {
        SetAlarmFeed("A1");
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        // Alarm closes: it disappears from the active feed and tracking is pruned.
        SetAlarmFeed();
        fakeTime.Advance(TimeSpan.FromMinutes(31));
        await sut.ProcessOneCycleAsync(CancellationToken.None);
        Assert.Empty(sut.TrackedAlarmIds);

        // The same extid reappearing is a new operation and must switch the TV on again.
        SetAlarmFeed("A1");
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvPowerActorMock.Verify(a => a.TurnOnAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task TryProcessOneCycleAsync_TurnOffThrows_LoopSurvives()
    {
        SetAlarmFeed("A1");
        await sut.ProcessOneCycleAsync(CancellationToken.None);

        tvPowerActorMock.Setup(a => a.TurnOffAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        fakeTime.Advance(TimeSpan.FromMinutes(31));

        // Must not throw.
        await sut.TryProcessOneCycleAsync(CancellationToken.None);
    }
}
