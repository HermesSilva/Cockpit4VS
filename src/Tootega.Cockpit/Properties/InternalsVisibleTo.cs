using System.Runtime.CompilerServices;

// The extension's types are internal on purpose — nothing outside the VSIX should bind to
// them. The test assembly is the one exception, so the protocol, parser and aggregators can
// be tested directly instead of through the IDE.
[assembly: InternalsVisibleTo("Tootega.Cockpit.Tests")]
