using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public void CallingAddResilientWorkerKitTwice_MergesJobs_InsteadOfLosingThem()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // A library module registers its job...
        services.AddResilientWorkerKit(kit =>
            kit.AddJob<DelegateJob>("module-job", j => j.WithInterval(TimeSpan.FromMinutes(5))));

        // ...and the application registers its own separately.
        services.AddResilientWorkerKit(kit =>
            kit.AddJob<DelegateJob>("app-job", j => j.WithInterval(TimeSpan.FromMinutes(1))));

        services.AddScoped(_ => new DelegateJob((_, _) => Task.CompletedTask));
        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IJobRegistry>();
        Assert.Equal(2, registry.Jobs.Count);
        Assert.NotNull(registry.Find("module-job"));
        Assert.NotNull(registry.Find("app-job"));
    }

    [Fact]
    public void CallingAddResilientWorkerKitTwice_SharesOneOptionsInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddResilientWorkerKit(kit => kit.Options.HostInstanceId = "first");
        services.AddResilientWorkerKit(kit =>
        {
            Assert.Equal("first", kit.Options.HostInstanceId); // sees the earlier configuration
            kit.Options.ShutdownGracePeriod = TimeSpan.FromSeconds(7);
        });

        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<WorkerKitOptions>();
        Assert.Equal("first", options.HostInstanceId);
        Assert.Equal(TimeSpan.FromSeconds(7), options.ShutdownGracePeriod);
    }

    [Fact]
    public void DuplicateJobIdsAcrossSeparateCalls_StillFailFast()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientWorkerKit(kit =>
            kit.AddJob<DelegateJob>("same-id", j => j.WithInterval(TimeSpan.FromMinutes(5))));
        services.AddResilientWorkerKit(kit =>
            kit.AddJob<DelegateJob>("same-id", j => j.WithInterval(TimeSpan.FromMinutes(1))));

        using var provider = services.BuildServiceProvider();

        Assert.Throws<JobConfigurationException>(() => provider.GetRequiredService<IJobRegistry>());
    }

    [Fact]
    public void EngineHostedService_IsRegisteredAfterAnythingTheCallbackAdds()
    {
        // Hosted services start in registration order. A store provider registers its schema
        // initializer from inside the callback, and it must run before the engine's first job —
        // otherwise the engine queries tables that do not exist yet.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientWorkerKit(kit =>
        {
            kit.Services.AddHostedService<InitializerStub>();
            kit.AddJob<DelegateJob>("job", j => j.WithInterval(TimeSpan.FromMinutes(5)));
        });

        var hostedServices = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        var initializerIndex = hostedServices.FindIndex(d => d.ImplementationType == typeof(InitializerStub));
        var engineIndex = hostedServices.FindIndex(d => d.ImplementationFactory is not null);

        Assert.True(initializerIndex >= 0, "the callback's hosted service must be registered");
        Assert.True(engineIndex > initializerIndex,
            $"the engine must start last; initializer at {initializerIndex}, engine at {engineIndex}");
    }

    [Fact]
    public void EngineStaysLast_EvenWhenALaterCallRegistersAnInitializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientWorkerKit(kit =>
            kit.AddJob<DelegateJob>("first", j => j.WithInterval(TimeSpan.FromMinutes(5))));
        services.AddResilientWorkerKit(kit => kit.Services.AddHostedService<InitializerStub>());

        var hostedServices = services.Where(d => d.ServiceType == typeof(IHostedService)).ToList();
        var initializerIndex = hostedServices.FindIndex(d => d.ImplementationType == typeof(InitializerStub));
        var engineIndex = hostedServices.FindIndex(d => d.ImplementationFactory is not null);

        Assert.True(engineIndex > initializerIndex,
            $"the engine must still start last; initializer at {initializerIndex}, engine at {engineIndex}");
        Assert.Equal(1, hostedServices.Count(d => d.ImplementationFactory is not null));
    }

    private sealed class InitializerStub : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void TwoJobsBackedByTheSameType_RegisterTheTypeOnce()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddResilientWorkerKit(kit =>
        {
            kit.AddJob<DelegateJob>("job-a", j => j.WithInterval(TimeSpan.FromMinutes(5)));
            kit.AddJob<DelegateJob>("job-b", j => j.WithInterval(TimeSpan.FromMinutes(5)));
        });

        Assert.Equal(1, services.Count(d => d.ServiceType == typeof(DelegateJob)));
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
