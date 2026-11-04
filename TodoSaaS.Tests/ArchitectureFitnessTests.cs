using NetArchTest.Rules;
using TodoSaaS.Api.Models;
using Xunit;

namespace TodoSaaS.Tests;

public class ArchitectureFitnessTests
{
    private static readonly System.Reflection.Assembly ApiAssembly = typeof(Tenant).Assembly;

    [Fact]
    public void DomainModels_ShouldNotDependOn_DataLayer()
    {
        // Fitness Function: Domain Models must remain pure and free from data persistence coupling
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .ResideInNamespace("TodoSaaS.Api.Models")
            .ShouldNot()
            .HaveDependencyOn("TodoSaaS.Api.Data")
            .GetResult();

        Assert.True(result.IsSuccessful, "Domain models must not depend on Data layer.");
    }

    [Fact]
    public void MultiTenantEntities_MustImplement_IMustHaveTenant()
    {
        // Fitness Function: Prevent accidental cross-tenant data leaks by requiring IMustHaveTenant on tenant-scoped models
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .HaveName("TodoItem")
            .Or()
            .HaveName("AuditLog")
            .Should()
            .ImplementInterface(typeof(IMustHaveTenant))
            .GetResult();

        Assert.True(result.IsSuccessful, "Tenant-scoped entities must implement IMustHaveTenant.");
    }

    [Fact]
    public void Consumers_ShouldResideIn_ConsumersNamespace()
    {
        // Fitness Function: MassTransit consumers must be cleanly segregated in Consumers namespace
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .HaveNameEndingWith("Consumer")
            .Should()
            .ResideInNamespace("TodoSaaS.Api.Consumers")
            .GetResult();

        Assert.True(result.IsSuccessful, "Consumers must reside in TodoSaaS.Api.Consumers namespace.");
    }

    [Fact]
    public void Services_ShouldResideIn_ServicesNamespace()
    {
        // Fitness Function: Application and domain services must reside in Services namespace
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .HaveNameEndingWith("Service")
            .And()
            .DoNotHaveName("ITokenService")
            .Should()
            .ResideInNamespace("TodoSaaS.Api.Services")
            .GetResult();

        Assert.True(result.IsSuccessful, "Services must reside in TodoSaaS.Api.Services namespace.");
    }
}
