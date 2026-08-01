using Microsoft.Extensions.DependencyInjection;
using ResilientWorkerKit.Registration;
using ResilientWorkerKit.UnitTests.TestInfrastructure;

namespace ResilientWorkerKit.UnitTests.Registration;

public class RegistrationValidationTests
{
    [Fact]
    public void UnknownTimeZone_FailsAtBuild()
    {
        var ex = Assert.Throws<JobConfigurationException>(() =>
            RunnerHarness.Definition(b => b.WithInterval(TimeSpan.FromMinutes(1)).WithTimeZone("Mars/Olympus")));
        Assert.Contains("Mars/Olympus", ex.Message);
    }

    [Fact]
    public void SettingTwoSchedules_Fails()
    {
        Assert.Throws<JobConfigurationException>(() =>
            RunnerHarness.Definition(b => b
                .WithInterval(TimeSpan.FromMinutes(1))
                .DailyAt(new TimeOnly(2, 0))));
    }

    [Fact]
    public void RunIfWithinTolerance_RequiresATolerance()
    {
        Assert.Throws<JobConfigurationException>(() =>
            RunnerHarness.Definition(b => b
                .WithInterval(TimeSpan.FromMinutes(1))
                .WithMisfirePolicy(MisfirePolicy.RunIfWithinTolerance)));
    }

    [Fact]
    public void RescheduleFromNow_IsRejectedForCalendarSchedules()
    {
        Assert.Throws<JobConfigurationException>(() =>
            RunnerHarness.Definition(b => b
                .DailyAt(new TimeOnly(2, 0))
                .WithMisfirePolicy(MisfirePolicy.RescheduleFromNow)));
    }

    [Fact]
    public void RescheduleFromNow_IsAllowedForIntervalSchedules()
    {
        var definition = RunnerHarness.Definition(b => b
            .WithInterval(TimeSpan.FromMinutes(1))
            .WithMisfirePolicy(MisfirePolicy.RescheduleFromNow));
        Assert.Equal(MisfirePolicy.RescheduleFromNow, definition.MisfirePolicy);
    }

    [Fact]
    public void DefaultMisfirePolicies_DependOnScheduleType()
    {
        Assert.Equal(MisfirePolicy.Skip,
            RunnerHarness.Definition(b => b.WithInterval(TimeSpan.FromMinutes(1))).MisfirePolicy);
        Assert.Equal(MisfirePolicy.RescheduleFromNow,
            RunnerHarness.Definition(b => b.WithFixedDelay(TimeSpan.FromMinutes(1))).MisfirePolicy);
        Assert.Equal(MisfirePolicy.RunImmediatelyOnce,
            RunnerHarness.Definition(b => b.OnceAt(DateTimeOffset.UtcNow.AddDays(1))).MisfirePolicy);
    }

    [Theory]
    [InlineData(-1)]
    public void NegativeRetryCount_Fails(int retries)
    {
        Assert.Throws<JobConfigurationException>(() =>
            RunnerHarness.Definition(b => b.WithRetryCount(retries)));
    }

    [Fact]
    public void NonPositiveTimeout_Fails()
    {
        Assert.Throws<JobConfigurationException>(() =>
            RunnerHarness.Definition(b => b.WithTimeout(TimeSpan.Zero)));
    }

    [Fact]
    public void DuplicateJobIds_FailInTheRegistry()
    {
        var a = RunnerHarness.Definition(jobId: "same-id");
        var b = RunnerHarness.Definition(jobId: "same-id");

        Assert.Throws<JobConfigurationException>(() => new JobRegistry([a, b]));
    }

    [Fact]
    public void AddResilientWorkerKit_RegistersEverything_AndValidatesLazily()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientWorkerKit(kit =>
        {
            kit.AddJob<DelegateJob>("job-a", j => j.WithInterval(TimeSpan.FromMinutes(1)));
        });
        services.AddScoped(_ => new DelegateJob((_, _) => Task.CompletedTask));

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IJobRegistry>();
        Assert.Single(registry.Jobs);
        Assert.NotNull(provider.GetRequiredService<IJobHealthTracker>());
        Assert.NotNull(provider.GetRequiredService<IManualJobTrigger>());
    }

    [Fact]
    public void InvalidJobConfiguration_SurfacesWhenTheRegistryIsBuilt()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientWorkerKit(kit =>
        {
            kit.AddJob<DelegateJob>("bad-job", j => j.WithCron("0 2 * * *").WithTimeZone("Not/AZone"));
        });

        using var provider = services.BuildServiceProvider();

        Assert.Throws<JobConfigurationException>(() => provider.GetRequiredService<IJobRegistry>());
    }
}
