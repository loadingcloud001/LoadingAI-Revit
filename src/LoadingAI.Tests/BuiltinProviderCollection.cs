using Xunit;

namespace LoadingAI.Tests;

/// <summary>
/// Tests that touch process-wide built-in provider state (gateway URL, quota
/// ticket, packed-key source) must not run alongside each other.
/// </summary>
[CollectionDefinition("builtin-provider", DisableParallelization = true)]
public class BuiltinProviderCollection
{
}
