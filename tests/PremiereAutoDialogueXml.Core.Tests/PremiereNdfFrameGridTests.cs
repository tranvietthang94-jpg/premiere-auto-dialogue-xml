using PremiereAutoDialogueXml.Core.Timing;
using PremiereAutoDialogueXml.Core.Xml;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class PremiereNdfFrameGridTests
{
    [TestMethod]
    [DataRow(24, 2_000)]
    [DataRow(25, 1_920)]
    [DataRow(30, 1_600)]
    public void Create_ProvidesExactSampleAndTickGrid(int frameRate, int samplesPerFrame)
    {
        var grid = PremiereNdfFrameGrid.Create(frameRate);

        Assert.AreEqual(frameRate, grid.FramesPerSecond);
        Assert.AreEqual(48_000, grid.AudioSampleRate);
        Assert.AreEqual(samplesPerFrame, grid.SamplesPerFrame);
        Assert.AreEqual(samplesPerFrame, grid.FrameToSample(1));
        Assert.AreEqual(PremiereTimeMath.TicksPerSecond / frameRate, grid.FrameToTicks(1));
    }

    [TestMethod]
    [DataRow(24)]
    [DataRow(25)]
    [DataRow(30)]
    public void Conversion_PreservesLargeWholeFrameBoundaries(int frameRate)
    {
        const long frame = 10_000_000;
        var grid = PremiereNdfFrameGrid.Create(frameRate);

        var sample = grid.FrameToSample(frame);
        var ticks = grid.FrameToTicks(frame);

        Assert.AreEqual(frame, grid.SampleToFrameFloor(sample));
        Assert.AreEqual(frame, grid.SampleToFrameCeiling(sample));
        Assert.AreEqual(sample, grid.TicksToSampleFloor(ticks));
        Assert.AreEqual(sample, grid.TicksToSampleCeiling(ticks));
    }

    [TestMethod]
    [DataRow(24)]
    [DataRow(25)]
    [DataRow(30)]
    public void Conversion_FloorsStartsAndCeilsEndsAtSubframeSamples(int frameRate)
    {
        var grid = PremiereNdfFrameGrid.Create(frameRate);
        var lastSampleInFirstFrame = grid.SamplesPerFrame - 1;

        Assert.AreEqual(0, grid.SampleToFrameFloor(lastSampleInFirstFrame));
        Assert.AreEqual(1, grid.SampleToFrameCeiling(lastSampleInFirstFrame));
        Assert.AreEqual(1, grid.SampleToFrameFloor(grid.SamplesPerFrame));
        Assert.AreEqual(1, grid.SampleToFrameCeiling(grid.SamplesPerFrame));
    }

    [TestMethod]
    [DataRow(24)]
    [DataRow(25)]
    [DataRow(30)]
    public void Conversion_MapsPremiereSubframeTicksWithoutRateSpecificRounding(int frameRate)
    {
        const long sourceFrame = 25;
        var grid = PremiereNdfFrameGrid.Create(frameRate);
        var twoFifthsFrameTicks = grid.FrameToTicks(1) * 2 / 5;
        var ticks = checked(grid.FrameToTicks(sourceFrame) + twoFifthsFrameTicks);
        var expectedSample = checked(
            grid.FrameToSample(sourceFrame) +
            grid.SamplesPerFrame * 2 / 5);

        Assert.AreEqual(expectedSample, grid.TicksToSampleFloor(ticks));
        Assert.AreEqual(expectedSample, grid.TicksToSampleCeiling(ticks));
    }

    [TestMethod]
    [DataRow(23)]
    [DataRow(29)]
    [DataRow(60)]
    public void Create_RejectsRateOutsideFirstPhase12Gate(int frameRate)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PremiereNdfFrameGrid.Create(frameRate));
    }

    [TestMethod]
    public void Create_RejectsAudioSampleRateOutsideCurrentRoutingContract()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PremiereNdfFrameGrid.Create(25, 44_100));
    }

    [TestMethod]
    public void Conversion_RejectsNegativeInputsAndFinalOverflow()
    {
        var grid = PremiereNdfFrameGrid.Create(24);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => grid.FrameToSample(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => grid.SampleToFrameFloor(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => grid.FrameToTicks(-1));
        Assert.ThrowsExactly<OverflowException>(() => grid.FrameToTicks(long.MaxValue));
    }
}
