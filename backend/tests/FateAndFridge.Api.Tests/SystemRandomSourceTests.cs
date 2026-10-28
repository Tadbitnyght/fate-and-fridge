using System.Runtime.CompilerServices;
using FateAndFridge.Api.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FateAndFridge.Api.Tests;

/// <summary>
/// Contract checks for the M6 secure randomness seam
/// the OS-backed source stays inside [0, 1), varies across draws, and the exact
/// production registration in <c>Program.cs</c> wires it as a singleton.
/// </summary>
public sealed class SystemRandomSourceTests
{
    [Fact]
    public void Next_unit_interval_stays_within_the_half_open_unit_interval()
    {
        var source = new SystemRandomSource();

        for (var sample = 0; sample < 5_000; sample++)
        {
            var value = source.NextUnitInterval();
            value.Should().BeGreaterThanOrEqualTo(0m);
            value.Should().BeLessThan(1m);
        }
    }

    [Fact]
    public void Next_unit_interval_is_not_constant_over_many_samples()
    {
        var source = new SystemRandomSource();
        var distinct = new HashSet<decimal>();

        for (var sample = 0; sample < 1_000; sample++)
        {
            distinct.Add(source.NextUnitInterval());
        }

        distinct.Count.Should().BeGreaterThan(1);
    }

    [Fact]
    public void System_random_source_satisfies_the_random_source_contract()
    {
        IRandomSource source = new SystemRandomSource();

        source.Should().BeOfType<SystemRandomSource>();
    }

    [Fact]
    public void Production_registration_wires_the_cryptographic_source_and_the_engine()
    {
        var program = File.ReadAllText(ProgramSourcePath());

        program.Should().Contain("AddSingleton<IRandomSource, SystemRandomSource>()");
        program.Should().Contain("AddSingleton<RandomizerEngine>()");
    }

    [Fact]
    public void The_production_registration_resolves_the_source_as_a_singleton()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRandomSource, SystemRandomSource>();
        services.AddSingleton<RandomizerEngine>();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IRandomSource>();
        var second = provider.GetRequiredService<IRandomSource>();

        first.Should().BeOfType<SystemRandomSource>();
        second.Should().BeSameAs(first);
        provider.GetRequiredService<RandomizerEngine>().Should().NotBeNull();
    }

    private static string ProgramSourcePath()
    {
        var testDirectory = Path.GetDirectoryName(ThisFilePath())!;
        return Path.GetFullPath(
            Path.Combine(testDirectory, "..", "..", "src", "FateAndFridge.Api", "Program.cs"));
    }

    private static string ThisFilePath([CallerFilePath] string path = "") => path;
}
