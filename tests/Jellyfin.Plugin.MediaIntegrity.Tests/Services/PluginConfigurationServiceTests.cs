using Jellyfin.Plugin.MediaIntegrity.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaIntegrity.Tests.Services;

public sealed class PluginConfigurationServiceTests
{
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void NonFiniteOrNegativeDurationTolerance_IsRejected(double value)
    {
        var service = new PluginConfigurationService(NullLogger<PluginConfigurationService>.Instance);
        Assert.Throws<InvalidOperationException>(() => service.Validate(new PluginConfiguration { DurationToleranceSeconds = value }));
    }

    [Theory]
    [InlineData(nameof(PluginConfiguration.StreamDurationToleranceSeconds))]
    [InlineData(nameof(PluginConfiguration.StreamStartTimeToleranceSeconds))]
    public void NonFiniteTimelineTolerance_IsRejected(string propertyName)
    {
        var configuration = new PluginConfiguration();
        typeof(PluginConfiguration).GetProperty(propertyName)!
            .SetValue(configuration, double.NaN);

        var service = new PluginConfigurationService(
            NullLogger<PluginConfigurationService>.Instance);

        Assert.Throws<InvalidOperationException>(
            () => service.Validate(configuration));
    }

    [Fact]
    public void Defaults_AreConservative()
    {
        var configuration = new PluginConfiguration();
        Assert.True(configuration.DryRun);
        Assert.Equal(1, configuration.MaxRepairsPerRun);
        Assert.True(configuration.KeepBackups);
        Assert.True(configuration.ValidateFullPacketPass);
        Assert.False(configuration.AllowRepairOfCorrupted);
        Assert.Equal("/media", configuration.SourceRoot);
        Assert.Equal("/repair-media", configuration.RepairRoot);
        Assert.Equal("/repair-backups", configuration.BackupRoot);
        Assert.Equal("/cache/media-integrity", configuration.TempRoot);
        Assert.Equal(0.05, configuration.StreamDurationToleranceSeconds);
        Assert.Equal(0.01, configuration.StreamStartTimeToleranceSeconds);
    }
}
