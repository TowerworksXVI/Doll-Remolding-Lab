using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Remold.Core.Tests.Support;

/// <summary>
/// A test whose body runs on its own <see cref="PumpedUiThread"/>, the way the window's code runs on the
/// window's thread: the body, every continuation of its awaits, and everything the page under test
/// dispatches all take turns on ONE thread. The page gets that pump as its dispatch (see
/// <see cref="PumpedUiThread.Current"/>), so a redraw a worker asks for — a plan landing, a read settling —
/// runs when the body next yields, never beside a read the body is in the middle of.
///
/// <para>That is the contract the app has and the one an inline dispatcher cannot give a test: inline, a
/// worker's dispatch runs ON the worker, concurrently with the test thread, and a row read mid-refill fails
/// once in a few hundred runs. A body that blocks its own thread while waiting for something that needs it
/// hangs here exactly as it would hang the app.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("Remold.Core.Tests.Support.UiFactDiscoverer", "Remold.Core.Tests")]
public sealed class UiFactAttribute : FactAttribute { }

/// <summary>The theory twin of <see cref="UiFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
[XunitTestCaseDiscoverer("Remold.Core.Tests.Support.UiTheoryDiscoverer", "Remold.Core.Tests")]
public sealed class UiTheoryAttribute : TheoryAttribute { }

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class UiFactDiscoverer : FactDiscoverer
{
    private readonly IMessageSink _diagnosticMessageSink;

    public UiFactDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink)
        => _diagnosticMessageSink = diagnosticMessageSink;

    protected override IXunitTestCase CreateTestCase(ITestFrameworkDiscoveryOptions discoveryOptions,
        ITestMethod testMethod, IAttributeInfo factAttribute)
        => new UiTestCase(_diagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), testMethod);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class UiTheoryDiscoverer : TheoryDiscoverer
{
    public UiTheoryDiscoverer(IMessageSink diagnosticMessageSink) : base(diagnosticMessageSink) { }

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo theoryAttribute,
        object[] dataRow)
    {
        yield return new UiTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(), testMethod,
            dataRow);
    }

    protected override IEnumerable<IXunitTestCase> CreateTestCasesForTheory(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo theoryAttribute)
    {
        yield return new UiTheoryTestCase(DiagnosticMessageSink, discoveryOptions.MethodDisplayOrDefault(),
            TestMethodDisplayOptions.None, testMethod);
    }
}

public sealed class UiTestCase : XunitTestCase
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public UiTestCase() { }

    public UiTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay,
        ITestMethod testMethod, object?[]? testMethodArguments = null)
        : base(diagnosticMessageSink, defaultMethodDisplay, TestMethodDisplayOptions.None, testMethod,
            testMethodArguments) { }

    public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus,
        object[] constructorArguments, ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
        => UiTestCaseRunner.RunTest(this, DisplayName, SkipReason, constructorArguments, TestMethodArguments,
            messageBus, aggregator, cancellationTokenSource);
}

public sealed class UiTheoryTestCase : XunitTheoryTestCase
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public UiTheoryTestCase() { }

    public UiTheoryTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod) { }

    public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus,
        object[] constructorArguments, ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
        => UiTestCaseRunner.RunTest(this, DisplayName, SkipReason, constructorArguments, TestMethodArguments,
            messageBus, aggregator, cancellationTokenSource);
}

internal sealed class UiTestCaseRunner : XunitTestCaseRunner
{
    private UiTestCaseRunner(IXunitTestCase testCase, string displayName, string skipReason,
        object[] constructorArguments, object[] testMethodArguments, IMessageBus messageBus,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
        : base(testCase, displayName, skipReason, constructorArguments, testMethodArguments, messageBus,
            aggregator, cancellationTokenSource) { }

    /// <summary>The xunit worker blocks for the test's length so the runner's concurrency throttle counts this
    /// test as the one running — the body runs on the pump, not on the worker, and an awaited handoff would
    /// free the worker to start another class alongside.</summary>
    public static Task<RunSummary> RunTest(IXunitTestCase testCase, string displayName, string skipReason,
        object[] constructorArguments, object[] testMethodArguments, IMessageBus messageBus,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    {
        var runner = new UiTestCaseRunner(testCase, displayName, skipReason, constructorArguments,
            testMethodArguments, messageBus, aggregator, cancellationTokenSource);
        var summary = Task.Run(runner.RunAsync).GetAwaiter().GetResult();
        return Task.FromResult(summary);
    }

    protected override XunitTestRunner CreateTestRunner(ITest test, IMessageBus messageBus, Type testClass,
        object[] constructorArguments, MethodInfo testMethod, object[] testMethodArguments, string skipReason,
        IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator,
        CancellationTokenSource cancellationTokenSource)
        => new UiTestRunner(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments,
            skipReason, beforeAfterAttributes, aggregator, cancellationTokenSource);

    private sealed class UiTestRunner : XunitTestRunner
    {
        public UiTestRunner(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments,
            MethodInfo testMethod, object[] testMethodArguments, string skipReason,
            IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator,
            CancellationTokenSource cancellationTokenSource)
            : base(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, skipReason,
                beforeAfterAttributes, aggregator, cancellationTokenSource) { }

        protected override async Task<decimal> InvokeTestMethodAsync(ExceptionAggregator aggregator)
        {
            using var pump = new PumpedUiThread();
            var done = new TaskCompletionSource<decimal>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The whole invocation — the class's construction, the body, its disposal — runs on the pump; the
            // invoker's own await of an async body resumes there through the pump's context.
            pump.Post(async () =>
            {
                try
                {
                    done.SetResult(await new XunitTestInvoker(Test, MessageBus, TestClass, ConstructorArguments,
                        TestMethod, TestMethodArguments, BeforeAfterAttributes, aggregator,
                        CancellationTokenSource).RunAsync());
                }
                catch (Exception e) { done.SetException(e); }
            });
            return await done.Task;
        }
    }
}
