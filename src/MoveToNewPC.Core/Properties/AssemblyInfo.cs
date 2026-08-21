using System.Reflection;
using System.Runtime.InteropServices;

// One assembly-info file for the whole product. The App, the test harness and the tools all
// compile the Core sources in (see MoveToNewPC.App.csproj for why), so a second copy of
// these attributes anywhere would be a duplicate-attribute error.
[assembly: AssemblyTitle("Move to New PC")]
[assembly: AssemblyDescription("Moves user profile files from an old Windows PC to a new one.")]
[assembly: AssemblyProduct("Move to New PC")]
[assembly: AssemblyCopyright("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]
[assembly: AssemblyVersion("0.6.0.0")]
[assembly: AssemblyFileVersion("0.6.0.0")]
