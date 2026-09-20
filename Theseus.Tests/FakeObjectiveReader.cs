using Theseus.Services.Duty;

namespace Theseus.Tests;

/// <summary>Objective state on demand, for tests that only care which objective a step belongs to.</summary>
internal sealed class FakeObjectiveReader : IObjectiveReader
{
    public int CurrentIndex { get; set; }

    public DutyObjectiveSnapshot Read() => new([], CurrentIndex, 0, Available: true);
}
