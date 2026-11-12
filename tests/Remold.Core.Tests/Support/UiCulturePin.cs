using System.Runtime.CompilerServices;
using Remold.Core.Text;

namespace Remold.Core.Tests.Support;

/// <summary>Selects English before any test executes, so every assertion on on-screen text reads the English
/// catalog whatever the machine's display language is.</summary>
internal static class UiCulturePin
{
    [ModuleInitializer]
    internal static void PinEnglish() => Catalog.Apply(Catalog.English);
}
