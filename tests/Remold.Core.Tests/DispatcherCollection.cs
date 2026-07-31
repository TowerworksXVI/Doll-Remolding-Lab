using Xunit;

namespace Remold.Core.Tests;

/// <summary>Serializes the test classes that drive the shared Avalonia <c>Dispatcher.UIThread</c> (workbench VM
/// preview/edit tests). xUnit runs distinct classes in parallel by default; two of them draining and posting to
/// the one dispatcher concurrently races its thread affinity. Membership in this collection makes them run
/// sequentially without disabling parallelism for the rest of the suite.</summary>
[CollectionDefinition("Dispatcher")]
public sealed class DispatcherCollection { }
