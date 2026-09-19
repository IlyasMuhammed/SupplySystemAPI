using System.Runtime.CompilerServices;

namespace SMS.Modules.Logistics.Tests;

internal static class TestAssemblySetup
{
    /// <summary>
    /// QuestPDF refuses to render until a licence tier is declared. <c>Program.cs</c> does it at
    /// application startup, which tests never reach — so it is declared here too, once per test
    /// assembly, rather than in every document test.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize() =>
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
}
