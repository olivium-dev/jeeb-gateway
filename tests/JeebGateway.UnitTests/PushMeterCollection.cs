using Xunit;

namespace JeebGateway.UnitTests;

// MeterListener sees every Add() on the process-wide meter, so these classes must not
// run in parallel with each other or one test's counter lands in another's snapshot.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PushMeterCollection
{
    public const string Name = "push-handover-meter";
}
